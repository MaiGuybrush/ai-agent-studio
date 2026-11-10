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

    /// <summary>權限規則集中在這裡（spec 3.3）。負責人調離 CIM 後 IsCimEngineer 為 false，自然變唯讀。</summary>
    static object PermissionsOf(RequirementTicket t, UserIdentity me)
    {
        var responsible = me.IsCimEngineer && t.CimEngineerEmpNo == me.EmpNo;
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

    [HttpGet("{id}")]
    public async Task<IActionResult> Get(string id, CancellationToken ct)
    {
        if (await WhoAsync(ct) is not { } me) return Forbid();
        var t = await db.Tickets.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        return t is null ? NotFound() : Ok(View(t, me));
    }
}
