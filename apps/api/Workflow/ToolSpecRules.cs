using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace AiAgentStudio.Api.Workflow;

/// <summary>Tool Spec 的 Kind（DB／JSON 的值就是這些字串）。</summary>
public static class ToolSpecKinds
{
    public const string Reuse = "Reuse", Extend = "Extend", New = "New";
    public static readonly string[] All = [Reuse, Extend, New];
    /// <summary>Reuse／Extend 要參照現有工具（ref）。</summary>
    public static bool NeedsRef(string? kind) => kind is Reuse or Extend;
}

/// <summary>Tool Spec 的欄位規則（spec 6.1、6.2）。業務規則只在這裡，Python 與畫面都不重複。</summary>
public static class ToolSpecRules
{
    public static readonly string[] SideEffects = ["read", "write"];
    static readonly string[] Types = ["string", "number", "integer", "boolean", "array", "object"];
    static readonly string[] SchemaKeys = ["type", "properties", "items", "enum", "format", "required", "description"];

    /// <summary>送出前必填但目前缺少的欄位；Reuse 不需要 System Owner（所屬單由 token 決定，不用填）；Reuse／Extend 要有 ref（§6.2）。</summary>
    public static string[] Missing(JsonObject spec)
    {
        bool Has(string k) => k == "ref"
            ? spec[k] is JsonObject r && !string.IsNullOrWhiteSpace(Str(r["server"]!)) && !string.IsNullOrWhiteSpace(Str(r["tool"]!))
            : spec[k] is { } v && !(v is JsonValue jv && jv.TryGetValue<string>(out var s) && string.IsNullOrWhiteSpace(s));
        var need = new List<string> { "kind", "purpose", "system", "inputs", "outputs", "side_effect" };
        if (spec["kind"]?.GetValue<string>() != ToolSpecKinds.Reuse) need.Add("system_owner");
        if (ToolSpecKinds.NeedsRef(spec["kind"]?.GetValue<string>())) need.Add("ref");
        return need.Where(k => !Has(k)).ToArray();
    }

    /// <summary>檢查會寫入的欄位；回傳錯誤訊息，null 表示通過。systems 為系統對照表的系統名稱。</summary>
    public static string? Validate(JsonObject fields, IEnumerable<string> systems)
    {
        if (fields["kind"] is { } k && !ToolSpecKinds.All.Contains(Str(k))) return $"kind 必須是 {string.Join("／", ToolSpecKinds.All)}。";
        if (fields["side_effect"] is { } se && !SideEffects.Contains(Str(se))) return $"side_effect 必須是 {string.Join("／", SideEffects)}。";
        if (fields["system"] is { } sys && !systems.Contains(Str(sys))) return $"未知的系統 '{Str(sys)}'；可用：{string.Join("、", systems)}。";
        foreach (var f in new[] { "inputs", "outputs" })
            if (fields[f] is { } schema && Schema(schema, f) is { } err) return err;
        if (fields["ref"] is { } r && !(r is JsonObject o && o["server"] is JsonValue && o["tool"] is JsonValue)) return "ref 必須是 {server, tool}。";
        return null;
    }

    static string? Str(JsonNode n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>JSON Schema 子集（spec 6.1），巢狀欄位遞迴檢查。</summary>
    static string? Schema(JsonNode n, string at)
    {
        if (n is not JsonObject o) return $"{at} 必須是 JSON Schema 物件。";
        if (o.Select(p => p.Key).FirstOrDefault(k => !SchemaKeys.Contains(k)) is { } bad) return $"{at}：不支援的關鍵字 '{bad}'（只支援 {string.Join("、", SchemaKeys)}）。";
        var type = o["type"] is { } t ? Str(t) : null;
        if (type is null || !Types.Contains(type)) return $"{at}：type 必須是 {string.Join("／", Types)}。";
        if (o["format"] is { } fmt && (Str(fmt) != "date-time" || type != "string")) return $"{at}：format 只支援 string 的 date-time。";
        if (o["enum"] is { } e && e is not JsonArray) return $"{at}：enum 必須是陣列。";
        if (o["properties"] is { } ps)
        {
            if (type != "object" || ps is not JsonObject po) return $"{at}：properties 只能用在 object。";
            foreach (var p in po) if (Schema(p.Value!, $"{at}.{p.Key}") is { } err) return err;
        }
        if (o["required"] is { } rq)
        {
            if (rq is not JsonArray ra || ra.Any(x => x is null || Str(x) is not { } n2 || o["properties"]?[n2] is null)) return $"{at}：required 必須列出 properties 內的欄位名稱。";
        }
        if (type == "array")
        {
            if (o["items"] is not { } it) return $"{at}：array 需要 items。";
            if (Schema(it, $"{at}[]") is { } err) return err;
        }
        else if (o["items"] is not null) return $"{at}：items 只能用在 array。";
        return null;
    }
}
