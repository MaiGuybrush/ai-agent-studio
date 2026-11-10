using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AiAgentStudio.Api.Identity;
using AiAgentStudio.Api.Notifications;
using AiAgentStudio.Api.Services;
using AiAgentStudio.Api.Tickets;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AiAgentStudio.Api.Submissions;

/// <summary>tool_spec_comment（spec 13）：留言串跨 revision 延續，每則標記所屬 revision；不影響 Tool Spec 狀態。</summary>
public class ToolSpecComment
{
    public long Id { get; set; }
    public string TicketId { get; set; } = "";
    public string SpecId { get; set; } = "";
    public int Revision { get; set; }
    public string AuthorEmpNo { get; set; } = "";
    public string Body { get; set; } = "";
    public DateTimeOffset At { get; set; }
}

/// <summary>review_assignment（spec 13）：System Owner 的轉派（By 為當時的 owner）與改派（By 為 CIM Engineer），含原因。</summary>
public class ReviewAssignment
{
    public long Id { get; set; }
    public string TicketId { get; set; } = "";
    public string SpecId { get; set; } = "";
    public int Revision { get; set; }
    public string FromEmpNo { get; set; } = "";
    public string ToEmpNo { get; set; } = "";
    public string ByEmpNo { get; set; } = "";
    public string Reason { get; set; } = "";
    public DateTimeOffset At { get; set; }
}

public static class SpecOwner
{
    /// <summary>目前負責評估的 System Owner：該 revision 最新一筆 review_assignment 的 ToEmpNo，沒有就用 revision 內容的 system_owner。</summary>
    public static string? Resolve(string contentJson, int revision, IEnumerable<ReviewAssignment> assignmentsOfSpec) =>
        assignmentsOfSpec.Where(a => a.Revision == revision).MaxBy(a => a.Id)?.ToEmpNo
        ?? (JsonNode.Parse(contentJson)?["system_owner"] is JsonValue v && v.TryGetValue<string>(out var o) ? o.Trim() : null);
}

public sealed record CommentRequest(string? Body);
public sealed record ReassignOwnerRequest(string? NewOwnerEmpNo, string? Reason);

/// <summary>Tool Spec 留言串與 System Owner 轉派／改派（spec 3.3、9、10.1）。只有負責的 CIM Engineer 與目前被指派的 System Owner 可讀寫。</summary>
[ApiController]
[Route("api/tickets/{ticketId}/specs/{specId}")]
[Authorize]
public class SpecCommentsController(ICurrentUserService user, IdentityService identity, IUserDirectory directory, StudioDb db, TimeProvider time) : ControllerBase
{
    record Ctx(RequirementTicket Ticket, ToolSpecRecord Rec, string? Owner, string Me, bool IsCim);

    async Task<(Ctx? Ctx, IActionResult? Error)> LoadAsync(string ticketId, string specId, CancellationToken ct)
    {
        if (user.EmpNo is not { } me) return (null, Forbid());
        var t = await db.Tickets.AsNoTracking().FirstOrDefaultAsync(x => x.Id == ticketId, ct);
        var rec = await db.ToolSpecRecords.FirstOrDefaultAsync(r => r.TicketId == ticketId && r.SpecId == specId, ct);
        if (t is null || rec is null || rec.Kind == "Reuse") return (null, NotFound());
        var rev = await db.ToolSpecRevisions.AsNoTracking().FirstAsync(r => r.TicketId == ticketId && r.SpecId == specId && r.Revision == rec.CurrentRevision, ct);
        var owner = SpecOwner.Resolve(rev.ContentJson, rec.CurrentRevision, await db.ReviewAssignments.AsNoTracking().Where(a => a.TicketId == ticketId && a.SpecId == specId).ToListAsync(ct));
        var jwt = Request.Headers.Authorization.ToString()["Bearer ".Length..].Trim();
        var isCim = t.CimEngineerEmpNo == me && (await identity.GetAsync(me, jwt, ct)).IsCimEngineer;
        return (new Ctx(t, rec, owner, me, isCim), null);
    }

    static bool Allowed(Ctx c) => c.IsCim || c.Owner == c.Me;

    [HttpGet("comments")]
    public async Task<IActionResult> List(string ticketId, string specId, CancellationToken ct)
    {
        var (c, err) = await LoadAsync(ticketId, specId, ct);
        if (c is null) return err!;
        if (!Allowed(c)) return Forbid();
        var rows = await db.ToolSpecComments.AsNoTracking().Where(x => x.TicketId == ticketId && x.SpecId == specId).OrderBy(x => x.Id).ToListAsync(ct);
        var names = new Dictionary<string, string>();
        foreach (var e in rows.Select(r => r.AuthorEmpNo).Distinct()) names[e] = (await directory.FindAsync(e, ct))?.Name ?? e;
        return Ok(rows.Select(r => new { r.Id, r.Revision, authorEmpNo = r.AuthorEmpNo, authorName = names[r.AuthorEmpNo], r.Body, r.At }));
    }

    [HttpPost("comments")]
    public async Task<IActionResult> Add(string ticketId, string specId, [FromBody] CommentRequest req, CancellationToken ct)
    {
        var (c, err) = await LoadAsync(ticketId, specId, ct);
        if (c is null) return err!;
        if (!Allowed(c)) return Forbid();
        if (string.IsNullOrWhiteSpace(req.Body)) return Problem("留言內容必填。", statusCode: 400);
        var now = time.GetUtcNow();
        db.ToolSpecComments.Add(new ToolSpecComment { TicketId = ticketId, SpecId = specId, Revision = c.Rec.CurrentRevision, AuthorEmpNo = c.Me, Body = req.Body.Trim(), At = now });
        // 通知對方：CIM Engineer 留言 → System Owner；System Owner 留言 → CIM Engineer。同一人兼任兩個角色時不寄給自己。
        var to = c.IsCim ? c.Owner : c.Ticket.CimEngineerEmpNo;
        if (to is not null && to != c.Me) EmailOutbox.Queue(db, EmailEvents.Commented, to, ticketId, new { specs = new[] { specId } }, now);
        await db.SaveChangesAsync(ct);
        return Ok();
    }

    /// <summary>轉派（目前 System Owner）或改派（負責的 CIM Engineer）：須附原因與有效工號，寫入 review_assignment 並通知新的 System Owner。</summary>
    [HttpPost("reassign-owner")]
    public async Task<IActionResult> Reassign(string ticketId, string specId, [FromBody] ReassignOwnerRequest req, CancellationToken ct)
    {
        var (c, err) = await LoadAsync(ticketId, specId, ct);
        if (c is null) return err!;
        if (!Allowed(c)) return Forbid();
        if (c.Ticket.Status != TicketStatus.Submitted || c.Rec.Handled) return Problem("目前狀態無法轉派。", statusCode: 409);
        if (string.IsNullOrWhiteSpace(req.Reason) || string.IsNullOrWhiteSpace(req.NewOwnerEmpNo)) return Problem("轉派須附新 System Owner 工號與原因。", statusCode: 400);
        var to = req.NewOwnerEmpNo.Trim();
        if (to == c.Owner) return Problem("新的 System Owner 與目前相同。", statusCode: 400);
        if (await directory.FindAsync(to, ct) is null) return Problem($"查無工號 {to}，無法轉派。", statusCode: 422);
        var now = time.GetUtcNow();
        db.ReviewAssignments.Add(new ReviewAssignment { TicketId = ticketId, SpecId = specId, Revision = c.Rec.CurrentRevision, FromEmpNo = c.Owner ?? "", ToEmpNo = to, ByEmpNo = c.Me, Reason = req.Reason.Trim(), At = now });
        EmailOutbox.Queue(db, EmailEvents.OwnerReassigned, to, ticketId, new { specs = new[] { specId } }, now);
        await db.SaveChangesAsync(ct);
        return Ok();
    }
}
