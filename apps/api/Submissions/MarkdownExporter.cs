using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;

namespace AiAgentStudio.Api.Submissions;

/// <summary>複製為 Markdown（spec 6.6）：Workflow Definition 快照加上每一步的 MCP server key 與工具名稱。業務規則只在這裡。</summary>
public static class MarkdownExporter
{
    /// <param name="ticketId">單號。</param>
    /// <param name="description">需求描述。</param>
    /// <param name="workflow">最新一次送出的 Workflow Definition 快照。</param>
    /// <param name="specs">specId → 目前 revision 的內容（Tool Spec Revision）。</param>
    /// <param name="implemented">specId → 已實作時 interface_snapshot 記下的 (server key, 工具名稱)；有的話以它為準（New 也有真正的 server 與工具名）。</param>
    public static string Build(string ticketId, string description, JsonArray workflow, IReadOnlyDictionary<string, JsonObject> specs, IReadOnlyDictionary<string, (string Server, string Tool)>? implemented = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Workflow Definition：{ticketId}").AppendLine().AppendLine(description).AppendLine();
        var i = 0;
        foreach (var step in workflow.OfType<JsonObject>())
        {
            i++;
            sb.AppendLine($"## 步驟 {i}（{Str(step["id"])}）：{Str(step["description"])}");
            if (Str(step["condition"]) is { Length: > 0 } cond) sb.AppendLine($"- 條件：{cond}");
            sb.AppendLine($"- 工具：{Tool(Str(step["tool_spec"]), specs, implemented)}");
            sb.AppendLine($"- 輸入來源：{Inputs(step["inputs"])}");
            sb.AppendLine($"- 輸出：{(step["outputs"] is JsonArray o && o.Count > 0 ? string.Join("、", o.Select(x => $"`{x}`")) : "（無）")}");
            sb.AppendLine($"- 需要人工核准：{(step["needs_approval"]?.GetValue<bool>() == true ? "是" : "否")}").AppendLine();
        }
        return sb.ToString();
    }

    static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    static string Tool(string? specId, IReadOnlyDictionary<string, JsonObject> specs, IReadOnlyDictionary<string, (string Server, string Tool)>? implemented)
    {
        if (string.IsNullOrEmpty(specId)) return "人工步驟（不使用 MCP 工具）";
        if (!specs.TryGetValue(specId, out var spec)) return $"（找不到 Tool Spec {specId}）";
        if (implemented?.TryGetValue(specId, out var snap) == true) return $"MCP server `{snap.Server}`，工具 `{snap.Tool}`";
        // Reuse／Extend 用 ref；New 用 expected_tool_name（尚無 MCP server key）
        if (spec["ref"] is JsonObject r) return $"MCP server `{Str(r["server"])}`，工具 `{Str(r["tool"])}`";
        return Str(spec["expected_tool_name"]) is { Length: > 0 } n ? $"MCP server（待實作後指定），工具 `{n}`（預期名稱）" : "（尚未指定工具名稱）";
    }

    static string Inputs(JsonNode? inputs)
    {
        if (inputs is not JsonArray a || a.Count == 0) return "（無）";
        return string.Join("；", a.OfType<JsonObject>().Select(x =>
            $"`{Str(x["name"])}` ← " + (x["from_user"]?.GetValue<bool>() == true ? "使用者輸入" : $"{Str(x["from_step"])}.{Str(x["from_field"])}")));
    }
}
