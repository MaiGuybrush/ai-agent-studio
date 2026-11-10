using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AiAgentStudio.Api.Interviews;
using AiAgentStudio.Api.Studio;
using AiAgentStudio.Api.Tickets;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AiAgentStudio.Api.Workflow;

/// <summary>agent 的 Tool Spec 草稿工具（spec 8.3）。與 workflow-steps 同一套人工修改優先；單號取自 run token，所以 spec 一定只屬於該 run 的那張單。</summary>
[ApiController]
[Route("internal/tools/tool-specs")]
[AllowAnonymous]
public class ToolSpecToolController(RunTokenService tokens, WorkflowDraftService drafts, InterviewRunner runner, StudioConfigProvider config, McpToolCatalog mcp, StudioDb db) : ControllerBase
{
    const string Doc = WorkflowDraft.ToolSpecsDoc;

    /// <summary>search_tool_specs：別張單（未取消）進行中的 Tool Spec，關鍵字比對用途、預期工具名稱、系統、id。相似時由 agent 提醒 CIM Engineer。</summary>
    [HttpGet]
    public async Task<IActionResult> Search([FromQuery] string keyword, CancellationToken ct)
    {
        if (tokens.Validate(Request.Headers["X-Run-Token"].ToString()) is not { } c) return Unauthorized();
        var live = db.Tickets.Where(t => t.Status != TicketStatus.Cancelled).Select(t => t.Id);
        // ponytail: 讀出其他單的整份草稿後在記憶體比對；Tool Spec 量大時改成 DB 端的 JSONB 查詢
        var docs = await db.WorkflowDrafts.AsNoTracking().Where(d => d.Doc == Doc && d.TicketId != c.TicketId && live.Contains(d.TicketId)).ToListAsync(ct);
        bool Hit(JsonObject s) => new[] { "purpose", "expected_tool_name", "system", "id" }
            .Any(k => s[k]?.ToString().Contains(keyword ?? "", System.StringComparison.OrdinalIgnoreCase) == true);
        return Ok(docs.SelectMany(d => JsonNode.Parse(d.DraftJson)![Doc]!.AsArray().OfType<JsonObject>().Where(Hit).Select(s => new
        {
            ticketId = d.TicketId, id = s["id"]!.GetValue<string>(), kind = s["kind"], purpose = s["purpose"], system = s["system"], expected_tool_name = s["expected_tool_name"],
        })));
    }

    [HttpPut("{specId}")]
    public async Task<IActionResult> Upsert(string specId, [FromBody] JsonObject body, CancellationToken ct)
    {
        if (tokens.Validate(Request.Headers["X-Run-Token"].ToString()) is not { } c) return Unauthorized();
        var over = body["override_human_edit"]?.GetValue<bool>() ?? false;
        body.Remove("override_human_edit");
        if (ToolSpecRules.Validate(body, config.Current.Systems.Keys) is { } err) return BadRequest(new { message = err });
        if (body["ref"] is JsonObject rf && await Snapshot(rf, ct) is { } err2) return BadRequest(new { message = err2 });
        var r = await drafts.AgentUpsertAsync(c.TicketId, c.EmpNo, specId, body, over, Doc);
        return await Reply(c, r, specId);
    }

    [HttpDelete("{specId}")]
    public async Task<IActionResult> Delete(string specId, [FromQuery(Name = "override_human_edit")] bool over = false)
    {
        if (tokens.Validate(Request.Headers["X-Run-Token"].ToString()) is not { } c) return Unauthorized();
        return await Reply(c, await drafts.AgentDeleteAsync(c.TicketId, c.EmpNo, specId, over, Doc), null);
    }

    /// <summary>Reuse／Extend 參照現有工具時，保存當時的 schema 快照；找不到工具就拒絕。</summary>
    async Task<string?> Snapshot(JsonObject rf, CancellationToken ct)
    {
        var (server, tool) = (rf["server"]!.GetValue<string>(), rf["tool"]!.GetValue<string>());
        var s = (await mcp.ListAsync(ct)).FirstOrDefault(x => x.Server == server);
        if (s is null) return $"未知的 MCP server '{server}'。";
        if (s.Error is not null) return $"無法列舉 '{server}' 的工具：{s.Error}";
        if (s.Tools.FirstOrDefault(t => t.Name == tool) is not { } t) return $"'{server}' 沒有名為 '{tool}' 的工具。";
        rf["schema_snapshot"] = new JsonObject
        {
            ["input"] = JsonNode.Parse(t.InputSchema.GetRawText()),
            ["output"] = t.OutputSchema is { } o ? JsonNode.Parse(o.GetRawText()) : null,
        };
        return null;
    }

    async Task<IActionResult> Reply(RunTokenClaims c, AgentWriteResult r, string? specId)
    {
        switch (r.Status)
        {
            case "Rejected": return Conflict(new { message = "這些欄位已被人工修改；請先與 CIM Engineer 確認，同意後再帶 override_human_edit=true。", rejected = r.Rejected });
            case "NotFound": return NotFound();
            default:
                runner.Find(c.TicketId)?.Publish("draft_changed", new { version = r.Version, doc = Doc });
                var missing = specId is null ? null : (await drafts.GetAsync(c.TicketId, Doc)).Steps.OfType<JsonObject>().Where(s => s["id"]!.GetValue<string>() == specId).Select(ToolSpecRules.Missing).FirstOrDefault();
                return Ok(new { version = r.Version, missing });
        }
    }
}
