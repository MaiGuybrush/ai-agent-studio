using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AiAgentStudio.Api.Studio;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ModelContextProtocol.Protocol;

namespace AiAgentStudio.Api.Controllers;

/// <summary>MCP Tool Catalog 頁的資料來源（spec 12.2）。後續欄位（fetchedAt 等）加在這些 DTO 上即可。</summary>
public sealed record McpToolDto(
    string Name, string QualifiedName, string? Description,
    JsonElement InputSchema, JsonElement? OutputSchema, ToolAnnotations? Annotations);

/// <summary>刻意只有 id、type、error、tools；連線設定（url、headers、command、args、env）不在其中。</summary>
public sealed record McpServerDto(string Id, string Type, string? Error, McpToolDto[] Tools);

public sealed record McpToolsResponse(McpServerDto[] Servers);

[ApiController]
[Route("api/mcp-tools")]
public class McpToolsController(McpToolCatalog catalog, StudioConfigProvider config) : ControllerBase
{
    [HttpGet, Authorize]
    public async Task<McpToolsResponse> Get(CancellationToken ct)
    {
        var servers = await catalog.ListAsync(ct);
        return new McpToolsResponse([.. servers.Select(s => new McpServerDto(
            s.Server, config.Current.McpServers[s.Server].Type, s.Error,
            [.. s.Tools.Select(t => new McpToolDto(t.Name, t.QualifiedName, t.Description, t.InputSchema, t.OutputSchema, t.Annotations))]))]);
    }
}
