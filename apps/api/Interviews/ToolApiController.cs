using System.Threading;
using System.Threading.Tasks;
using AiAgentStudio.Api.Tickets;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AiAgentStudio.Api.Interviews;

/// <summary>
/// Python agent 呼叫的內部 tool API（spec 2.3）。只認 X-Run-Token（不是使用者 JWT），
/// 且單號一律取自 token，不接受呼叫端指定，所以只能操作該 run 的那張單。
/// #17 只開 get_requirement_ticket；其餘工具在後續票加入並沿用同一個檢查。
/// </summary>
[ApiController]
[Route("internal/tools")]
[AllowAnonymous]
public class ToolApiController(RunTokenService tokens, StudioDb db) : ControllerBase
{
    [HttpGet("requirement-ticket")]
    public async Task<IActionResult> GetRequirementTicket(CancellationToken ct)
    {
        if (tokens.Validate(Request.Headers["X-Run-Token"].ToString()) is not { } claims) return Unauthorized();
        var t = await db.Tickets.AsNoTracking().FirstOrDefaultAsync(x => x.Id == claims.TicketId, ct);
        if (t is null) return NotFound();
        return Ok(new
        {
            id = t.Id, description = t.Description, status = TicketStatus.Label(t.Status),
            cimEngineerEmpNo = t.CimEngineerEmpNo, cimEngineerName = t.CimEngineerName,
            businessUserEmpNo = t.BusinessUserEmpNo, businessUserName = t.BusinessUserName,
        });
    }
}
