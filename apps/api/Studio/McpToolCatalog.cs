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
public sealed record McpServerTools(string Server, string Type, IReadOnlyList<McpToolInfo> Tools, string? Error, DateTimeOffset? FetchedAt, bool TimedOut = false);

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
    /// <summary>單一 server 連線＋列舉的上限；逾時視同連不上（測試可調短）。</summary>
    public TimeSpan ServerTimeout { get; set; } = TimeSpan.FromSeconds(10);
    readonly ConcurrentDictionary<string, (DateTimeOffset At, McpServerTools Result)> cache = new();

    public async Task<IReadOnlyList<McpServerTools>> ListAsync(CancellationToken ct = default, bool refresh = false)
    {
        // 平行列舉；Task.WhenAll 依輸入順序回傳，維持設定檔順序
        return await Task.WhenAll(config.Current.McpServers.Select(s => ListServerAsync(s.Key, s.Value, ct, refresh)));
    }

    /// <summary>只回設定中的 server id 與 type，不連線。</summary>
    public IReadOnlyList<(string Id, string Type)> ListServerIds() => [.. config.Current.McpServers.Select(s => (s.Key, s.Value.Type))];

    /// <summary>單一 server 的工具清單，走快取（refresh 略過）。找不到設定回 null。</summary>
    public async Task<McpServerTools?> GetAsync(string id, CancellationToken ct = default, bool refresh = false) =>
        config.Current.McpServers.TryGetValue(id, out var cfg) ? await ListServerAsync(id, cfg, ct, refresh) : null;

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
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ServerTimeout);
        try
        {
            await using var client = await McpClient.CreateAsync(transports.Create(id, cfg), cancellationToken: timeout.Token);
            var tools = await client.ListToolsAsync(cancellationToken: timeout.Token);
            r = new McpServerTools(id, cfg.Type, [.. tools.Select(t => new McpToolInfo(
                id, t.Name, $"{id}__{t.Name}", t.Description, t.ProtocolTool.InputSchema,
                t.ProtocolTool.OutputSchema, t.ProtocolTool.Annotations))], null, time.GetUtcNow());
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            var timedOut = e is OperationCanceledException;
            if (timedOut) e = new TimeoutException($"列舉逾時（{ServerTimeout.TotalSeconds:0.#} 秒）");
            return new McpServerTools(id, cfg.Type, [], e.Message, null, timedOut); // 失敗不進快取，下次再試
        }
        cache[key] = (r.FetchedAt!.Value, r);
        return r;
    }
}
