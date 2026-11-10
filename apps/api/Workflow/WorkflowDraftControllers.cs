using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AiAgentStudio.Api.Identity;
using AiAgentStudio.Api.Interviews;
using AiAgentStudio.Api.Services;
using AiAgentStudio.Api.Tickets;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AiAgentStudio.Api.Workflow;

public record SaveDraftRequest(int Version, JsonArray? Steps);

/// <summary>CIM Engineer 直接編輯草稿（spec 8.4）。讀：任何 CIM Engineer；寫：負責的 CIM Engineer，且帶版本號。</summary>
[ApiController]
[Route("api/tickets/{ticketId}/workflow-draft")]
[Authorize]
public class WorkflowDraftController(ICurrentUserService user, IdentityService identity, StudioDb db, WorkflowDraftService drafts) : ControllerBase
{
    async Task<UserIdentity?> WhoAsync(CancellationToken ct)
    {
        if (user.EmpNo is not { } empNo) return null;
        var jwt = Request.Headers.Authorization.ToString()["Bearer ".Length..].Trim();
        return await identity.GetAsync(empNo, jwt, ct);
    }

    [HttpGet]
    public async Task<IActionResult> Get(string ticketId, CancellationToken ct)
    {
        if (await WhoAsync(ct) is not { IsCimEngineer: true }) return Forbid();
        if (!await db.Tickets.AnyAsync(x => x.Id == ticketId, ct)) return NotFound();
        return Ok(await drafts.GetAsync(ticketId));
    }

    [HttpPut]
    public async Task<IActionResult> Save(string ticketId, SaveDraftRequest req, CancellationToken ct)
    {
        if (await WhoAsync(ct) is not { IsCimEngineer: true } me) return Forbid();
        var t = await db.Tickets.AsNoTracking().FirstOrDefaultAsync(x => x.Id == ticketId, ct);
        if (t is null) return NotFound();
        if (t.CimEngineerEmpNo != me.EmpNo) return Forbid();
        if (t.Status == TicketStatus.Cancelled) return Problem("需求單已取消，無法編輯。", statusCode: 409);
        var ids = req.Steps?.Select(s => (s as JsonObject)?["id"]?.GetValue<string>()).ToList();
        if (req.Steps is null || ids!.Any(string.IsNullOrWhiteSpace) || ids!.Distinct().Count() != ids!.Count)
            return Problem("steps 的每一步都需要唯一的 id。", statusCode: 400);
        var r = await drafts.HumanSaveAsync(ticketId, req.Version, req.Steps);
        return r.Saved ? Ok(r.Current) : Conflict(new { title = "草稿已被更新，請重新載入後再修改。", current = r.Current });
    }
}

/// <summary>agent 的草稿工具（spec 8.3）。只認 run token；寫入後在該 run 的 SSE 串流插入 draft_changed。</summary>
[ApiController]
[Route("internal/tools/workflow-steps")]
[AllowAnonymous]
public class WorkflowToolController(RunTokenService tokens, WorkflowDraftService drafts, InterviewRunner runner) : ControllerBase
{
    [HttpPut("{stepId}")]
    public async Task<IActionResult> Upsert(string stepId, [FromBody] JsonObject body)
    {
        if (tokens.Validate(Request.Headers["X-Run-Token"].ToString()) is not { } c) return Unauthorized();
        var over = body["override_human_edit"]?.GetValue<bool>() ?? false;
        body.Remove("override_human_edit");
        return Reply(c, await drafts.AgentUpsertAsync(c.TicketId, c.EmpNo, stepId, body, over));
    }

    [HttpDelete("{stepId}")]
    public async Task<IActionResult> Delete(string stepId, [FromQuery(Name = "override_human_edit")] bool over = false)
    {
        if (tokens.Validate(Request.Headers["X-Run-Token"].ToString()) is not { } c) return Unauthorized();
        return Reply(c, await drafts.AgentDeleteAsync(c.TicketId, c.EmpNo, stepId, over));
    }

    IActionResult Reply(RunTokenClaims c, AgentWriteResult r)
    {
        switch (r.Status)
        {
            case "Rejected": return Conflict(new { message = "這些欄位已被人工修改；請先與 CIM Engineer 確認，同意後再帶 override_human_edit=true。", rejected = r.Rejected });
            case "NotFound": return NotFound();
            default:
                runner.Find(c.TicketId)?.Publish("draft_changed", new { version = r.Version });
                return Ok(new { version = r.Version });
        }
    }
}
