using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AiAgentStudio.Api.Identity;
using AiAgentStudio.Api.Services;
using AiAgentStudio.Api.Tickets;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AiAgentStudio.Api.Submissions;

/// <summary>送出 Interview 與送出前狀態（spec 8.5）。只認使用者登入憑證；run token 不是 JWT，所以 agent 呼叫不到。</summary>
[ApiController]
[Route("api/tickets/{ticketId}")]
[Authorize]
public class SubmissionController(ICurrentUserService user, IdentityService identity, StudioDb db, SubmissionService submissions) : ControllerBase
{
    async Task<UserIdentity?> WhoAsync(CancellationToken ct)
    {
        if (user.EmpNo is not { } empNo) return null;
        var jwt = Request.Headers.Authorization.ToString()["Bearer ".Length..].Trim();
        return await identity.GetAsync(empNo, jwt, ct);
    }

    /// <summary>阻擋清單（訪談中才有）、建議的 System Owner、各次送出的凍結內容與目前的 Tool Spec 狀態。任何 CIM Engineer 可讀。</summary>
    [HttpGet("submission")]
    public async Task<IActionResult> State(string ticketId, CancellationToken ct)
    {
        if (await WhoAsync(ct) is not { IsCimEngineer: true } me) return Forbid();
        var t = await db.Tickets.AsNoTracking().FirstOrDefaultAsync(x => x.Id == ticketId, ct);
        if (t is null) return NotFound();
        var blockers = t.Status == TicketStatus.Interviewing ? await submissions.BlockersAsync(ticketId, ct) : [];
        var suggested = await submissions.OwnerSuggestionsAsync(ct);

        var subs = await db.Submissions.AsNoTracking().Where(x => x.TicketId == ticketId).OrderBy(x => x.Number).ToListAsync(ct);
        var revs = await db.ToolSpecRevisions.AsNoTracking().Where(x => x.TicketId == ticketId).OrderBy(x => x.SpecId).ToListAsync(ct);
        var recs = await db.ToolSpecRecords.AsNoTracking().Where(x => x.TicketId == ticketId).OrderBy(x => x.SpecId).ToListAsync(ct);
        return Ok(new
        {
            blockers, pendingCount = blockers.Count,
            canSubmit = t.Status == TicketStatus.Interviewing && t.CimEngineerEmpNo == me.EmpNo && blockers.Count == 0,
            suggestedOwners = suggested,
            toolSpecs = recs.Select(r => new { specId = r.SpecId, kind = r.Kind, state = r.State, revision = r.CurrentRevision }),
            submissions = subs.Select(s => new
            {
                number = s.Number, at = s.At, submittedBy = s.SubmittedByEmpNo, workflow = JsonNode.Parse(s.WorkflowJson),
                specs = revs.Where(r => r.SubmissionNumber == s.Number).Select(r => new
                {
                    specId = r.SpecId, revision = r.Revision, submissionNumber = r.SubmissionNumber, content = JsonNode.Parse(r.ContentJson),
                    state = recs.FirstOrDefault(x => x.SpecId == r.SpecId && x.CurrentRevision == r.Revision)?.State, // 已被新 revision 取代者為 null
                }),
            }),
        });
    }

    [HttpPost("submit")]
    public async Task<IActionResult> Submit(string ticketId, CancellationToken ct)
    {
        if (await WhoAsync(ct) is not { IsCimEngineer: true } me) return Forbid();
        var t = await db.Tickets.FirstOrDefaultAsync(x => x.Id == ticketId, ct);
        if (t is null) return NotFound();
        if (t.CimEngineerEmpNo != me.EmpNo) return Forbid();
        if (t.Status != TicketStatus.Interviewing) return Problem(t.Status == TicketStatus.Cancelled ? "需求單已取消，無法送出。" : "Interview 已送出。", statusCode: 409);
        var (number, blockers) = await submissions.SubmitAsync(t, me.EmpNo, ct);
        if (number is null) return Conflict(new { title = "還有未處理的項目，無法送出。", blockers });
        return Ok(new { number, status = TicketStatus.Label((await SubmissionService.StatusesAsync(db, [t], ct))[t.Id]) });
    }
}
