using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AiAgentStudio.Api.Studio;

/// <summary>mcpServers 一個項目（同 Claude Code .mcp.json）。type 只有 http（Url、Headers）與 stdio（Command、Args、Env）。</summary>
public sealed record McpServerConfig(
    string Type, string? Url, IReadOnlyDictionary<string, string> Headers,
    string? Command, IReadOnlyList<string> Args, IReadOnlyDictionary<string, string> Env);

/// <summary>系統設定（spec 4）：MCP Server List、系統 → System Owner 工號、CIM 部門代碼清單。</summary>
public sealed record StudioConfig(
    IReadOnlyDictionary<string, McpServerConfig> McpServers,
    IReadOnlyDictionary<string, string> Systems,
    IReadOnlyList<string> CimDeptCodes);

/// <summary>
/// 讀納入 git 的 JSON 設定檔。每次取 <see cref="Current"/> 都比對檔案的修改時間與大小，變了就重讀（改檔自動重載、免 watcher）。
/// 秘密值寫成 ${VAR}，由環境變數帶入；變數不存在視為錯誤。設定檔有誤時丟 <see cref="InvalidDataException"/>。
/// </summary>
public sealed partial class StudioConfigProvider(string path, Func<string, string?>? env = null)
{
    readonly Func<string, string?> env = env ?? Environment.GetEnvironmentVariable;
    readonly object gate = new();
    (DateTime, long) stamp;
    StudioConfig? current;

    public StudioConfig Current
    {
        get
        {
            lock (gate)
            {
                var f = new FileInfo(path);
                if (!f.Exists) throw new InvalidDataException($"設定檔不存在：{path}");
                var s = (f.LastWriteTimeUtc, f.Length);
                if (current is null || s != stamp) { current = Parse(File.ReadAllText(path)); stamp = s; }
                return current;
            }
        }
    }

    StudioConfig Parse(string json)
    {
        JsonElement root;
        try { root = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip }).RootElement; }
        catch (JsonException e) { throw new InvalidDataException($"設定檔不是合法 JSON：{e.Message}", e); }

        var servers = new Dictionary<string, McpServerConfig>();
        foreach (var (id, e) in Props(root, "mcpServers"))
        {
            var type = Str(e, "type") ?? throw new InvalidDataException($"mcpServers.{id}: 缺少 type");
            servers[id] = type switch
            {
                "http" => new McpServerConfig(type, Sub(Str(e, "url") ?? throw new InvalidDataException($"mcpServers.{id}: http 需要 url")),
                    Map(e, "headers"), null, [], new Dictionary<string, string>()),
                "stdio" => new McpServerConfig(type, null, new Dictionary<string, string>(),
                    Sub(Str(e, "command") ?? throw new InvalidDataException($"mcpServers.{id}: stdio 需要 command")),
                    e.TryGetProperty("args", out var a) ? a.EnumerateArray().Select(x => Sub(x.GetString() ?? "")).ToList() : [], Map(e, "env")),
                _ => throw new InvalidDataException($"mcpServers.{id}: type 只支援 http 與 stdio，不支援 {type}"),
            };
        }
        var systems = Props(root, "systems").ToDictionary(p => p.Key, p => p.Value.GetString() ?? "");
        var depts = root.TryGetProperty("cimDeptCodes", out var d) ? d.EnumerateArray().Select(x => x.GetString() ?? "").ToList() : [];
        return new StudioConfig(servers, systems, depts);
    }

    static IEnumerable<KeyValuePair<string, JsonElement>> Props(JsonElement e, string name) =>
        e.TryGetProperty(name, out var o) ? o.EnumerateObject().Select(p => KeyValuePair.Create(p.Name, p.Value)) : [];

    static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) ? v.GetString() : null;

    Dictionary<string, string> Map(JsonElement e, string name) =>
        Props(e, name).ToDictionary(p => p.Key, p => Sub(p.Value.GetString() ?? ""));

    string Sub(string s) => VarPattern().Replace(s, m =>
        env(m.Groups[1].Value) ?? throw new InvalidDataException($"環境變數 {m.Groups[1].Value} 未設定"));

    [GeneratedRegex(@"\$\{([A-Za-z_][A-Za-z0-9_]*)\}")]
    private static partial Regex VarPattern();
}
