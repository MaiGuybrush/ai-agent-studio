using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AiAgentStudio.Api.Identity;
using AiAgentStudio.Api.Services;
using AiAgentStudio.Api.Tickets;
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
public class ToolSpecHandlingController(ICurrentUserService user, IdentityService identity, StudioDb db, TimeProvider time) : ControllerBase
{
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
}
