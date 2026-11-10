using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AiAgentStudio.Api.Identity;
using AiAgentStudio.Api.Notifications;
using AiAgentStudio.Api.Services;
using AiAgentStudio.Api.Studio;
using AiAgentStudio.Api.Tickets;
using AiAgentStudio.Api.Vocabulary;
using AiAgentStudio.Api.Workflow;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AiAgentStudio.Api.Submissions;

/// <summary>
/// 負責的 CIM Engineer 處理評估結果（spec 5.3 第 4–6 點）：接受條件、標為實作中。
/// 每個動作都寫 ticket_event；只認使用者 JWT，且必須是該單負責的 CIM Engineer。業務規則全在這個檔案。
/// </summary>
[ApiController]
[Route("api/tickets/{ticketId}/specs/{specId}")]
[Authorize]
public class ToolSpecHandlingController(ICurrentUserService user, IdentityService identity, StudioDb db, WorkflowDraftService drafts, StudioConfigProvider config, IUserDirectory directory, TimeProvider time, McpToolCatalog mcp, VocabularyService vocab, PendingTermService pendingTerms) : ControllerBase
{
    static readonly System.Text.Json.JsonSerializerOptions J = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>載入單與 Tool Spec 並檢查權限。回傳 (ctx, error)；error 非 null 時直接回傳。</summary>
    async Task<((RequirementTicket T, ToolSpecRecord Rec, string Me)? Ctx, IActionResult? Error)> LoadAsync(string ticketId, string specId, CancellationToken ct)
    {
        if (user.EmpNo is not { } empNo) return (null, Forbid());
        var jwt = Request.Headers.Authorization.ToString()["Bearer ".Length..].Trim();
        if (await identity.GetAsync(empNo, jwt, ct) is not { IsCimEngineer: true }) return (null, Forbid());
        var t = await db.Tickets.FirstOrDefaultAsync(x => x.Id == ticketId, ct);
        if (t is null) return (null, NotFound());
        if (t.CimEngineerEmpNo != empNo) return (null, Forbid());
        var rec = await db.ToolSpecRecords.FirstOrDefaultAsync(r => r.TicketId == ticketId && r.SpecId == specId, ct);
        if (rec is null) return (null, NotFound());
        if (t.Status != TicketStatus.Submitted) return (null, Problem("需求單目前不在評估／實作階段。", statusCode: 409));
        return ((t, rec, empNo), null);
    }

    void Log(string ticketId, string me, string type, string specId, string from, string to) =>
        db.TicketEvents.Add(new TicketEvent { TicketId = ticketId, Type = type, ActorEmpNo = me, At = time.GetUtcNow(), FromValue = from, ToValue = to, Reason = specId });

    /// <summary>接受附條件的評估結果：條件記錄在 Tool Spec 上，視同可行；System Owner 之後不能再改回覆。</summary>
    [HttpPost("accept-condition")]
    public async Task<IActionResult> AcceptCondition(string ticketId, string specId, CancellationToken ct)
    {
        var (ctx, err) = await LoadAsync(ticketId, specId, ct);
        if (ctx is not var (t, rec, me)) return err!;
        if (rec.State != ToolSpecState.Conditional) return Problem("只有附條件的 Tool Spec 可以接受條件。", statusCode: 409);
        var review = await db.FeasibilityReviews.Where(h => h.TicketId == ticketId && h.SpecId == specId && h.Revision == rec.CurrentRevision).OrderByDescending(h => h.Id).FirstAsync(ct);
        rec.AcceptedCondition = review.Condition;
        Log(ticketId, me, "ToolSpecAcceptCondition", specId, rec.State, ToolSpecState.ConditionAccepted);
        rec.State = ToolSpecState.ConditionAccepted; rec.Handled = true;
        await db.SaveChangesAsync(ct);
        return Ok(new { specId, state = rec.State });
    }

    public sealed record ReviseRequest(JsonObject? Fields);

    /// <summary>
    /// 修改後送去重評（不可行或附條件）。修改的起點是目前凍結的 revision 內容（不是草稿）：只套用 fields 給的欄位，
    /// 產生新的待評估 revision（只有這份 spec 換 revision，其他不動，spec 6.5）與一筆新的 submission 快照（沿用最新的 Workflow Definition），
    /// 並讓草稿跟上，避免之後重開時草稿蓋掉這次的修改。通知（重新）送評對象 System Owner。
    /// ponytail: 不重跑 Naming Check；重開 Interview（#30）送出時會重新檢查。
    /// </summary>
    [HttpPost("revise")]
    public async Task<IActionResult> Revise(string ticketId, string specId, [FromBody] ReviseRequest body, CancellationToken ct)
    {
        var (ctx, err) = await LoadAsync(ticketId, specId, ct);
        if (ctx is not var (t, rec, me)) return err!;
        if (rec.State is not (ToolSpecState.Infeasible or ToolSpecState.Conditional)) return Problem("只有不可行或附條件的 Tool Spec 可以修改後重評。", statusCode: 409);
        if (body.Fields is not { Count: > 0 } fields) return Problem("沒有要修改的欄位。", statusCode: 400);

        var current = await db.ToolSpecRevisions.AsNoTracking().FirstAsync(r => r.TicketId == ticketId && r.SpecId == specId && r.Revision == rec.CurrentRevision, ct);
        var baseline = JsonNode.Parse(current.ContentJson)!.AsObject();
        baseline.Remove("system_owner_name");
        var next = (JsonObject)baseline.DeepClone();
        foreach (var (k, v) in fields.Where(p => p.Key is not ("id" or "system_owner_name" or "naming_check"))) next[k] = v?.DeepClone();
        if (JsonNode.DeepEquals(baseline, next)) return Problem("內容沒有改動。", statusCode: 400);
        if (next["kind"]?.GetValue<string>() != rec.Kind) return Problem("不能變更 Tool Spec 的 Kind。", statusCode: 400);
        if (ToolSpecRules.Validate(next, config.Current.Systems.Keys) is { } rule) return Problem(rule, statusCode: 400);
        if (ToolSpecRules.Missing(next) is { Length: > 0 } missing) return Problem($"缺少必填欄位 {string.Join("、", missing)}。", statusCode: 400);
        var owner = next["system_owner"]!.GetValue<string>().Trim();
        if (await directory.FindAsync(owner, ct) is not { } ownerUser) return Problem($"查無 System Owner 工號 {owner}。", statusCode: 400);
        next["system_owner"] = owner;
        next["system_owner_name"] = ownerUser.Name;

        var now = time.GetUtcNow();
        var latest = await db.Submissions.Where(s => s.TicketId == ticketId).OrderByDescending(s => s.Number).FirstAsync(ct);
        var number = latest.Number + 1;
        db.Submissions.Add(new Submission { TicketId = ticketId, Number = number, WorkflowJson = latest.WorkflowJson, SubmittedByEmpNo = me, At = now });
        rec.CurrentRevision++;
        db.ToolSpecRevisions.Add(new ToolSpecRevision { TicketId = ticketId, SpecId = specId, Revision = rec.CurrentRevision, SubmissionNumber = number, ContentJson = next.ToJsonString(J) });
        Log(ticketId, me, "ToolSpecRevise", specId, rec.State, ToolSpecState.Pending);
        rec.State = ToolSpecState.Pending; rec.Handled = false; rec.AcceptedCondition = null;
        await drafts.SyncSpecAsync(ticketId, specId, next);
        EmailOutbox.Queue(db, EmailEvents.Resubmitted, owner, ticketId, new { number, specs = new[] { specId } }, now);
        await db.SaveChangesAsync(ct);
        return Ok(new { specId, state = rec.State, revision = rec.CurrentRevision });
    }

    /// <summary>
    /// 刪除不可行的 Tool Spec：Workflow Definition 中引用它的步驟解除引用（變成人工步驟），並新增一筆 submission 快照記錄調整後的流程。
    /// revision 與 feasibility_review 歷史保留。全部不可行時刪到最後一份會被擋下，須改走 Cancellation（不自動取消）。
    /// </summary>
    [HttpDelete]
    public async Task<IActionResult> Delete(string ticketId, string specId, CancellationToken ct)
    {
        var (ctx, err) = await LoadAsync(ticketId, specId, ct);
        if (ctx is not var (t, rec, me)) return err!;
        if (rec.State != ToolSpecState.Infeasible) return Problem("只有不可行的 Tool Spec 可以刪除。", statusCode: 409);
        if (!await db.ToolSpecRecords.AnyAsync(r => r.TicketId == ticketId && r.SpecId != specId, ct))
            return Problem("這是最後一份 Tool Spec，無法刪除；全部不可行時請改走 Cancellation（取消需求單）。", statusCode: 409);

        await drafts.SyncSpecAsync(ticketId, specId, null);
        var workflow = (await drafts.GetAsync(ticketId)).Steps;
        var now = time.GetUtcNow();
        var number = await db.Submissions.Where(s => s.TicketId == ticketId).MaxAsync(s => s.Number, ct) + 1;
        db.Submissions.Add(new Submission { TicketId = ticketId, Number = number, WorkflowJson = workflow.ToJsonString(J), SubmittedByEmpNo = me, At = now });
        Log(ticketId, me, "ToolSpecDelete", specId, rec.State, "Deleted");
        db.ToolSpecRecords.Remove(rec);
        await db.SaveChangesAsync(ct);
        return Ok(new { specId });
    }

    /// <summary>手動標為實作中（可行或已接受條件）。不串 Gitea。</summary>
    [HttpPost("implementing")]
    public async Task<IActionResult> Implementing(string ticketId, string specId, CancellationToken ct)
    {
        var (ctx, err) = await LoadAsync(ticketId, specId, ct);
        if (ctx is not var (t, rec, me)) return err!;
        if (rec.State is not (ToolSpecState.Feasible or ToolSpecState.ConditionAccepted)) return Problem("只有可行或已接受條件的 Tool Spec 可以標為實作中。", statusCode: 409);
        Log(ticketId, me, "ToolSpecImplementing", specId, rec.State, ToolSpecState.Implementing);
        rec.State = ToolSpecState.Implementing; rec.Handled = true;
        await db.SaveChangesAsync(ct);
        return Ok(new { specId, state = rec.State });
    }

    public sealed record ImplementedRequest(string? ServerKey, string? ToolName);

    /// <summary>
    /// 標為已實作（spec 5.3 第 7–8 點、6.4）：取得實際 MCP 工具介面並做 Interface Compliance Check。
    /// 沒通過回 200 + passed=false（附 failures），狀態不變；通過才存 interface_snapshot、標為已實作、寫事件。
    /// 若這是最後一份未實作的 Tool Spec，需求單隨之為已完成，同一次 SaveChanges 排入 email（Business User 與 CIM Engineer）。
    /// </summary>
    [HttpPost("implemented")]
    public async Task<IActionResult> MarkImplemented(string ticketId, string specId, [FromBody] ImplementedRequest body, CancellationToken ct)
    {
        var (ctx, err) = await LoadAsync(ticketId, specId, ct);
        if (ctx is not var (t, rec, me)) return err!;
        if (rec.State != ToolSpecState.Implementing) return Problem("只有實作中的 Tool Spec 可以標為已實作。", statusCode: 409);
        if (string.IsNullOrWhiteSpace(body.ServerKey) || string.IsNullOrWhiteSpace(body.ToolName)) return Problem("需提供 serverKey 與 toolName。", statusCode: 400);

        var failures = new System.Collections.Generic.List<string>();
        var warnings = new System.Collections.Generic.List<string>();
        var server = (await mcp.ListAsync(ct)).FirstOrDefault(s => s.Server == body.ServerKey);
        if (server is null) failures.Add($"找不到 MCP server '{body.ServerKey}'。");
        else if (server.Error is not null) failures.Add($"無法連線 MCP server '{body.ServerKey}'：{server.Error}");
        var tool = server?.Tools.FirstOrDefault(x => x.Name == body.ToolName);
        if (server is { Error: null } && tool is null) failures.Add($"MCP server '{body.ServerKey}' 沒有工具 '{body.ToolName}'。");

        if (tool is not null)
        {
            var spec = JsonNode.Parse((await db.ToolSpecRevisions.AsNoTracking().FirstAsync(r => r.TicketId == ticketId && r.SpecId == specId && r.Revision == rec.CurrentRevision, ct)).ContentJson)!;
            IReadOnlyList<GlossaryEntry>? entries = null; IReadOnlyCollection<string>? pending = null;
            try
            {
                entries = (await vocab.GetAsync(ct)).Entries;
                pending = (await pendingTerms.ActiveAsync(entries, ct)).Select(x => x.Term).ToList();
            }
            catch (VocabularyException e) { warnings.Add(ToolSpecNaming.NoCache(e.Message)); }
            var result = InterfaceComplianceChecker.Check(rec.Kind, spec["inputs"], spec["outputs"],
                JsonNode.Parse(tool.InputSchema.GetRawText()), tool.OutputSchema is { } o ? JsonNode.Parse(o.GetRawText()) : null, entries, pending);
            failures.AddRange(result.Failures); warnings.AddRange(result.Warnings);
        }
        if (failures.Count > 0 || tool is null) return Ok(new { specId, passed = false, failures, warnings });

        var now = time.GetUtcNow();
        db.InterfaceSnapshots.Add(new InterfaceSnapshot
        {
            TicketId = ticketId, SpecId = specId, Revision = rec.CurrentRevision, ServerKey = body.ServerKey!, ToolName = body.ToolName!,
            InputSchemaJson = tool.InputSchema.GetRawText(), OutputSchemaJson = tool.OutputSchema?.GetRawText(), CheckedByEmpNo = me, At = now,
        });
        Log(ticketId, me, "ToolSpecImplemented", specId, rec.State, ToolSpecState.Implemented);
        rec.State = ToolSpecState.Implemented; rec.Handled = true;
        var states = await db.ToolSpecRecords.Where(r => r.TicketId == ticketId && r.SpecId != specId).Select(r => r.State).ToListAsync(ct);
        states.Add(rec.State);
        var completed = TicketStatusDeriver.Derive(t.Status, states) == TicketStatus.Completed;
        if (completed)
        {
            var ids = await db.ToolSpecRecords.Where(r => r.TicketId == ticketId).Select(r => r.SpecId).ToListAsync(ct);
            foreach (var who in new[] { t.BusinessUserEmpNo, t.CimEngineerEmpNo }.Distinct())
                EmailOutbox.Queue(db, EmailEvents.Completed, who, ticketId, new { specs = ids }, now);
        }
        await db.SaveChangesAsync(ct);
        return Ok(new { specId, passed = true, state = rec.State, completed, failures, warnings });
    }
}
