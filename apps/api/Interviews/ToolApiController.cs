using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AiAgentStudio.Api.Studio;
using AiAgentStudio.Api.Tickets;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AiAgentStudio.Api.Interviews;

/// <summary>
/// Python agent 呼叫的內部 tool API（spec 2.3）。只認 X-Run-Token（不是使用者 JWT），
/// 且單號一律取自 token，不接受呼叫端指定，所以只能操作該 run 的那張單。
/// 唯讀工具：get_requirement_ticket（#17）、list_mcp_tools、list_systems（#19）；其餘工具在後續票加入並沿用同一個檢查。
/// </summary>
[ApiController]
[Route("internal/tools")]
[AllowAnonymous]
public class ToolApiController(RunTokenService tokens, StudioDb db, McpToolCatalog mcp, StudioConfigProvider config) : ControllerBase
{
    RunTokenClaims? Claims() => tokens.Validate(Request.Headers["X-Run-Token"].ToString());

    [HttpGet("requirement-ticket")]
    public async Task<IActionResult> GetRequirementTicket(CancellationToken ct)
    {
        if (Claims() is not { } claims) return Unauthorized();
        var t = await db.Tickets.AsNoTracking().FirstOrDefaultAsync(x => x.Id == claims.TicketId, ct);
        if (t is null) return NotFound();
        return Ok(new
        {
            id = t.Id, description = t.Description, status = TicketStatus.Label(t.Status),
            cimEngineerEmpNo = t.CimEngineerEmpNo, cimEngineerName = t.CimEngineerName,
            businessUserEmpNo = t.BusinessUserEmpNo, businessUserName = t.BusinessUserName,
        });
    }

    /// <summary>list_mcp_tools：各 MCP server 的工具清單（只列舉，agent 絕不呼叫業務 MCP 工具）。</summary>
    [HttpGet("mcp-tools")]
    public async Task<IActionResult> ListMcpTools(CancellationToken ct)
    {
        if (Claims() is null) return Unauthorized();
        var servers = await mcp.ListAsync(ct);
        return Ok(new
        {
            servers = servers.Select(s => new
            {
                server = s.Server, error = s.Error,
                tools = s.Tools.Select(t => new
                {
                    name = t.Name, qualifiedName = t.QualifiedName, description = t.Description,
                    inputSchema = t.InputSchema, outputSchema = t.OutputSchema, annotations = t.Annotations,
                }),
            }),
        });
    }

    /// <summary>list_systems：系統對照表（系統 → System Owner 工號）。</summary>
    [HttpGet("systems")]
    public IActionResult ListSystems()
    {
        if (Claims() is null) return Unauthorized();
        return Ok(config.Current.Systems.Select(s => new { system = s.Key, ownerEmpNo = s.Value }));
    }
}
