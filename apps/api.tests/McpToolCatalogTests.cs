using System.IO.Pipelines;
using System.Text.Json;
using AiAgentStudio.Api.Studio;
using Microsoft.Extensions.Time.Testing;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AiAgentStudio.Api.Tests;

/// <summary>
/// 以記憶體內的假 MCP server 驗證工具列舉（真的 McpGateway 在此環境連不到，見 issue 留言）。
/// 走的是真的 SDK：McpClient.CreateAsync → ListToolsAsync。
/// </summary>
public class McpToolCatalogTests : IDisposable
{
    readonly string path = Path.Combine(Path.GetTempPath(), $"studio-{Guid.NewGuid():N}.json");
    public void Dispose() => File.Delete(path);

    /// <summary>每個 server id 一組假工具；記錄建立連線的次數與收到的設定。</summary>
    public class FakeServers(Dictionary<string, Tool[]> tools) : IMcpTransportFactory
    {
        public List<(string Id, McpServerConfig Cfg)> Connections = [];

        public IClientTransport Create(string serverId, McpServerConfig cfg)
        {
            Connections.Add((serverId, cfg));
            if (!tools.TryGetValue(serverId, out var t)) throw new InvalidOperationException("連線失敗");
            Pipe c2s = new(), s2c = new();
            var server = McpServer.Create(new StreamServerTransport(c2s.Reader.AsStream(), s2c.Writer.AsStream()),
                new McpServerOptions { Handlers = new() { ListToolsHandler = (_, _) => ValueTask.FromResult(new ListToolsResult { Tools = [.. t] }) } });
            _ = server.RunAsync();
            return new StreamClientTransport(c2s.Writer.AsStream(), s2c.Reader.AsStream());
        }
    }

    public static JsonElement Obj(string props) => JsonDocument.Parse("{\"type\":\"object\",\"properties\":" + props + "}").RootElement.Clone();

    public static Tool[] Tools(params string[] names) =>
        [.. names.Select(n => new Tool { Name = n, Description = $"{n} 說明", InputSchema = Obj("{}") })];

    (McpToolCatalog cat, FakeServers servers, FakeTimeProvider time) Make(string json, Dictionary<string, Tool[]> tools)
    {
        File.WriteAllText(path, json);
        var servers = new FakeServers(tools);
        var time = new FakeTimeProvider();
        return (new McpToolCatalog(new StudioConfigProvider(path, _ => "k"), servers, time), servers, time);
    }

    const string TwoServers = """
    { "mcpServers": {
        "a": { "type": "http", "url": "http://a/mcp", "headers": { "X-Api-Key": "${K}" } },
        "b": { "type": "http", "url": "http://b/mcp" } } }
    """;

    [Fact]
    public async Task Lists_name_description_schemas_and_annotations()
    {
        var (cat, _, _) = Make("""{ "mcpServers": { "a": { "type": "http", "url": "http://a/mcp" } } }""",
            new()
            {
                ["a"] = [new Tool
                {
                    Name = "get_part", Description = "查詢料件", InputSchema = Obj("{\"id\":{\"type\":\"string\"}}"),
                    OutputSchema = Obj("{\"qty\":{\"type\":\"integer\"}}"), Annotations = new ToolAnnotations { ReadOnlyHint = true },
                }],
            });

        var s = Assert.Single(await cat.ListAsync());
        var t = Assert.Single(s.Tools);
        Assert.Equal("a", s.Server);
        Assert.Equal("get_part", t.Name);
        Assert.Equal("查詢料件", t.Description);
        Assert.Equal("a__get_part", t.QualifiedName);
        Assert.True(t.InputSchema.GetProperty("properties").TryGetProperty("id", out _));
        Assert.True(t.OutputSchema?.GetProperty("properties").TryGetProperty("qty", out _));
        Assert.True(t.Annotations?.ReadOnlyHint);
    }

    [Fact]
    public async Task Same_tool_name_on_two_servers_is_told_apart_by_server_key()
    {
        var (cat, _, _) = Make(TwoServers, new() { ["a"] = Tools("ping"), ["b"] = Tools("ping") });

        var all = (await cat.ListAsync()).SelectMany(s => s.Tools).Select(t => t.QualifiedName).Order().ToList();
        Assert.Equal(["a__ping", "b__ping"], all);
    }

    [Fact]
    public async Task One_unreachable_server_does_not_hide_the_others()
    {
        var (cat, _, _) = Make(TwoServers, new() { ["b"] = Tools("ping") });

        var r = await cat.ListAsync();
        Assert.Contains("連線失敗", r.Single(s => s.Server == "a").Error);
        Assert.Empty(r.Single(s => s.Server == "a").Tools);
        Assert.Single(r.Single(s => s.Server == "b").Tools);
    }

    [Fact]
    public async Task Result_is_cached_until_ttl_then_refetched()
    {
        var (cat, servers, time) = Make(TwoServers, new() { ["a"] = Tools("x"), ["b"] = Tools("y") });

        await cat.ListAsync(); await cat.ListAsync();
        Assert.Equal(2, servers.Connections.Count);

        time.Advance(McpToolCatalog.Ttl + TimeSpan.FromSeconds(1));
        await cat.ListAsync();
        Assert.Equal(4, servers.Connections.Count);
    }

    [Fact]
    public async Task Cache_key_includes_credential_identity()
    {
        var (cat, servers, _) = Make(TwoServers, new() { ["a"] = Tools("x"), ["b"] = Tools("y") });
        await cat.ListAsync();

        // 同一個 server id 換了憑證（改檔）＝不同身分，不可沿用舊快取
        File.WriteAllText(path, TwoServers.Replace("${K}", "k2"));
        await cat.ListAsync();
        Assert.Equal(3, servers.Connections.Count); // 只有憑證變了的 a 重連，b 沿用快取
        Assert.Equal("k", servers.Connections[0].Cfg.Headers["X-Api-Key"]);
        Assert.Equal("k2", servers.Connections[2].Cfg.Headers["X-Api-Key"]);
    }
}
