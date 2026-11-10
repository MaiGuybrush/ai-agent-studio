using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AiAgentStudio.Api.Identity;
using AiAgentStudio.Api.Services;
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
            canReassign = me.IsCimEngineer,
            canViewConversation = me.IsCimEngineer,
        };
    }

    static object View(RequirementTicket t, UserIdentity me) => new
    {
        id = t.Id, description = t.Description,
        cimEngineerEmpNo = t.CimEngineerEmpNo, cimEngineerName = t.CimEngineerName,
        businessUserEmpNo = t.BusinessUserEmpNo, businessUserName = t.BusinessUserName,
        status = TicketStatus.Label(t.Status), cancelReason = t.CancelReason, createdAt = t.CreatedAt,
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
        return Created($"/api/tickets/{t.Id}", View(t, me));
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        if (await WhoAsync(ct) is not { } me) return Forbid();
        var all = await db.Tickets.AsNoTracking().OrderByDescending(x => x.CreatedAt).ToListAsync(ct);
        return Ok(all.Select(t => View(t, me)));
    }

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
        return Ok(View(t, me));
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
        return Ok(View(t, me));
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> Get(string id, CancellationToken ct)
    {
        if (await WhoAsync(ct) is not { } me) return Forbid();
        var t = await db.Tickets.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        return t is null ? NotFound() : Ok(View(t, me));
    }
}
