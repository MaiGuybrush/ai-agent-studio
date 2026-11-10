using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AiAgentStudio.Api.Identity;
using AiAgentStudio.Api.Services;
using AiAgentStudio.Api.Submissions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AiAgentStudio.Api.Tickets;

public record CreateTicketRequest(string? Id, string? Description, string? BusinessUserEmpNo);

[ApiController]
[Route("api/tickets")]
[Authorize]
public class TicketsController(ICurrentUserService user, IdentityService identity, IUserDirectory directory, StudioDb db, TimeProvider time) : ControllerBase
{
    async Task<UserIdentity?> WhoAsync(CancellationToken ct)
    {
        if (user.EmpNo is not { } empNo) return null; // 例如 API Key 登入，沒有 empNo
        var jwt = Request.Headers.Authorization.ToString()["Bearer ".Length..].Trim();
        return await identity.GetAsync(empNo, jwt, ct);
    }

    static bool IsResponsible(RequirementTicket t, UserIdentity me) => me.IsCimEngineer && t.CimEngineerEmpNo == me.EmpNo;

    /// <summary>權限規則集中在這裡（spec 3.3）。負責人調離 CIM 後 IsCimEngineer 為 false，自然變唯讀。</summary>
    static object PermissionsOf(RequirementTicket t, UserIdentity me)
    {
        var responsible = IsResponsible(t, me);
        return new
        {
            canCancel = responsible && t.Status != TicketStatus.Cancelled,
            canReopen = responsible && t.Status == TicketStatus.Cancelled,
            canReopenInterview = responsible && t.Status == TicketStatus.Submitted, // 已送出（含已完成）才有東西可重開
            canReassign = me.IsCimEngineer,
            canViewConversation = me.IsCimEngineer,
            canChat = responsible && t.Status == TicketStatus.Interviewing, // 送出後輸入框停用（spec 8.6）
            canSubmit = responsible && t.Status == TicketStatus.Interviewing,
        };
    }

    static object View(RequirementTicket t, UserIdentity me, string status) => new
    {
        id = t.Id, description = t.Description,
        cimEngineerEmpNo = t.CimEngineerEmpNo, cimEngineerName = t.CimEngineerName,
        businessUserEmpNo = t.BusinessUserEmpNo, businessUserName = t.BusinessUserName,
        status = TicketStatus.Label(status), statusCode = status, cancelReason = t.CancelReason, createdAt = t.CreatedAt,
        permissions = PermissionsOf(t, me),
    };

    [HttpPost]
    public async Task<IActionResult> Create(CreateTicketRequest req, CancellationToken ct)
    {
        if (await WhoAsync(ct) is not { } me) return Forbid();
        if (!me.IsCimEngineer) return Forbid();
        if (string.IsNullOrWhiteSpace(req.Id) || string.IsNullOrWhiteSpace(req.Description) || string.IsNullOrWhiteSpace(req.BusinessUserEmpNo))
            return Problem("單號、需求描述、Business User 工號皆必填。", statusCode: 400);
        var id = req.Id.Trim();
        if (await db.Tickets.AnyAsync(x => x.Id == id, ct)) return Problem($"單號 {id} 已存在。", statusCode: 409);
        var bu = await directory.FindAsync(req.BusinessUserEmpNo.Trim(), ct);
        if (bu is null) return Problem($"查無工號 {req.BusinessUserEmpNo}，無法綁定為 Business User。", statusCode: 422);
        var t = new RequirementTicket
        {
            Id = id, Description = req.Description.Trim(),
            CimEngineerEmpNo = me.EmpNo, CimEngineerName = (await directory.FindAsync(me.EmpNo, ct))?.Name ?? me.EmpNo,
            BusinessUserEmpNo = bu.EmpNo, BusinessUserName = bu.Name,
            CreatedAt = time.GetUtcNow(),
        };
        db.Tickets.Add(t);
        await db.SaveChangesAsync(ct);
        return Created($"/api/tickets/{t.Id}", View(t, me, await StatusOf(t, ct)));
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        if (await WhoAsync(ct) is not { } me) return Forbid();
        var all = await db.Tickets.AsNoTracking().OrderByDescending(x => x.CreatedAt).ToListAsync(ct);
        var st = await SubmissionService.StatusesAsync(db, all, ct);
        return Ok(all.Select(t => View(t, me, st[t.Id])));
    }

    /// <summary>顯示用狀態：訪談中／已取消讀 DB，其餘由 Tool Spec 狀態推導（spec 5.2）。</summary>
    async Task<string> StatusOf(RequirementTicket t, CancellationToken ct) => (await SubmissionService.StatusesAsync(db, [t], ct))[t.Id];

    void Log(RequirementTicket t, string type, UserIdentity me, string? from, string? to, string? reason) =>
        db.TicketEvents.Add(new TicketEvent { TicketId = t.Id, Type = type, ActorEmpNo = me.EmpNo, At = time.GetUtcNow(), FromValue = from, ToValue = to, Reason = reason });

    public record CancelRequest(string? Reason);

    /// <summary>取消（spec 5.3-9）：只有負責的 CIM Engineer，須附原因。</summary>
    [HttpPost("{id}/cancel")]
    public async Task<IActionResult> Cancel(string id, CancelRequest req, CancellationToken ct)
    {
        if (await WhoAsync(ct) is not { } me) return Forbid();
        var t = await db.Tickets.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (t is null) return NotFound();
        if (!IsResponsible(t, me)) return Forbid();
        if (string.IsNullOrWhiteSpace(req.Reason)) return Problem("取消須附原因。", statusCode: 400);
        if (t.Status == TicketStatus.Cancelled) return Problem("需求單已是取消狀態。", statusCode: 409);
        Log(t, "Cancel", me, t.Status, TicketStatus.Cancelled, req.Reason.Trim());
        t.StatusBeforeCancel = t.Status;
        t.Status = TicketStatus.Cancelled;
        t.CancelReason = req.Reason.Trim();
        await db.SaveChangesAsync(ct);
        return Ok(View(t, me, await StatusOf(t, ct)));
    }

    /// <summary>重開：回到取消前的狀態。</summary>
    [HttpPost("{id}/reopen")]
    public async Task<IActionResult> Reopen(string id, CancellationToken ct)
    {
        if (await WhoAsync(ct) is not { } me) return Forbid();
        var t = await db.Tickets.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (t is null) return NotFound();
        if (!IsResponsible(t, me)) return Forbid();
        if (t.Status != TicketStatus.Cancelled) return Problem("需求單不是取消狀態。", statusCode: 409);
        var back = t.StatusBeforeCancel ?? TicketStatus.Interviewing;
        Log(t, "Reopen", me, t.Status, back, null);
        t.Status = back;
        t.StatusBeforeCancel = null;
        t.CancelReason = null;
        await db.SaveChangesAsync(ct);
        return Ok(View(t, me, await StatusOf(t, ct)));
    }

    public record ReassignRequest(string? NewCimEngineerEmpNo, string? Reason);

    /// <summary>改派（spec 3.3）：任何 CIM Engineer、須附原因、不寄通知；不驗證對方部門（等對方登入才判斷）。</summary>
    [HttpPost("{id}/reassign")]
    public async Task<IActionResult> Reassign(string id, ReassignRequest req, CancellationToken ct)
    {
        if (await WhoAsync(ct) is not { } me || !me.IsCimEngineer) return Forbid();
        var t = await db.Tickets.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (t is null) return NotFound();
        if (string.IsNullOrWhiteSpace(req.Reason) || string.IsNullOrWhiteSpace(req.NewCimEngineerEmpNo))
            return Problem("改派須附新負責人工號與原因。", statusCode: 400);
        var target = await directory.FindAsync(req.NewCimEngineerEmpNo.Trim(), ct);
        if (target is null) return Problem($"查無工號 {req.NewCimEngineerEmpNo}，無法改派。", statusCode: 422);
        Log(t, "Reassign", me, t.CimEngineerEmpNo, target.EmpNo, req.Reason.Trim());
        t.CimEngineerEmpNo = target.EmpNo;
        t.CimEngineerName = target.Name;
        await db.SaveChangesAsync(ct);
        return Ok(View(t, me, await StatusOf(t, ct)));
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> Get(string id, CancellationToken ct)
    {
        if (await WhoAsync(ct) is not { } me) return Forbid();
        var t = await db.Tickets.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        return t is null ? NotFound() : Ok(View(t, me, await StatusOf(t, ct)));
    }

    /// <summary>
    /// 唯讀檢視（spec 3.3「其他 SSO 使用者」、§12 唯讀的需求單頁）：任何登入者都能讀需求單、最新一次送出的 Workflow Definition、
    /// 各 Tool Spec 目前凍結的 revision 與評估結果；不含 Interview 對話，也沒有任何草稿內容（草稿只有 CIM Engineer 能讀）。
    /// </summary>
    [HttpGet("{id}/readonly")]
    public async Task<IActionResult> ReadOnlyView(string id, CancellationToken ct)
    {
        if (await WhoAsync(ct) is not { } me) return Forbid();
        var t = await db.Tickets.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (t is null) return NotFound();
        var sub = await db.Submissions.AsNoTracking().Where(x => x.TicketId == id).OrderByDescending(x => x.Number).FirstOrDefaultAsync(ct);
        var recs = await db.ToolSpecRecords.AsNoTracking().Where(r => r.TicketId == id).OrderBy(r => r.SpecId).ToListAsync(ct);
        var revs = await db.ToolSpecRevisions.AsNoTracking().Where(r => r.TicketId == id).ToListAsync(ct);
        var reviews = await db.FeasibilityReviews.AsNoTracking().Where(r => r.TicketId == id).OrderBy(r => r.Id).ToListAsync(ct);
        var snaps = await db.InterfaceSnapshots.AsNoTracking().Where(s => s.TicketId == id).OrderBy(s => s.Id).ToListAsync(ct);
        return Ok(new
        {
            ticket = View(t, me, await StatusOf(t, ct)),
            workflow = sub is null ? new System.Text.Json.Nodes.JsonArray() : System.Text.Json.Nodes.JsonNode.Parse(sub.WorkflowJson),
            toolSpecs = recs.Select(r => new
            {
                specId = r.SpecId, kind = r.Kind, state = r.State, revision = r.CurrentRevision, acceptedCondition = r.AcceptedCondition,
                content = System.Text.Json.Nodes.JsonNode.Parse(revs.First(v => v.SpecId == r.SpecId && v.Revision == r.CurrentRevision).ContentJson),
                reviews = reviews.Where(v => v.SpecId == r.SpecId).Select(v => new { v.Revision, v.Result, v.Reason, v.Condition, v.EstimatedDays, v.ReviewerEmpNo, v.At }),
                implemented = snaps.LastOrDefault(s => s.SpecId == r.SpecId && s.Revision == r.CurrentRevision) is { } s2 && r.State == ToolSpecState.Implemented ? new { s2.ServerKey, s2.ToolName } : null,
            }),
        });
    }

    /// <summary>複製為 Markdown（spec 6.6）：已完成的單，任何可檢視者都能取；用最新一次送出的 Workflow 快照與各 Tool Spec 目前的 revision。</summary>
    [HttpGet("{id}/markdown")]
    public async Task<IActionResult> Markdown(string id, CancellationToken ct)
    {
        if (await WhoAsync(ct) is null) return Forbid();
        var t = await db.Tickets.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (t is null) return NotFound();
        if (await StatusOf(t, ct) != TicketStatus.Completed) return Problem("需求單尚未完成，無法複製。", statusCode: 409);
        var sub = await db.Submissions.AsNoTracking().Where(x => x.TicketId == id).OrderByDescending(x => x.Number).FirstAsync(ct);
        var recs = await db.ToolSpecRecords.AsNoTracking().Where(r => r.TicketId == id).ToListAsync(ct);
        var revs = await db.ToolSpecRevisions.AsNoTracking().Where(r => r.TicketId == id).ToListAsync(ct);
        var specs = recs.ToDictionary(r => r.SpecId, r => (System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse(revs.First(v => v.SpecId == r.SpecId && v.Revision == r.CurrentRevision).ContentJson)!);
        // 已實作的 Tool Spec 以 interface_snapshot 為準（只取比對目前 revision 的那筆；同一 revision 多筆取最新）
        var snaps = (await db.InterfaceSnapshots.AsNoTracking().Where(s => s.TicketId == id).ToListAsync(ct))
            .Where(s => recs.Any(r => r.SpecId == s.SpecId && r.CurrentRevision == s.Revision && r.State == ToolSpecState.Implemented))
            .GroupBy(s => s.SpecId).ToDictionary(g => g.Key, g => g.OrderByDescending(s => s.Id).First());
        var implemented = snaps.ToDictionary(p => p.Key, p => (p.Value.ServerKey, p.Value.ToolName));
        var md = MarkdownExporter.Build(t.Id, t.Description, (System.Text.Json.Nodes.JsonArray)System.Text.Json.Nodes.JsonNode.Parse(sub.WorkflowJson)!, specs, implemented);
        return Content(md, "text/markdown; charset=utf-8");
    }
}
