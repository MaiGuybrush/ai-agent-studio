using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AiAgentStudio.Api.Notifications;
using AiAgentStudio.Api.Services;
using AiAgentStudio.Api.Tickets;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AiAgentStudio.Api.Submissions;

public sealed record ReviewReplyRequest(string? Result, string? Reason, string? Condition, decimal? EstimatedDays);

/// <summary>
/// Feasibility Review 回覆（spec 9）。只有被指派的 System Owner（目前 revision 內容的 system_owner 工號）能看評估畫面與回覆；
/// 這裡沒有任何修改 Tool Spec 的路徑。業務規則全在這個檔案。
/// </summary>
[ApiController]
[Route("api/reviews")]
[Authorize]
public class FeasibilityReviewController(ICurrentUserService user, StudioDb db, TimeProvider time) : ControllerBase
{
    static readonly string[] Results = [ToolSpecState.Feasible, ToolSpecState.Conditional, ToolSpecState.Infeasible];
    /// <summary>仍可由 System Owner 回覆／修改的狀態（再往後是 CIM Engineer 的處理）。</summary>
    static readonly string[] Replyable = [ToolSpecState.Pending, .. Results];

    /// <summary>回覆欄位規則（spec 9 表格）。回傳錯誤訊息清單，空表示通過；不適用的欄位由呼叫端捨棄。</summary>
    public static List<string> Validate(ReviewReplyRequest r)
    {
        var errors = new List<string>();
        if (r.Result is null || !Results.Contains(r.Result)) { errors.Add("結果必填，且須為 Feasible、Conditional 或 Infeasible。"); return errors; }
        if (r.Result != ToolSpecState.Feasible && string.IsNullOrWhiteSpace(r.Reason)) errors.Add("附條件或不可行時，原因必填。");
        if (r.Result == ToolSpecState.Conditional && string.IsNullOrWhiteSpace(r.Condition)) errors.Add("附條件時，條件內容必填。");
        if (r.Result != ToolSpecState.Infeasible && r.EstimatedDays is not > 0) errors.Add("可行或附條件時，預估工時（人日）必填且須大於 0。");
        return errors;
    }

    /// <summary>目前使用者的待評估清單：State 為 Pending、目前 revision 指派給他的 Tool Spec。</summary>
    [HttpGet("pending")]
    public async Task<IActionResult> Pending(CancellationToken ct)
    {
        if (user.EmpNo is not { } me) return Forbid();
        // ponytail: 先取全部 Pending 再在記憶體比對工號；量大時把 system_owner 存成 tool_spec 欄位
        var pending = await db.ToolSpecRecords.AsNoTracking().Where(r => r.State == ToolSpecState.Pending && r.Kind != "Reuse").ToListAsync(ct);
        var ticketIds = pending.Select(r => r.TicketId).Distinct().ToList();
        var tickets = await db.Tickets.AsNoTracking().Where(t => ticketIds.Contains(t.Id) && t.Status == TicketStatus.Submitted).ToDictionaryAsync(t => t.Id, ct);
        var revs = await db.ToolSpecRevisions.AsNoTracking().Where(r => ticketIds.Contains(r.TicketId)).ToListAsync(ct);
        var subs = await db.Submissions.AsNoTracking().Where(s => ticketIds.Contains(s.TicketId)).ToListAsync(ct);
        var assigns = await db.ReviewAssignments.AsNoTracking().Where(a => ticketIds.Contains(a.TicketId)).ToListAsync(ct);
        var now = time.GetUtcNow();
        var items = new List<object>();
        foreach (var r in pending.Where(r => tickets.ContainsKey(r.TicketId)))
        {
            var rev = revs.First(x => x.TicketId == r.TicketId && x.SpecId == r.SpecId && x.Revision == r.CurrentRevision);
            if (SpecOwner.Resolve(rev.ContentJson, rev.Revision, assigns.Where(a => a.TicketId == r.TicketId && a.SpecId == r.SpecId)) != me) continue; // 轉派後舊 owner 不再看到
            var sub = subs.First(s => s.TicketId == r.TicketId && s.Number == rev.SubmissionNumber);
            var t = tickets[r.TicketId];
            items.Add(new
            {
                ticketId = r.TicketId, specId = r.SpecId, revision = r.CurrentRevision, kind = r.Kind,
                purpose = JsonNode.Parse(rev.ContentJson)?["purpose"],
                workflow = JsonNode.Parse(sub.WorkflowJson),
                cimEngineerEmpNo = t.CimEngineerEmpNo, cimEngineerName = t.CimEngineerName,
                businessUserEmpNo = t.BusinessUserEmpNo, businessUserName = t.BusinessUserName,
                submittedAt = sub.At, waitingDays = Math.Max(0, (now - sub.At).Days),
            });
        }
        return Ok(items.OrderByDescending(i => ((dynamic)i).waitingDays));
    }

    /// <summary>評估畫面資料：Tool Spec revision 內容、Workflow Definition（唯讀）、回覆歷史。不含對話紀錄。</summary>
    [HttpGet("{ticketId}/{specId}")]
    public async Task<IActionResult> Detail(string ticketId, string specId, CancellationToken ct)
    {
        var (ctx, err) = await LoadAsync(ticketId, specId, ct);
        if (ctx is null) return err!;
        var (t, rec, rev) = ctx.Value;
        var sub = await db.Submissions.AsNoTracking().FirstAsync(s => s.TicketId == ticketId && s.Number == rev.SubmissionNumber, ct);
        var history = await db.FeasibilityReviews.AsNoTracking().Where(h => h.TicketId == ticketId && h.SpecId == specId).OrderBy(h => h.Id).ToListAsync(ct);
        var prev = await db.ToolSpecRevisions.AsNoTracking().Where(r => r.TicketId == ticketId && r.SpecId == specId && r.Revision < rev.Revision).OrderByDescending(r => r.Revision).FirstOrDefaultAsync(ct);
        return Ok(new
        {
            // 與同一份 Tool Spec 上一個 revision 的差異（spec 6.5）；第一版為 null
            diff = prev is null ? null : new { fromRevision = prev.Revision, changes = RevisionDiff.Between(prev.ContentJson, rev.ContentJson) },
            ticketId, specId, revision = rev.Revision, kind = rec.Kind, state = rec.State, canReply = CanReply(t, rec),
            content = JsonNode.Parse(rev.ContentJson), workflow = JsonNode.Parse(sub.WorkflowJson),
            cimEngineerName = t.CimEngineerName, businessUserName = t.BusinessUserName, submittedAt = sub.At,
            waitingDays = Math.Max(0, (time.GetUtcNow() - sub.At).Days),
            history = history.Select(h => new { h.Revision, h.Result, h.Reason, h.Condition, h.EstimatedDays, reviewer = h.ReviewerEmpNo, h.At }),
        });
    }

    /// <summary>回覆或修改回覆；每次新增一筆 feasibility_review，更新 Tool Spec 狀態，並在同一次 SaveChanges 寫入通知 CIM Engineer 的 outbox。</summary>
    [HttpPut("{ticketId}/{specId}")]
    public async Task<IActionResult> Reply(string ticketId, string specId, [FromBody] ReviewReplyRequest body, CancellationToken ct)
    {
        var (ctx, err) = await LoadAsync(ticketId, specId, ct, track: true);
        if (ctx is null) return err!;
        var (t, rec, rev) = ctx.Value;
        if (!CanReply(t, rec)) return Problem(rec.Handled ? "CIM Engineer 已處理此評估結果，無法再修改回覆。" : "目前狀態無法回覆。", statusCode: 409);
        var errors = Validate(body);
        if (errors.Count > 0) return BadRequest(new { title = "回覆欄位不符規則。", errors });

        var now = time.GetUtcNow();
        var infeasible = body.Result == ToolSpecState.Infeasible;
        db.FeasibilityReviews.Add(new FeasibilityReview
        {
            TicketId = ticketId, SpecId = specId, Revision = rev.Revision, Result = body.Result!, ReviewerEmpNo = user.EmpNo!, At = now,
            Reason = string.IsNullOrWhiteSpace(body.Reason) ? null : body.Reason.Trim(),
            Condition = body.Result == ToolSpecState.Conditional ? body.Condition!.Trim() : null,
            EstimatedDays = infeasible ? null : body.EstimatedDays,
        });
        rec.State = body.Result!;
        EmailOutbox.Queue(db, EmailEvents.Replied, t.CimEngineerEmpNo, ticketId, new { specs = new[] { specId }, result = body.Result }, now);
        await db.SaveChangesAsync(ct);
        return Ok(new { specId, state = rec.State });
    }

    static bool CanReply(RequirementTicket t, ToolSpecRecord rec) => t.Status == TicketStatus.Submitted && !rec.Handled && Replyable.Contains(rec.State);

    /// <summary>找出目前 revision 並檢查呼叫者就是被指派的 System Owner；Reuse 或不存在 404，非本人 403。</summary>
    async Task<((RequirementTicket, ToolSpecRecord, ToolSpecRevision)? Ctx, IActionResult? Error)> LoadAsync(string ticketId, string specId, CancellationToken ct, bool track = false)
    {
        var recs = track ? db.ToolSpecRecords : db.ToolSpecRecords.AsNoTracking();
        var rec = await recs.FirstOrDefaultAsync(r => r.TicketId == ticketId && r.SpecId == specId, ct);
        if (rec is null || rec.Kind == "Reuse") return (null, NotFound());
        var rev = await db.ToolSpecRevisions.AsNoTracking().FirstAsync(r => r.TicketId == ticketId && r.SpecId == specId && r.Revision == rec.CurrentRevision, ct);
        var assigns = await db.ReviewAssignments.AsNoTracking().Where(a => a.TicketId == ticketId && a.SpecId == specId).ToListAsync(ct);
        if (user.EmpNo is not { } me || SpecOwner.Resolve(rev.ContentJson, rev.Revision, assigns) != me) return (null, Forbid());
        var t = await db.Tickets.AsNoTracking().FirstAsync(x => x.Id == ticketId, ct);
        return ((t, rec, rev), null);
    }
}
