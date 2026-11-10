using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace AiAgentStudio.Api.Studio;

/// <summary>一個 MCP 工具的描述（spec 4.1）。QualifiedName 以 server key 當前綴，跨 server 同名不會混淆。</summary>
public sealed record McpToolInfo(
    string Server, string Name, string QualifiedName, string? Description,
    JsonElement InputSchema, JsonElement? OutputSchema, ToolAnnotations? Annotations);

/// <summary>一個 server 的列舉結果；連不上時 Error 有值、Tools 為空，不影響其他 server。</summary>
public sealed record McpServerTools(string Server, IReadOnlyList<McpToolInfo> Tools, string? Error);

/// <summary>依設定建立 SDK 的 transport；獨立出來讓測試換成記憶體內的假 server。</summary>
public interface IMcpTransportFactory
{
    IClientTransport Create(string serverId, McpServerConfig cfg);
}

public sealed class McpTransportFactory : IMcpTransportFactory
{
    public IClientTransport Create(string serverId, McpServerConfig cfg) => cfg.Type switch
    {
        "http" => new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(cfg.Url!), Name = serverId, TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = cfg.Headers.ToDictionary(h => h.Key, h => h.Value),
        }),
        _ => new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = serverId, Command = cfg.Command!, Arguments = [.. cfg.Args],
            EnvironmentVariables = cfg.Env.ToDictionary(e => e.Key, e => (string?)e.Value),
        }),
    };
}

/// <summary>
/// 以官方 SDK（McpClient.CreateAsync → ListToolsAsync）列舉各 server 的工具，結果存記憶體。
/// 快取 key = server id + 該 server 的連線設定（URL、header、command、env 即憑證身分），憑證一變就不會沿用舊清單；
/// 另有 <see cref="Ttl"/> 到期重抓。
/// </summary>
public sealed class McpToolCatalog(StudioConfigProvider config, IMcpTransportFactory transports, TimeProvider time)
{
    public static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);
    readonly ConcurrentDictionary<string, (DateTimeOffset At, McpServerTools Result)> cache = new();

    public async Task<IReadOnlyList<McpServerTools>> ListAsync(CancellationToken ct = default)
    {
        var result = new List<McpServerTools>();
        // ponytail: 依序列舉；server 很多或很慢時再改平行
        foreach (var (id, cfg) in config.Current.McpServers) result.Add(await ListServerAsync(id, cfg, ct));
        return result;
    }

    /// <summary>單一 server 的最新工具清單：略過快取直接重新列舉（結果會更新快取）。找不到該 server 設定回 null。供實作檢查用（spec 6.4）。</summary>
    public async Task<McpServerTools?> ListFreshAsync(string id, CancellationToken ct = default) =>
        config.Current.McpServers.TryGetValue(id, out var cfg) ? await ListServerAsync(id, cfg, ct, fresh: true) : null;

    async Task<McpServerTools> ListServerAsync(string id, McpServerConfig cfg, CancellationToken ct, bool fresh = false)
    {
        var key = $"{id}|{cfg.Type}|{cfg.Url}|{cfg.Command}|{string.Join('\u0001', cfg.Args)}|" +
                  string.Join('\u0001', cfg.Headers.Select(h => $"{h.Key}={h.Value}")) + "|" +
                  string.Join('\u0001', cfg.Env.Select(e => $"{e.Key}={e.Value}"));
        if (!fresh && cache.TryGetValue(key, out var hit) && time.GetUtcNow() - hit.At < Ttl) return hit.Result;

        McpServerTools r;
        try
        {
            await using var client = await McpClient.CreateAsync(transports.Create(id, cfg), cancellationToken: ct);
            var tools = await client.ListToolsAsync(cancellationToken: ct);
            r = new McpServerTools(id, [.. tools.Select(t => new McpToolInfo(
                id, t.Name, $"{id}__{t.Name}", t.Description, t.ProtocolTool.InputSchema,
                t.ProtocolTool.OutputSchema, t.ProtocolTool.Annotations))], null);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return new McpServerTools(id, [], e.Message); // 失敗不進快取，下次再試
        }
        cache[key] = (time.GetUtcNow(), r);
        return r;
    }
}
