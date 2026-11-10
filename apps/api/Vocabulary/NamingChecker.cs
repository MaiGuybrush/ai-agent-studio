using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace AiAgentStudio.Api.Vocabulary;

/// <summary>Status：pass（通過）／rename（建議改名，Suggestion 為標準名）／new（新名詞，交給 CIM Engineer 確認）。</summary>
public sealed record NameResult(string Status, string? Suggestion);

/// <summary>Naming Check 比對規則（spec 7.2）。業務規則只在這裡。</summary>
public static class NamingChecker
{
    /// <summary>依序：1 與標準名完全一致；2 只差大小寫；3 命中別名（不分大小寫）；4 新名詞。pascal 指定情境用 Pascal 或 camel（預設 camel，JSON 欄位名稱）。</summary>
    public static NameResult Check(IReadOnlyList<GlossaryEntry> vocab, string name, bool pascal = false)
    {
        string Std(GlossaryEntry e) => pascal ? e.StandardPascal : e.StandardCamel;
        if (vocab.FirstOrDefault(e => Std(e) == name) is not null) return new("pass", null);
        var ci = StringComparison.OrdinalIgnoreCase;
        if (vocab.FirstOrDefault(e => e.StandardCamel.Equals(name, ci) || e.StandardPascal.Equals(name, ci)) is { } e2) return new("rename", Std(e2));
        if (vocab.FirstOrDefault(e => e.Aliases.Any(a => a.Equals(name, ci))) is { } e3) return new("rename", Std(e3));
        return new("new", null);
    }

    /// <summary>走訪 JSON Schema 的所有欄位名稱（含巢狀 properties 與 array items），key 為欄位路徑（如 inputs.lots[].state）。</summary>
    public static Dictionary<string, NameResult> CheckSchema(IReadOnlyList<GlossaryEntry> vocab, JsonNode? schema, string path)
    {
        var res = new Dictionary<string, NameResult>();
        void Walk(JsonNode? n, string at)
        {
            if (n is not JsonObject o) return;
            if (o["properties"] is JsonObject props)
                foreach (var (name, child) in props)
                {
                    res[$"{at}.{name}"] = Check(vocab, name);
                    Walk(child, $"{at}.{name}");
                }
            if (o["items"] is { } items) Walk(items, $"{at}[]");
        }
        Walk(schema, path);
        return res;
    }
}
