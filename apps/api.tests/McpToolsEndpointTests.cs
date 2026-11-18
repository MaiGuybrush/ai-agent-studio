using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using AiAgentStudio.Api.Services;
using AiAgentStudio.Api.Studio;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;
using ModelContextProtocol.Protocol;

namespace AiAgentStudio.Api.Tests;

/// <summary>#41：GET /api/mcp-tools（MCP Tool Catalog 頁的資料來源）。走 HTTP seam，用真的 JwtBearer 管線與記憶體假 MCP server。</summary>
public class McpToolsEndpointTests : IDisposable
{
    static readonly RsaSecurityKey Key = new(System.Security.Cryptography.RSA.Create(2048));
    readonly string path = Path.Combine(Path.GetTempPath(), $"studio-{Guid.NewGuid():N}.json");
    public void Dispose() => File.Delete(path);

    class FakeJwks : IJwksProvider
    {
        public IEnumerable<SecurityKey> GetSigningKeys(string? kid = null) => [Key];
        public Task<IEnumerable<SecurityKey>> GetSigningKeysAsync(string? kid = null, CancellationToken ct = default) =>
            Task.FromResult<IEnumerable<SecurityKey>>([Key]);
    }

    class Factory(string path) : WebApplicationFactory<Program>
    {
        public readonly Microsoft.Extensions.Time.Testing.FakeTimeProvider Time = new();
        public McpToolCatalogTests.FakeServers Servers = null!;
        protected override void ConfigureWebHost(IWebHostBuilder b)
        {
            b.UseEnvironment("Testing");
            b.ConfigureServices(s =>
            {
                s.RemoveAll<IJwksProvider>(); s.AddSingleton<IJwksProvider, FakeJwks>();
                s.RemoveAll<StudioConfigProvider>(); s.AddSingleton(new StudioConfigProvider(path, _ => "k"));
                s.RemoveAll<IMcpTransportFactory>();
                s.RemoveAll<TimeProvider>(); s.AddSingleton<TimeProvider>(Time);
                s.AddSingleton<IMcpTransportFactory>(Servers = new McpToolCatalogTests.FakeServers(new()
                {
                    ["b"] = [new Tool { Name = "Zeta", Description = "第一行\n第二行", InputSchema = McpToolCatalogTests.Obj("{\"id\":{\"type\":\"string\"}}"),
                        OutputSchema = McpToolCatalogTests.Obj("{}"), Annotations = new ToolAnnotations { ReadOnlyHint = true } }],
                    ["a"] = McpToolCatalogTests.Tools("x"),
                    // "down" 不在假 server 表內 → 連線失敗
                }));
            });
        }
    }

    const string Config = """
    { "mcpServers": {
        "b": { "type": "http", "url": "http://secret-host/mcp", "headers": { "X-Api-Key": "TOPSECRET-HEADER" } },
        "down": { "type": "stdio", "command": "secret-cmd", "args": ["--secret-arg"], "env": { "TOKEN": "TOPSECRET-ENV" } },
        "a": { "type": "http", "url": "http://a/mcp" } } }
    """;

    static string Token() => new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
        claims: [new Claim("empNo", "E1")], expires: DateTime.UtcNow.AddHours(1),
        signingCredentials: new SigningCredentials(Key, SecurityAlgorithms.RsaSha256)));

    [Fact]
    public async Task Anonymous_is_401()
    {
        File.WriteAllText(path, Config);
        using var f = new Factory(path);
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.CreateClient().GetAsync("/api/mcp-tools")).StatusCode);
    }

    [Fact]
    public async Task Lists_servers_in_config_order_with_error_and_never_leaks_connection_settings()
    {
        File.WriteAllText(path, Config);
        using var f = new Factory(path);
        var c = f.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token());
        var r = await c.GetAsync("/api/mcp-tools");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var raw = await r.Content.ReadAsStringAsync();
        foreach (var s in new[] { "TOPSECRET", "secret-host", "secret-cmd", "secret-arg", "\"headers\"", "\"env\"", "\"url\"", "\"command\"", "\"args\"" })
            Assert.DoesNotContain(s, raw);

        var servers = JsonDocument.Parse(raw).RootElement.GetProperty("servers");
        Assert.Equal(["b", "down", "a"], servers.EnumerateArray().Select(s => s.GetProperty("id").GetString()));
        var b = servers[0];
        Assert.Equal("http", b.GetProperty("type").GetString());
        Assert.Equal(JsonValueKind.Null, b.GetProperty("error").ValueKind);
        var t = b.GetProperty("tools")[0];
        Assert.Equal("Zeta", t.GetProperty("name").GetString());
        Assert.Equal("b__Zeta", t.GetProperty("qualifiedName").GetString());
        Assert.Equal("第一行\n第二行", t.GetProperty("description").GetString());
        Assert.Equal("string", t.GetProperty("inputSchema").GetProperty("properties").GetProperty("id").GetProperty("type").GetString());
        Assert.Equal("object", t.GetProperty("outputSchema").GetProperty("type").GetString());
        Assert.True(t.GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean());

        var down = servers[1];
        Assert.Equal("stdio", down.GetProperty("type").GetString());
        Assert.Equal("無法連線", down.GetProperty("error").GetString()); // 例外訊息含 command/args，不外洩
        Assert.Equal(JsonValueKind.Null, down.GetProperty("fetchedAt").ValueKind);
        Assert.Equal(0, down.GetProperty("tools").GetArrayLength());
        Assert.Equal(1, servers[2].GetProperty("tools").GetArrayLength());
    }

    static async Task<JsonElement> Get(HttpClient c, string url) =>
        JsonDocument.Parse(await c.GetStringAsync(url)).RootElement.GetProperty("servers").Clone();

    [Fact]
    public async Task FetchedAt_is_kept_on_cache_hit_and_renewed_by_refresh_and_failed_server_is_not_cached()
    {
        File.WriteAllText(path, Config);
        using var f = new Factory(path);
        var c = f.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token());
        var t0 = f.Time.GetUtcNow();

        var first = await Get(c, "/api/mcp-tools");
        Assert.Equal(t0, first[0].GetProperty("fetchedAt").GetDateTimeOffset());
        var connections = f.Servers.Connections.Count;

        f.Time.Advance(TimeSpan.FromMinutes(1));
        var cached = await Get(c, "/api/mcp-tools");
        Assert.Equal(t0, cached[0].GetProperty("fetchedAt").GetDateTimeOffset());
        Assert.Equal(t0, cached[2].GetProperty("fetchedAt").GetDateTimeOffset());
        // 命中快取的 server 不重連；失敗的 "down" 不進快取，所以只多一次連線
        Assert.Equal(connections + 1, f.Servers.Connections.Count);

        var refreshed = await Get(c, "/api/mcp-tools?refresh=true");
        var t1 = f.Time.GetUtcNow();
        Assert.Equal(t1, refreshed[0].GetProperty("fetchedAt").GetDateTimeOffset());
        Assert.Equal(t1, refreshed[2].GetProperty("fetchedAt").GetDateTimeOffset());
        Assert.Equal("無法連線", refreshed[1].GetProperty("error").GetString());
    }

    [Fact]
    public async Task Timed_out_server_shows_generic_timeout_error()
    {
        File.WriteAllText(path, Config);
        using var f = new Factory(path);
        f.Services.GetRequiredService<McpToolCatalog>().ServerTimeout = TimeSpan.FromMilliseconds(300);
        f.Servers.Hang.Add("a");
        var c = f.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token());
        var a = (await Get(c, "/api/mcp-tools"))[2];
        Assert.Equal("連線逾時", a.GetProperty("error").GetString());
        Assert.Equal(JsonValueKind.Null, a.GetProperty("fetchedAt").ValueKind);
    }

    HttpClient Authed(Factory f)
    {
        var c = f.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token());
        return c;
    }

    [Fact]
    public async Task Server_list_returns_only_id_and_type_without_connecting()
    {
        File.WriteAllText(path, Config);
        using var f = new Factory(path);
        var c = Authed(f);
        var raw = await c.GetStringAsync("/api/mcp-tools/servers");
        Assert.Empty(f.Servers.Connections);
        foreach (var s in new[] { "TOPSECRET", "secret-host", "secret-cmd", "secret-arg", "\"headers\"", "\"env\"", "\"url\"", "\"command\"", "\"args\"" })
            Assert.DoesNotContain(s, raw);
        var servers = JsonDocument.Parse(raw).RootElement.GetProperty("servers");
        Assert.Equal(["b", "down", "a"], servers.EnumerateArray().Select(s => s.GetProperty("id").GetString()));
        Assert.Equal("stdio", servers[1].GetProperty("type").GetString());
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.CreateClient().GetAsync("/api/mcp-tools/servers")).StatusCode);
    }

    [Fact]
    public async Task Single_server_returns_tools_or_error_and_unknown_id_is_404()
    {
        File.WriteAllText(path, Config);
        using var f = new Factory(path);
        var c = Authed(f);
        var b = JsonDocument.Parse(await c.GetStringAsync("/api/mcp-tools/servers/b")).RootElement;
        Assert.Equal("b", b.GetProperty("id").GetString());
        Assert.Equal("b__Zeta", b.GetProperty("tools")[0].GetProperty("qualifiedName").GetString());
        var down = JsonDocument.Parse(await c.GetStringAsync("/api/mcp-tools/servers/down")).RootElement;
        Assert.Equal("無法連線", down.GetProperty("error").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/api/mcp-tools/servers/nope")).StatusCode);
    }

    [Fact]
    public async Task Single_server_hits_cache_and_refresh_bypasses_it()
    {
        File.WriteAllText(path, Config);
        using var f = new Factory(path);
        var c = Authed(f);
        await c.GetStringAsync("/api/mcp-tools/servers/a");
        var n = f.Servers.Connections.Count;
        await c.GetStringAsync("/api/mcp-tools/servers/a");
        Assert.Equal(n, f.Servers.Connections.Count);
        await c.GetStringAsync("/api/mcp-tools/servers/a?refresh=true");
        Assert.Equal(n + 1, f.Servers.Connections.Count);
    }

    [Fact]
    public async Task Single_server_timeout_does_not_affect_others()
    {
        File.WriteAllText(path, Config);
        using var f = new Factory(path);
        f.Services.GetRequiredService<McpToolCatalog>().ServerTimeout = TimeSpan.FromMilliseconds(300);
        f.Servers.Hang.Add("a");
        var c = Authed(f);
        var a = JsonDocument.Parse(await c.GetStringAsync("/api/mcp-tools/servers/a")).RootElement;
        Assert.Equal("連線逾時", a.GetProperty("error").GetString());
        var b = JsonDocument.Parse(await c.GetStringAsync("/api/mcp-tools/servers/b")).RootElement;
        Assert.Equal(JsonValueKind.Null, b.GetProperty("error").ValueKind);
    }
}
