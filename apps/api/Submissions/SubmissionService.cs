using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AiAgentStudio.Api.Notifications;
using AiAgentStudio.Api.Studio;
using AiAgentStudio.Api.Tickets;
using AiAgentStudio.Api.Vocabulary;
using AiAgentStudio.Api.Workflow;
using Microsoft.EntityFrameworkCore;

namespace AiAgentStudio.Api.Submissions;

/// <summary>阻擋送出的一項待處理事項。Code：no_tool_specs／missing_field／owner／new_term／rename_reason／naming_unavailable。</summary>
public sealed record Blocker(string Code, string Message, string? SpecId = null, string? Path = null);

/// <summary>處理結果：Error 為 null 表示成功，Body 是回應內容；否則 Status 是 HTTP 狀態碼、Error 是訊息。controller 只負責轉成 HTTP。</summary>
public sealed record HandleResult(int Status, string? Error, object? Body)
{
    public static HandleResult Ok(object body) => new(200, null, body);
    public static HandleResult Fail(int status, string error) => new(status, error, null);
}

/// <summary>
/// 送出前檢查、送出、送出後的處理（spec 8.5、6.5、5.3）。業務規則只在這裡；agent 沒有對應工具，也沒有 run token 能呼叫的路由。
/// 送出後 CIM Engineer 的動作（修改後重評、刪除不可行、標為已實作）也在這裡，controller 只管 HTTP 與權限。
/// </summary>
public class SubmissionService(StudioDb db, WorkflowDraftService drafts, ToolSpecNaming naming, StudioConfigProvider config, IUserDirectory directory, TimeProvider time,
    McpToolCatalog mcp, VocabularyService vocab, PendingTermService pendingTerms)
{
    static readonly System.Text.Json.JsonSerializerOptions J = JsonOpts.Relaxed;
    const string Specs = WorkflowDraft.ToolSpecsDoc;

    /// <summary>目前的阻擋清單；會先重跑 Naming Check（詞彙 sha 落後才真的重算），所以結果以最新詞彙為準。</summary>
    public async Task<List<Blocker>> BlockersAsync(string ticketId, CancellationToken ct)
    {
        var refresh = await naming.RefreshAsync(ticketId, ct);
        var specs = (await drafts.GetAsync(ticketId, Specs)).Steps.OfType<JsonObject>().ToList();
        var res = new List<Blocker>();
        if (specs.Count == 0) return [new("no_tool_specs", "還沒有任何 Tool Spec。")];
        if (refresh.Error is not null) res.Add(new("naming_unavailable", refresh.Error));

        var frozen = await FrozenSpecs.IdsAsync(db, ticketId, specs, ct); // 已實作且沒改動：以 interface_snapshot 為準，不再檢查（spec 7.3）
        foreach (var s in specs.Where(s => !frozen.Contains(s["id"]!.GetValue<string>())))
            res.AddRange(await SpecBlockersAsync(s, s["naming_check"]?["fields"] as JsonObject, ct));
        return res;
    }

    /// <summary>單一 Tool Spec 的送出條件（spec 8.5）：必填欄位、System Owner、新名詞、改名理由。送出與修改後重評共用。</summary>
    async Task<List<Blocker>> SpecBlockersAsync(JsonObject s, JsonObject? namingFields, CancellationToken ct)
    {
        var res = new List<Blocker>();
        var id = s["id"]!.GetValue<string>();
        var missing = ToolSpecRules.Missing(s).Where(k => k != "system_owner").ToArray();
        if (missing.Length > 0) res.Add(new("missing_field", $"{id}：缺少必填欄位 {string.Join("、", missing)}。", id));
        if (s["kind"]?.GetValue<string>() != ToolSpecKinds.Reuse)
        {
            var owner = s["system_owner"] is JsonValue v && v.TryGetValue<string>(out var o) ? o : null;
            if (string.IsNullOrWhiteSpace(owner)) res.Add(new("owner", $"{id}：尚未指定 System Owner。", id));
            else if (await directory.FindAsync(owner.Trim(), ct) is null) res.Add(new("owner", $"{id}：查無 System Owner 工號 {owner}。", id));
        }
        if (namingFields is null) return res;
        foreach (var (path, r) in namingFields)
        {
            var status = r?["status"]?.GetValue<string>();
            if (status == "new") res.Add(new("new_term", $"{id}：{path} 是新名詞，請確認並填寫說明。", id, path));
            else if (status == "rename" && string.IsNullOrWhiteSpace(Reason(s, path)))
                res.Add(new("rename_reason", $"{id}：{path} 建議改成 {r?["suggestion"]}，不改請填理由。", id, path));
        }
        return res;
    }

    /// <summary>目前所在階段（spec 8.2），只供畫面決定要不要展開阻擋清單：沒有步驟 1；有步驟 2；有 Tool Spec 但欄位未補齊 4；只剩阻擋事項 5；可送出 6。</summary>
    public async Task<int> StageAsync(string ticketId, IReadOnlyCollection<Blocker> blockers)
    {
        if ((await drafts.GetAsync(ticketId)).Steps.Count == 0) return 1;
        if (blockers.Any(b => b.Code == "no_tool_specs")) return 2;
        if (blockers.Any(b => b.Code == "missing_field")) return 4;
        return blockers.Count > 0 ? 5 : 6;
    }

    /// <summary>理由記在欄位所屬 spec 的 naming_reasons[欄位路徑]。</summary>
    static string? Reason(JsonObject spec, string path) =>
        spec["naming_reasons"] is JsonObject o && o[path] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>送出；呼叫端已確認權限。有阻擋事項時不動任何資料，回傳阻擋清單。</summary>
    public async Task<(int? Number, List<Blocker> Blockers)> SubmitAsync(RequirementTicket t, string empNo, CancellationToken ct)
    {
        var blockers = await BlockersAsync(t.Id, ct);
        if (blockers.Count > 0) return (null, blockers);

        var workflow = (await drafts.GetAsync(t.Id)).Steps;
        var specs = (await drafts.GetAsync(t.Id, Specs)).Steps.OfType<JsonObject>().ToList();
        await drafts.ClearHumanEditsAsync(t.Id);

        var number = (await db.Submissions.Where(x => x.TicketId == t.Id).MaxAsync(x => (int?)x.Number, ct) ?? 0) + 1;
        db.Submissions.Add(new Submission { TicketId = t.Id, Number = number, WorkflowJson = workflow.ToJsonString(J), SubmittedByEmpNo = empNo, At = time.GetUtcNow() });

        var records = await db.ToolSpecRecords.Where(x => x.TicketId == t.Id).ToListAsync(ct);
        var revisions = await db.ToolSpecRevisions.Where(x => x.TicketId == t.Id).ToListAsync(ct);
        foreach (var gone in records.Where(r => !specs.Any(s => s["id"]!.GetValue<string>() == r.SpecId))) db.ToolSpecRecords.Remove(gone); // 草稿已刪除的 spec
        var toEvaluate = new Dictionary<string, List<string>>(); // System Owner 工號 → 這次新產生 revision、需要他評估的 Tool Spec
        var finalStates = new List<string>(); // 送出後每份 Tool Spec 的狀態，用來判斷是否隨即完成
        foreach (var s in specs)
        {
            var id = s["id"]!.GetValue<string>();
            var content = (JsonObject)s.DeepClone();
            content.Remove("naming_check"); // 衍生欄位，不屬於內容
            if (content["system_owner"] is JsonValue ov && ov.TryGetValue<string>(out var ownerNo) && !string.IsNullOrWhiteSpace(ownerNo))
                content["system_owner_name"] = (await directory.FindAsync(ownerNo.Trim(), ct))?.Name;
            var rec = records.FirstOrDefault(r => r.SpecId == id);
            var last = rec is null ? null : revisions.FirstOrDefault(r => r.SpecId == id && r.Revision == rec.CurrentRevision);
            if (rec is not null && last is not null && JsonNode.DeepEquals(JsonNode.Parse(last.ContentJson), content)) { finalStates.Add(rec.State); continue; } // 沒改動：保留原 revision 與評估結果
            var kind = s["kind"]!.GetValue<string>();
            rec ??= db.ToolSpecRecords.Add(new ToolSpecRecord { TicketId = t.Id, SpecId = id }).Entity;
            rec.Kind = kind;
            rec.CurrentRevision = (rec.CurrentRevision) + 1;
            rec.Handled = false; rec.AcceptedCondition = null; // 重評：System Owner 可再回覆（spec 9）
            rec.State = kind == ToolSpecKinds.Reuse ? ToolSpecState.Implemented : ToolSpecState.Pending; // Reuse 不需要 Feasibility Review
            finalStates.Add(rec.State);
            db.ToolSpecRevisions.Add(new ToolSpecRevision { TicketId = t.Id, SpecId = id, Revision = rec.CurrentRevision, SubmissionNumber = number, ContentJson = content.ToJsonString(J) });
            if (kind != ToolSpecKinds.Reuse && content["system_owner"] is JsonValue eo && eo.TryGetValue<string>(out var evalOwner) && !string.IsNullOrWhiteSpace(evalOwner))
                (toEvaluate.TryGetValue(evalOwner.Trim(), out var l) ? l : toEvaluate[evalOwner.Trim()] = []).Add(id);
        }
        // email（spec 10.1）：每位 System Owner 一封；與下面的狀態變更同一次 SaveChanges，所以同一個交易
        foreach (var (owner, ids) in toEvaluate)
            EmailOutbox.Queue(db, number > 1 ? EmailEvents.Resubmitted : EmailEvents.Submitted, owner, t.Id, new { number, specs = ids }, time.GetUtcNow());
        db.TicketEvents.Add(new TicketEvent { TicketId = t.Id, Type = "Submit", ActorEmpNo = empNo, At = time.GetUtcNow(), FromValue = t.Status, ToValue = TicketStatus.Submitted, Reason = $"submission #{number}" });
        var statusBefore = t.Status;
        t.Status = TicketStatus.Submitted;
        QueueCompletedOnTransition(t, statusBefore, [], t.Status, finalStates, specs.Select(s => s["id"]!.GetValue<string>()).ToList()); // 只有 Reuse 的單送出即完成
        await db.SaveChangesAsync(ct);
        return (number, []);
    }

    /// <summary>
    /// 需求單由「未完成」變成「已完成」時，排入 Completed email（Business User 與 CIM Engineer，spec 5.3-8、10.1）。
    /// 前後狀態都由 Tool Spec 狀態推導；不呼叫 SaveChanges，與呼叫端的變更同一個交易。回傳是否剛完成。
    /// </summary>
    bool QueueCompletedOnTransition(RequirementTicket t, string statusBefore, IEnumerable<string> statesBefore, string statusAfter, IReadOnlyCollection<string> statesAfter, IReadOnlyCollection<string> specIds)
    {
        if (statesAfter.Count == 0 || TicketStatusDeriver.Derive(statusBefore, statesBefore) == TicketStatus.Completed
            || TicketStatusDeriver.Derive(statusAfter, statesAfter) != TicketStatus.Completed) return false;
        foreach (var who in new[] { t.BusinessUserEmpNo, t.CimEngineerEmpNo }.Distinct())
            EmailOutbox.Queue(db, EmailEvents.Completed, who, t.Id, new { specs = specIds }, time.GetUtcNow());
        return true;
    }

    // 以下是送出後 CIM Engineer 處理評估結果的動作（spec 5.3 第 4–8 點）；呼叫端（controller）已確認權限與單的狀態。

    /// <summary>寫 ticket_event（不存檔）。ticket_event 沒有 spec 欄位，spec id 記在 Reason（加欄位要 migration，v1 先不動）。</summary>
    public void Log(string ticketId, string me, string type, string specId, string from, string to) =>
        db.TicketEvents.Add(new TicketEvent { TicketId = ticketId, Type = type, ActorEmpNo = me, At = time.GetUtcNow(), FromValue = from, ToValue = to, Reason = specId });

    /// <summary>
    /// 修改後送去重評（不可行或附條件）。修改的起點是目前凍結的 revision 內容（不是草稿）：只套用 fields 給的欄位，
    /// 且須通過與送出相同的條件（spec 8.5）：必填欄位、System Owner、被改動的欄位做 Naming Check（新名詞須先確認、改名須填理由）。
    /// 產生新的待評估 revision（只有這份 spec 換 revision，spec 6.5）與一筆新的 submission 快照（沿用最新的 Workflow Definition），
    /// 並讓草稿跟上，避免之後重開時草稿蓋掉這次的修改。通知（重新）送評對象 System Owner。
    /// </summary>
    public async Task<HandleResult> ReviseAsync(RequirementTicket t, ToolSpecRecord rec, string me, JsonObject? fields, CancellationToken ct)
    {
        var (ticketId, specId) = (t.Id, rec.SpecId);
        if (rec.State is not (ToolSpecState.Infeasible or ToolSpecState.Conditional)) return HandleResult.Fail(409, "只有不可行或附條件的 Tool Spec 可以修改後重評。");
        if (fields is not { Count: > 0 }) return HandleResult.Fail(400, "沒有要修改的欄位。");

        var current = await db.ToolSpecRevisions.AsNoTracking().FirstAsync(r => r.TicketId == ticketId && r.SpecId == specId && r.Revision == rec.CurrentRevision, ct);
        var baseline = JsonNode.Parse(current.ContentJson)!.AsObject();
        baseline.Remove("system_owner_name");
        var next = (JsonObject)baseline.DeepClone();
        foreach (var (k, v) in fields.Where(p => p.Key is not ("id" or "system_owner_name" or "naming_check"))) next[k] = v?.DeepClone();
        if (JsonNode.DeepEquals(baseline, next)) return HandleResult.Fail(400, "內容沒有改動。");
        if (next["kind"]?.GetValue<string>() != rec.Kind) return HandleResult.Fail(400, "不能變更 Tool Spec 的 Kind。");
        if (ToolSpecRules.Validate(next, config.Current.Systems.Keys) is { } rule) return HandleResult.Fail(400, rule);

        var (changedNaming, namingError) = await naming.CheckChangedAsync(baseline, next, ct);
        if (namingError is not null) return HandleResult.Fail(409, namingError);
        next["id"] = specId; // Blocker 訊息用
        var blockers = await SpecBlockersAsync(next, changedNaming, ct);
        next.Remove("id");
        if (blockers.Count > 0) return HandleResult.Fail(400, string.Join(" ", blockers.Select(b => b.Message)));
        var owner = next["system_owner"]!.GetValue<string>().Trim();
        next["system_owner"] = owner;
        next["system_owner_name"] = (await directory.FindAsync(owner, ct))!.Name;

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
        return HandleResult.Ok(new { specId, state = rec.State, revision = rec.CurrentRevision });
    }

    /// <summary>
    /// 刪除不可行的 Tool Spec：Workflow Definition 中引用它的步驟解除引用（變成人工步驟），並新增一筆 submission 快照記錄調整後的流程。
    /// revision 與 feasibility_review 歷史保留。全部不可行時刪到最後一份會被擋下，須改走 Cancellation（不自動取消）。
    /// 刪完剩下的 Tool Spec 若全部已實作，需求單隨之完成並排入 email。
    /// </summary>
    public async Task<HandleResult> DeleteSpecAsync(RequirementTicket t, ToolSpecRecord rec, string me, CancellationToken ct)
    {
        var (ticketId, specId) = (t.Id, rec.SpecId);
        if (rec.State != ToolSpecState.Infeasible) return HandleResult.Fail(409, "只有不可行的 Tool Spec 可以刪除。");
        var others = await db.ToolSpecRecords.Where(r => r.TicketId == ticketId && r.SpecId != specId).Select(r => new { r.SpecId, r.State }).ToListAsync(ct);
        if (others.Count == 0)
            return HandleResult.Fail(409, "這是最後一份 Tool Spec，無法刪除；全部不可行時請改走 Cancellation（取消需求單）。");

        await drafts.SyncSpecAsync(ticketId, specId, null);
        var workflow = (await drafts.GetAsync(ticketId)).Steps;
        var now = time.GetUtcNow();
        var number = await db.Submissions.Where(s => s.TicketId == ticketId).MaxAsync(s => s.Number, ct) + 1;
        db.Submissions.Add(new Submission { TicketId = ticketId, Number = number, WorkflowJson = workflow.ToJsonString(J), SubmittedByEmpNo = me, At = now });
        Log(ticketId, me, "ToolSpecDelete", specId, rec.State, "Deleted");
        var after = others.Select(o => o.State).ToList();
        QueueCompletedOnTransition(t, t.Status, after.Append(rec.State), t.Status, after, others.Select(o => o.SpecId).ToList());
        db.ToolSpecRecords.Remove(rec);
        await db.SaveChangesAsync(ct);
        return HandleResult.Ok(new { specId });
    }

    /// <summary>
    /// 標為已實作（spec 5.3 第 7–8 點、6.4）：取得實際 MCP 工具介面（略過快取，一定是最新的）並做 Interface Compliance Check。
    /// 沒通過回 200 + passed=false（附 failures），狀態不變；通過才存 interface_snapshot、標為已實作、寫事件。
    /// 若這是最後一份未實作的 Tool Spec，需求單隨之為已完成，同一次 SaveChanges 排入 email（Business User 與 CIM Engineer）。
    /// </summary>
    public async Task<HandleResult> MarkImplementedAsync(RequirementTicket t, ToolSpecRecord rec, string me, string? serverKey, string? toolName, CancellationToken ct)
    {
        var (ticketId, specId) = (t.Id, rec.SpecId);
        if (rec.State != ToolSpecState.Implementing) return HandleResult.Fail(409, "只有實作中的 Tool Spec 可以標為已實作。");
        if (string.IsNullOrWhiteSpace(serverKey) || string.IsNullOrWhiteSpace(toolName)) return HandleResult.Fail(400, "需提供 serverKey 與 toolName。");

        var failures = new List<string>();
        var warnings = new List<string>();
        var server = await mcp.ListFreshAsync(serverKey, ct);
        if (server is null) failures.Add($"找不到 MCP server '{serverKey}'。");
        else if (server.Error is not null) failures.Add($"無法連線 MCP server '{serverKey}'：{server.Error}");
        var tool = server?.Tools.FirstOrDefault(x => x.Name == toolName);
        if (server is { Error: null } && tool is null) failures.Add($"MCP server '{serverKey}' 沒有工具 '{toolName}'。");

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
        if (failures.Count > 0 || tool is null) return HandleResult.Ok(new { specId, passed = false, failures, warnings });

        db.InterfaceSnapshots.Add(new InterfaceSnapshot
        {
            TicketId = ticketId, SpecId = specId, Revision = rec.CurrentRevision, ServerKey = serverKey!, ToolName = toolName!,
            InputSchemaJson = tool.InputSchema.GetRawText(), OutputSchemaJson = tool.OutputSchema?.GetRawText(), CheckedByEmpNo = me, At = time.GetUtcNow(),
        });
        Log(ticketId, me, "ToolSpecImplemented", specId, rec.State, ToolSpecState.Implemented);
        var others = await db.ToolSpecRecords.Where(r => r.TicketId == ticketId && r.SpecId != specId).Select(r => new { r.SpecId, r.State }).ToListAsync(ct);
        var before = others.Select(o => o.State).Append(rec.State).ToList();
        rec.State = ToolSpecState.Implemented; rec.Handled = true;
        var completed = QueueCompletedOnTransition(t, t.Status, before, t.Status, others.Select(o => o.State).Append(rec.State).ToList(), others.Select(o => o.SpecId).Append(specId).Order().ToList());
        await db.SaveChangesAsync(ct);
        return HandleResult.Ok(new { specId, passed = true, state = rec.State, completed, failures, warnings });
    }

    /// <summary>需求單的顯示狀態；一次查多張單的 Tool Spec 狀態。</summary>
    public static async Task<Dictionary<string, string>> StatusesAsync(StudioDb db, IReadOnlyCollection<RequirementTicket> tickets, CancellationToken ct)
    {
        var ids = tickets.Where(t => t.Status == TicketStatus.Submitted).Select(t => t.Id).ToList();
        var states = (await db.ToolSpecRecords.AsNoTracking().Where(r => ids.Contains(r.TicketId)).Select(r => new { r.TicketId, r.State }).ToListAsync(ct))
            .ToLookup(x => x.TicketId, x => x.State);
        return tickets.ToDictionary(t => t.Id, t => TicketStatusDeriver.Derive(t.Status, states[t.Id]));
    }

    /// <summary>系統對照表的建議 System Owner。用清單而非字典回傳：JSON 序列化會改動字典的 key 大小寫。</summary>
    public async Task<List<OwnerSuggestion>> OwnerSuggestionsAsync(CancellationToken ct)
    {
        var res = new List<OwnerSuggestion>();
        foreach (var (system, empNo) in config.Current.Systems.Where(p => p.Value != ""))
            res.Add(new(system, empNo, (await directory.FindAsync(empNo, ct))?.Name));
        return res;
    }
}

public sealed record OwnerSuggestion(string System, string EmpNo, string? Name);
