using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AiAgentStudio.Api.Notifications;
using AiAgentStudio.Api.Studio;
using AiAgentStudio.Api.Submissions;
using AiAgentStudio.Api.Tickets;
using AiAgentStudio.Api.Vocabulary;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using ModelContextProtocol.Protocol;
using static AiAgentStudio.Api.Tests.VocabularyServiceTests;

namespace AiAgentStudio.Api.Tests;

/// <summary>#29：標為已實作前的 Interface Compliance Check（spec 5.3 第 7–8 點、6.4）。假 MCP server，只透過 HTTP 觀察。</summary>
public class InterfaceComplianceApiTests : IDisposable
{
    readonly string path = Path.Combine(Path.GetTempPath(), $"studio-{Guid.NewGuid():N}.json");
    public void Dispose() => File.Delete(path);

    readonly FakeSource src = new() { Sha = "sha1", Text = "class T { static object[] E = new[] { new GlossaryEntry(\"eqptId\", \"EqptId\", \"d\", new[] { \"equipmentId\" }), new GlossaryEntry(\"status\", \"Status\", \"d\", new string[0]) }; }" };
    readonly FakeTimeProvider time = new(DateTimeOffset.UtcNow);

    class FakeProposals : IVocabularyProposalClient
    {
        public Task<ProposalIssue> CreateAsync(VocabularyProposal p, CancellationToken ct) => Task.FromResult(new ProposalIssue(1, "https://gitea.test/issues/1"));
    }

    static JsonElement Schema(string json) => JsonDocument.Parse(json).RootElement.Clone();
    const string InOk = """{"type":"object","properties":{"eqptId":{"type":"string"}}}""";
    const string OutOk = """{"type":"object","properties":{"status":{"type":"string"}}}""";

    class Factory(string path, FakeSource src, TimeProvider time, Dictionary<string, Tool[]> tools) : InterviewFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder b)
        {
            base.ConfigureWebHost(b);
            b.ConfigureServices(s =>
            {
                s.RemoveAll<StudioConfigProvider>(); s.AddSingleton(new StudioConfigProvider(path, _ => "k"));
                s.RemoveAll<IVocabularySource>(); s.AddSingleton<IVocabularySource>(src);
                s.RemoveAll<IVocabularyAlert>(); s.AddSingleton<IVocabularyAlert>(new FakeAlert());
                s.RemoveAll<IVocabularyProposalClient>(); s.AddSingleton<IVocabularyProposalClient>(new FakeProposals());
                s.RemoveAll<TimeProvider>(); s.AddSingleton(time);
                s.RemoveAll<IMcpTransportFactory>(); s.AddSingleton<IMcpTransportFactory>(new McpToolCatalogTests.FakeServers(tools));
            });
        }
    }

    const string Owner1 = "10000001", Owner2 = "10000002";

    /// <summary>ts1（New）、ts2（Extend）皆為可行並標為實作中；ts3 為 Reuse（送出即已實作）。實際工具 gw/get_order 預設與 Tool Spec 相符。</summary>
    async Task<Factory> Make(Tool? actual = null)
    {
        File.WriteAllText(path, """{ "mcpServers": { "gw": { "type": "http", "url": "http://gw/mcp" } }, "systems": { "MES": "10000001" } }""");
        var tool = actual ?? new Tool { Name = "get_order", InputSchema = Schema(InOk), OutputSchema = Schema(OutOk) };
        var f = new Factory(path, src, time, new() { ["gw"] = [tool] });
        f.Names[Owner1] = "MES 負責人"; f.Names[Owner2] = "ERP 負責人";
        await f.Seed();
        var agent = f.CreateClient();
        agent.DefaultRequestHeaders.Add("X-Run-Token", f.Services.GetRequiredService<AiAgentStudio.Api.Interviews.RunTokenService>().Issue("T-1", "CIM1"));
        await agent.PutAsJsonAsync("/internal/tools/workflow-steps/s1", new { description = "查工單", tool_spec = "ts1" });
        await agent.PutAsJsonAsync("/internal/tools/tool-specs/ts1", Spec("New", Owner1));
        await agent.PutAsJsonAsync("/internal/tools/tool-specs/ts2", Spec("Extend", Owner2));
        await agent.PutAsJsonAsync("/internal/tools/tool-specs/ts3", Spec("Reuse", null));
        (await f.As("CIM1").PostAsync("/api/tickets/T-1/submit", null)).EnsureSuccessStatusCode();
        foreach (var (spec, owner) in new[] { ("ts1", Owner1), ("ts2", Owner2) })
        {
            (await f.As(owner).PutAsJsonAsync($"/api/reviews/T-1/{spec}", new { result = "Feasible", estimatedDays = 1 })).EnsureSuccessStatusCode();
            (await Act(f, "CIM1", "implementing", spec)).EnsureSuccessStatusCode();
        }
        return f;
    }

    static object Spec(string kind, string? owner) => new Dictionary<string, object?>
    {
        ["kind"] = kind, ["purpose"] = "查工單", ["system"] = "MES", ["side_effect"] = "read", ["system_owner"] = owner,
        ["inputs"] = new { type = "object", properties = new { eqptId = new { type = "string" } } },
        ["outputs"] = new { type = "object", properties = new { status = new { type = "string" } } },
    };

    static Task<HttpResponseMessage> Act(InterviewFactory f, string who, string action, string spec = "ts1", object? body = null) =>
        f.As(who).PostAsJsonAsync($"/api/tickets/T-1/specs/{spec}/{action}", body ?? new { });
    static Task<HttpResponseMessage> Implemented(InterviewFactory f, string who = "CIM1", string spec = "ts1", string server = "gw", string tool = "get_order") =>
        Act(f, who, "implemented", spec, new { serverKey = server, toolName = tool });
    static async Task<string> State(InterviewFactory f, string spec = "ts1") =>
        (await TicketFactory.Json(await f.As("CIM1").GetAsync("/api/tickets/T-1/submission"))).GetProperty("toolSpecs").EnumerateArray().First(x => x.GetProperty("specId").GetString() == spec).GetProperty("state").GetString()!;
    static async Task<string> Status(InterviewFactory f) => (await TicketFactory.Json(await f.As("CIM1").GetAsync("/api/tickets/T-1"))).GetProperty("status").GetString()!;
    static T Db<T>(InterviewFactory f, Func<StudioDb, T> q)
    {
        using var s = f.Services.CreateScope();
        return q(s.ServiceProvider.GetRequiredService<StudioDb>());
    }

    [Fact]
    public async Task Passing_check_marks_implemented_stores_a_snapshot_and_logs_the_event()
    {
        using var f = await Make();
        var r = await TicketFactory.Json(await Implemented(f));
        Assert.True(r.GetProperty("passed").GetBoolean());
        Assert.Equal("Implemented", await State(f));
        var snap = Db(f, d => d.InterfaceSnapshots.AsNoTracking().Single());
        Assert.Equal(("T-1", "ts1", "gw", "get_order"), (snap.TicketId, snap.SpecId, snap.ServerKey, snap.ToolName));
        Assert.Contains("eqptId", snap.InputSchemaJson); Assert.Contains("status", snap.OutputSchemaJson!);
        var ev = f.Events().Last();
        Assert.Equal(("ToolSpecImplemented", "ts1", "Implementing", "Implemented"), (ev.Type, ev.Reason, ev.FromValue, ev.ToValue));
        Assert.Equal("實作中", await Status(f)); // ts2 還沒好
    }

    [Fact]
    public async Task Failing_check_returns_failures_and_changes_nothing()
    {
        using var f = await Make(new Tool { Name = "get_order", InputSchema = Schema("""{"type":"object","properties":{"eqptId":{"type":"integer"}}}"""), OutputSchema = Schema(OutOk) });
        var r = await TicketFactory.Json(await Implemented(f));
        Assert.False(r.GetProperty("passed").GetBoolean());
        Assert.Contains("eqptId", r.GetProperty("failures")[0].GetString());
        Assert.Equal("Implementing", await State(f));
        Assert.Empty(Db(f, d => d.InterfaceSnapshots.ToList()));
    }

    [Theory]
    [InlineData("nope", "get_order")]   // server 不存在
    [InlineData("gw", "missing_tool")]  // 工具不存在
    public async Task Unknown_server_or_tool_fails(string server, string tool)
    {
        using var f = await Make();
        var r = await TicketFactory.Json(await Implemented(f, server: server, tool: tool));
        Assert.False(r.GetProperty("passed").GetBoolean());
        Assert.NotEmpty(r.GetProperty("failures").EnumerateArray());
        Assert.Equal("Implementing", await State(f));
    }

    [Fact]
    public async Task Extra_optional_alias_field_passes_with_a_warning()
    {
        using var f = await Make(new Tool { Name = "get_order", InputSchema = Schema("""{"type":"object","properties":{"eqptId":{"type":"string"},"equipmentId":{"type":"string"}}}"""), OutputSchema = Schema(OutOk) });
        var r = await TicketFactory.Json(await Implemented(f));
        Assert.True(r.GetProperty("passed").GetBoolean());
        Assert.Contains("equipmentId", r.GetProperty("warnings")[0].GetString());
    }

    [Fact]
    public async Task Last_implemented_spec_completes_the_ticket_and_queues_one_email_each_for_business_user_and_cim_engineer()
    {
        using var f = await Make();
        (await Implemented(f)).EnsureSuccessStatusCode();
        Assert.DoesNotContain(Db(f, d => d.EmailOutbox.ToList()), e => e.EventType == EmailEvents.Completed);
        (await Implemented(f, spec: "ts2")).EnsureSuccessStatusCode();
        Assert.Equal("已完成", await Status(f));
        var mails = Db(f, d => d.EmailOutbox.AsNoTracking().Where(e => e.EventType == EmailEvents.Completed).ToList());
        Assert.Equal(new[] { "BIZ1", "CIM1" }, mails.Select(m => m.RecipientEmpNo).Order().ToArray());
    }

    [Fact]
    public async Task Only_the_responsible_cim_engineer_can_mark_implemented_and_only_from_implementing()
    {
        using var f = await Make();
        Assert.Equal(HttpStatusCode.Forbidden, (await Implemented(f, "CIM2")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Implemented(f, Owner1)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Implemented(f, spec: "ts3")).StatusCode); // Reuse：本來就已實作
        (await Implemented(f)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, (await Implemented(f)).StatusCode); // 已實作不重做
    }
}
