using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using AiAgentStudio.Api.Vocabulary;

namespace AiAgentStudio.Api.Submissions;

/// <summary>Failures 非空即不通過；Warnings 只提示（Naming Check 命中別名）。</summary>
public sealed record ComplianceResult(IReadOnlyList<string> Failures, IReadOnlyList<string> Warnings)
{
    public bool Passed => Failures.Count == 0;
}

/// <summary>
/// Interface Compliance Check 比對規則（spec 6.4）。純函式，業務規則只在這裡。
/// 規則：input 名稱／型別／required 相符（遞迴）；output 只比名稱與型別；
/// 實際多出必填 input → 失敗；多出的選填 input 與 output 只跑 Naming Check（命中別名僅警告）；Extend 只比對有定義的欄位；
/// enum：實際須包含 Tool Spec 全部值；number（Tool Spec）接受 integer（實際），反過來不行。
/// </summary>
public static class InterfaceComplianceChecker
{
    public static ComplianceResult Check(string kind, JsonNode? specInputs, JsonNode? specOutputs, JsonNode? actualInput, JsonNode? actualOutput,
        IReadOnlyList<GlossaryEntry>? vocab = null, IReadOnlyCollection<string>? pending = null)
    {
        var failures = new List<string>();
        var warnings = new List<string>();
        var extend = kind == "Extend";

        void Walk(JsonNode? spec, JsonNode? actual, string path, bool isInput)
        {
            if (spec is not JsonObject s) return;
            if (actual is not JsonObject a) { failures.Add($"{path}：實際工具沒有這個欄位的定義。"); return; }
            var st = TypeOf(s); var at = TypeOf(a);
            if (st != at && !(st == "number" && at == "integer")) failures.Add($"{path}：型別不符（Tool Spec 為 {st}，實際為 {at}）。");

            if (s["enum"] is JsonArray se && a["enum"] is JsonArray ae)
            {
                var have = ae.Select(x => x?.ToJsonString()).ToHashSet();
                var lost = se.Where(x => !have.Contains(x?.ToJsonString())).Select(x => x?.ToJsonString());
                if (lost.Any()) failures.Add($"{path}：實際 enum 缺少 Tool Spec 的值 {string.Join("、", lost)}。");
            }

            var sp = s["properties"] as JsonObject; var ap = a["properties"] as JsonObject;
            if (sp is not null)
            {
                var sreq = Required(s); var areq = Required(a);
                foreach (var (name, child) in sp)
                {
                    var p = $"{path}.{name}";
                    if (ap is null || !ap.TryGetPropertyValue(name, out var actChild)) { failures.Add($"{p}：實際工具沒有這個欄位。"); continue; }
                    if (isInput && sreq.Contains(name) != areq.Contains(name))
                        failures.Add($"{p}：required 不符（Tool Spec {(sreq.Contains(name) ? "必填" : "選填")}，實際{(areq.Contains(name) ? "必填" : "選填")}）。");
                    Walk(child, actChild, p, isInput);
                }
            }
            if (!extend && ap is not null)
                foreach (var (name, _) in ap.Where(x => sp is null || !sp.ContainsKey(x.Key)))
                {
                    var p = $"{path}.{name}";
                    if (isInput && Required(a).Contains(name)) failures.Add($"{p}：實際工具多出必填 input。");
                    else if (vocab is not null && NamingChecker.Check(vocab, name, pending: pending) is { Status: "rename" } r)
                        warnings.Add($"{p}：實際工具多出的欄位 {name} 命中別名，建議標準名 {r.Suggestion}。");
                }

            if (s["items"] is { } si) Walk(si, a["items"], $"{path}[]", isInput);
        }

        Walk(specInputs, actualInput ?? new JsonObject(), "inputs", true);
        if (specOutputs?["properties"] is JsonObject { Count: > 0 } && actualOutput is null) failures.Add("outputs：實際工具沒有 outputSchema。");
        else Walk(specOutputs, actualOutput ?? new JsonObject(), "outputs", false);
        return new(failures, warnings);
    }

    /// <summary>type 可能是 ["string","null"]：取第一個非 null 的型別。</summary>
    static string? TypeOf(JsonObject o) => o["type"] switch
    {
        JsonArray arr => arr.Select(x => x?.GetValue<string>()).FirstOrDefault(x => x != "null"),
        JsonValue v when v.TryGetValue<string>(out var t) => t,
        _ => null,
    };

    static HashSet<string> Required(JsonObject o) => o["required"] is JsonArray r ? r.Select(x => x?.GetValue<string>() ?? "").ToHashSet() : [];
}
