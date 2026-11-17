using System.Net;
using System.Text.Json;
using AiAgentStudio.Api.Interviews;
using AiAgentStudio.Api.Studio;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelContextProtocol.Protocol;

namespace AiAgentStudio.Api.Tests;

/// <summary>#19：agent 的唯讀工具 list_mcp_tools、list_systems（C# 內部 tool API），只認 run token。</summary>
public class ToolApiReadOnlyTests : IDisposable
{
    readonly string path = Path.Combine(Path.GetTempPath(), $"studio-{Guid.NewGuid():N}.json");
    public void Dispose() => File.Delete(path);

    class Factory(string path) : InterviewFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder b)
        {
            base.ConfigureWebHost(b);
            b.ConfigureServices(s =>
            {
                s.RemoveAll<StudioConfigProvider>(); s.AddSingleton(new StudioConfigProvider(path, _ => "k"));
                s.RemoveAll<IMcpTransportFactory>();
                s.AddSingleton<IMcpTransportFactory>(new McpToolCatalogTests.FakeServers(new()
                {
                    ["gw"] = [new Tool { Name = "get_part", Description = "查詢料件", InputSchema = McpToolCatalogTests.Obj("{}") }],
                    ["slow"] = McpToolCatalogTests.Tools("x"), ["s1"] = McpToolCatalogTests.Tools("x"), ["s2"] = McpToolCatalogTests.Tools("x"),
                }));
            });
        }
    }

    HttpClient Anon(Factory f) => f.CreateClient();
    static HttpRequestMessage Get(string url, string? token)
    {
        var r = new HttpRequestMessage(HttpMethod.Get, url);
        if (token != null) r.Headers.Add("X-Run-Token", token);
        return r;
    }

    [Fact]
    public async Task list_mcp_tools_and_list_systems_return_config_data()
    {
        File.WriteAllText(path, """
        { "mcpServers": { "gw": { "type": "http", "url": "http://gw/mcp" } },
          "systems": { "MES": "10000001", "ERP": "10000002" } }
        """);
        using var f = new Factory(path); await f.Seed();
        var token = f.Services.GetRequiredService<RunTokenService>().Issue("T-1", "CIM1");

        var tools = await TicketFactory.Json(await Anon(f).SendAsync(Get("/internal/tools/mcp-tools", token)));
        var gw = tools.GetProperty("servers")[0];
        Assert.Equal("gw", gw.GetProperty("server").GetString());
        var t = gw.GetProperty("tools")[0];
        Assert.Equal("gw__get_part", t.GetProperty("qualifiedName").GetString());
        Assert.Equal("查詢料件", t.GetProperty("description").GetString());
        Assert.Equal("object", t.GetProperty("inputSchema").GetProperty("type").GetString());

        var systems = await TicketFactory.Json(await Anon(f).SendAsync(Get("/internal/tools/systems", token)));
        Assert.Equal(["ERP:10000002", "MES:10000001"],
            systems.EnumerateArray().Select(s => $"{s.GetProperty("system").GetString()}:{s.GetProperty("ownerEmpNo").GetString()}").Order());
    }

    [Fact]
    public async Task Hung_server_times_out_with_error_while_others_return_in_config_order_and_hung_one_is_retried()
    {
        File.WriteAllText(path, """
        { "mcpServers": { "slow": { "type": "http", "url": "http://slow/mcp" }, "gw": { "type": "http", "url": "http://gw/mcp" } } }
        """);
        using var f = new Factory(path); await f.Seed();
        var fake = (McpToolCatalogTests.FakeServers)f.Services.GetRequiredService<IMcpTransportFactory>();
        fake.Hang.Add("slow");
        f.Services.GetRequiredService<McpToolCatalog>().ServerTimeout = TimeSpan.FromMilliseconds(500);
        var token = f.Services.GetRequiredService<RunTokenService>().Issue("T-1", "CIM1");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var servers = (await TicketFactory.Json(await Anon(f).SendAsync(Get("/internal/tools/mcp-tools", token)))).GetProperty("servers");
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5));
        Assert.Equal(["slow", "gw"], servers.EnumerateArray().Select(s => s.GetProperty("server").GetString()));
        Assert.NotNull(servers[0].GetProperty("error").GetString());
        Assert.Equal(0, servers[0].GetProperty("tools").GetArrayLength());
        Assert.Equal(1, servers[1].GetProperty("tools").GetArrayLength());

        fake.Hang.Clear(); // 逾時不進快取：下次直接重試成功
        servers = (await TicketFactory.Json(await Anon(f).SendAsync(Get("/internal/tools/mcp-tools", token)))).GetProperty("servers");
        Assert.Equal(JsonValueKind.Null, servers[0].GetProperty("error").ValueKind);
    }

    [Fact]
    public async Task Servers_are_listed_in_parallel()
    {
        File.WriteAllText(path, """
        { "mcpServers": { "s1": { "type": "http", "url": "http://1/mcp" }, "s2": { "type": "http", "url": "http://2/mcp" } } }
        """);
        using var f = new Factory(path); await f.Seed();
        var fake = (McpToolCatalogTests.FakeServers)f.Services.GetRequiredService<IMcpTransportFactory>();
        fake.Hang.UnionWith(["s1", "s2"]);
        f.Services.GetRequiredService<McpToolCatalog>().ServerTimeout = TimeSpan.FromSeconds(1);
        var token = f.Services.GetRequiredService<RunTokenService>().Issue("T-1", "CIM1");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await Anon(f).SendAsync(Get("/internal/tools/mcp-tools", token));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1.8), $"sequential? {sw.Elapsed}"); // 序列會 >= 2 秒
    }

    [Theory]
    [InlineData("/internal/tools/mcp-tools")]
    [InlineData("/internal/tools/systems")]
    public async Task Rejects_missing_bad_and_user_jwt_tokens(string url)
    {
        File.WriteAllText(path, "{}");
        using var f = new Factory(path); await f.Seed();
        var c = Anon(f);

        Assert.Equal(HttpStatusCode.Unauthorized, (await c.SendAsync(Get(url, null))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.SendAsync(Get(url, "garbage"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.SendAsync(Get(url, TicketFactory.Token("CIM1")))).StatusCode);
    }
}
