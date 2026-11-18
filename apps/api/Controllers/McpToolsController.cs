using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AiAgentStudio.Api.Studio;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ModelContextProtocol.Protocol;

namespace AiAgentStudio.Api.Controllers;

/// <summary>MCP Tool Catalog 頁的資料來源（spec 12.2）。</summary>
public sealed record McpToolDto(
    string Name, string QualifiedName, string? Description,
    JsonElement InputSchema, JsonElement? OutputSchema, ToolAnnotations? Annotations);

/// <summary>刻意只有 id、type、fetchedAt（失敗時 null）、error（已去識別的通用訊息）、tools；連線設定（url、headers、command、args、env）不在其中。</summary>
public sealed record McpServerDto(string Id, string Type, DateTimeOffset? FetchedAt, string? Error, McpToolDto[] Tools);

public sealed record McpServerIdDto(string Id, string Type);

public sealed record McpServerListResponse(McpServerIdDto[] Servers);

[ApiController]
[Route("api/mcp-tools")]
public class McpToolsController(McpToolCatalog catalog) : ControllerBase
{
    static McpServerDto ToDto(McpServerTools s) => new(
        s.Server, s.Type, s.FetchedAt, s.Error is null ? null : s.TimedOut ? "連線逾時" : "無法連線",
        [.. s.Tools.Select(t => new McpToolDto(t.Name, t.QualifiedName, t.Description, t.InputSchema, t.OutputSchema, t.Annotations))]);

    [HttpGet("servers"), Authorize]
    public McpServerListResponse ListServers() => new([.. catalog.ListServerIds().Select(s => new McpServerIdDto(s.Id, s.Type))]);

    [HttpGet("servers/{id}"), Authorize]
    public async Task<ActionResult<McpServerDto>> GetServer(string id, CancellationToken ct, [FromQuery] bool refresh = false) =>
        await catalog.GetAsync(id, ct, refresh) is { } s ? ToDto(s) : NotFound();
}
