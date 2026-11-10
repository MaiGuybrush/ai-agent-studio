using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AiAgentStudio.Api.Interviews;
using AiAgentStudio.Api.Studio;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelContextProtocol.Protocol;

namespace AiAgentStudio.Api.Tests;

/// <summary>#21：Tool Spec 草稿（Reuse／Extend／New），沿用 #20 的人工修改優先機制。回應的 "steps" 即 Tool Spec 清單（共用 DraftView）。</summary>
public class ToolSpecDraftTests : IDisposable
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
                    ["gw"] = [new Tool { Name = "get_part", Description = "查詢料件", InputSchema = McpToolCatalogTests.Obj("{\"id\":{\"type\":\"string\"}}") }],
                }));
            });
        }
    }

    Factory Make()
    {
        File.WriteAllText(path, """
        { "mcpServers": { "gw": { "type": "http", "url": "http://gw/mcp" } },
          "systems": { "MES": "10000001", "ERP": "10000002" } }
        """);
        return new Factory(path);
    }

    static HttpClient Agent(InterviewFactory f, string ticket = "T-1", string who = "CIM1")
    {
        var c = f.CreateClient();
        c.DefaultRequestHeaders.Add("X-Run-Token", f.Services.GetRequiredService<RunTokenService>().Issue(ticket, who));
        return c;
    }
    static Task<HttpResponseMessage> Upsert(HttpClient c, string id, object body) => c.PutAsJsonAsync($"/internal/tools/tool-specs/{id}", body);
    static async Task<JsonElement> Draft(InterviewFactory f, string who = "CIM1") => await TicketFactory.Json(await f.As(who).GetAsync("/api/tickets/T-1/tool-spec-draft"));
    static Task<HttpResponseMessage> Human(InterviewFactory f, int version, object specs, string who = "CIM1") =>
        f.As(who).PutAsJsonAsync("/api/tickets/T-1/tool-spec-draft", new { version, steps = specs });

    static readonly object Str = new { type = "string" };
    static object NewSpec(string purpose = "查工單") => new
    {
        kind = "New", purpose, system = "MES", system_owner = "10000001", side_effect = "read",
        inputs = new { type = "object", properties = new { work_order_no = Str }, required = new[] { "work_order_no" } },
        outputs = new { type = "object", properties = new { status = Str } },
    };

    [Fact]
    public async Task Agent_creates_spec_and_reports_missing_required_fields()
    {
        using var f = Make(); await f.Seed();
        var a = Agent(f);
        var r = await Upsert(a, "ts1", new { kind = "New", purpose = "查工單", system = "MES" });
        r.EnsureSuccessStatusCode();
        var missing = (await TicketFactory.Json(r)).GetProperty("missing").EnumerateArray().Select(x => x.GetString()).Order().ToArray();
        Assert.Equal(["inputs", "outputs", "side_effect", "system_owner"], missing);

        Assert.Equal(HttpStatusCode.OK, (await Upsert(a, "ts1", NewSpec())).StatusCode);
        var d = await Draft(f);
        Assert.Empty(d.GetProperty("missing").GetProperty("ts1").EnumerateArray());
        Assert.Equal("查工單", d.GetProperty("steps")[0].GetProperty("purpose").GetString());
    }
}
