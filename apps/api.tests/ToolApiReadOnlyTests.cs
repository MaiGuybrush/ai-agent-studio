using System.Net;
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
