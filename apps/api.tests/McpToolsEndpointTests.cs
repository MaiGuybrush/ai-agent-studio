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

/// <summary>#41：GET /api/mcp-tools/servers[/{id}]（MCP Tool Catalog 頁的資料來源）。走 HTTP seam，用真的 JwtBearer 管線與記憶體假 MCP server。</summary>
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

    HttpClient Authed(Factory f)
    {
        var c = f.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token());
        return c;
    }

    static async Task<JsonElement> Server(HttpClient c, string id, bool refresh = false) =>
        JsonDocument.Parse(await c.GetStringAsync($"/api/mcp-tools/servers/{id}{(refresh ? "?refresh=true" : "")}")).RootElement.Clone();

    [Fact]
    public async Task Old_aggregate_endpoint_is_gone()
    {
        File.WriteAllText(path, Config);
        using var f = new Factory(path);
        Assert.Equal(HttpStatusCode.NotFound, (await Authed(f).GetAsync("/api/mcp-tools")).StatusCode);
    }

    [Fact]
    public async Task Single_server_requires_auth_and_never_leaks_connection_settings()
    {
        File.WriteAllText(path, Config);
        using var f = new Factory(path);
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.CreateClient().GetAsync("/api/mcp-tools/servers/b")).StatusCode);
        var c = Authed(f);
        foreach (var id in new[] { "b", "down" })
        {
            var raw = await c.GetStringAsync($"/api/mcp-tools/servers/{id}");
            foreach (var s in new[] { "TOPSECRET", "secret-host", "secret-cmd", "secret-arg", "\"headers\"", "\"env\"", "\"url\"", "\"command\"", "\"args\"" })
                Assert.DoesNotContain(s, raw);
        }
        var t = (await Server(c, "b")).GetProperty("tools")[0];
        Assert.Equal("Zeta", t.GetProperty("name").GetString());
        Assert.Equal("第一行\n第二行", t.GetProperty("description").GetString());
        Assert.Equal("string", t.GetProperty("inputSchema").GetProperty("properties").GetProperty("id").GetProperty("type").GetString());
        Assert.Equal("object", t.GetProperty("outputSchema").GetProperty("type").GetString());
        Assert.True(t.GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean());
        var down = await Server(c, "down");
        Assert.Equal("stdio", down.GetProperty("type").GetString());
        Assert.Equal(JsonValueKind.Null, down.GetProperty("fetchedAt").ValueKind);
        Assert.Equal(0, down.GetProperty("tools").GetArrayLength());
    }

    [Fact]
    public async Task FetchedAt_is_kept_on_cache_hit_and_renewed_by_refresh()
    {
        File.WriteAllText(path, Config);
        using var f = new Factory(path);
        var c = Authed(f);
        var t0 = f.Time.GetUtcNow();
        Assert.Equal(t0, (await Server(c, "a")).GetProperty("fetchedAt").GetDateTimeOffset());
        f.Time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(t0, (await Server(c, "a")).GetProperty("fetchedAt").GetDateTimeOffset());
        Assert.Equal(f.Time.GetUtcNow(), (await Server(c, "a", refresh: true)).GetProperty("fetchedAt").GetDateTimeOffset());
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
