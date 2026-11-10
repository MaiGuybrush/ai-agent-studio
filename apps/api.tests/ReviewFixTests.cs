using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AiAgentStudio.Api.Notifications;
using AiAgentStudio.Api.Studio;
using AiAgentStudio.Api.Tickets;
using AiAgentStudio.Api.Vocabulary;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using ModelContextProtocol.Protocol;
using static AiAgentStudio.Api.Tests.VocabularyServiceTests;

namespace AiAgentStudio.Api.Tests;

/// <summary>code review 修正：唯讀檢視、完成通知、ref 必填、已實作不重檢、Revise 規則、Markdown 快照、草稿／token 寫入限制。只走 HTTP。</summary>
public class ReviewFixTests : IDisposable
{
    readonly string path = Path.Combine(Path.GetTempPath(), $"studio-{Guid.NewGuid():N}.json");
    readonly string mailDir = Path.Combine(Path.GetTempPath(), $"mail-{Guid.NewGuid():N}");
    public void Dispose() { File.Delete(path); if (Directory.Exists(mailDir)) Directory.Delete(mailDir, true); }

    const string VocabA = "class T { static object[] E = new[] { new GlossaryEntry(\"eqptId\", \"EqptId\", \"d\", new[] { \"eqptIdAlias\" }), new GlossaryEntry(\"status\", \"Status\", \"d\", new[] { \"statusAlias\" }) }; }";
    readonly FakeSource src = new() { Sha = "sha1", Text = VocabA };
    readonly FakeTimeProvider time = new(DateTimeOffset.UtcNow);
    readonly Dictionary<string, Tool[]> tools = new();

    class FakeProposals : IVocabularyProposalClient
    {
        public Task<ProposalIssue> CreateAsync(VocabularyProposal p, CancellationToken ct) => Task.FromResult(new ProposalIssue(1, "https://gitea.test/issues/1"));
    }

    class Factory(string path, FakeSource src, TimeProvider time, string mailDir, Dictionary<string, Tool[]> tools) : InterviewFactory
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
                s.RemoveAll<IEmailSender>(); s.AddSingleton<IEmailSender>(new LocalFolderEmailSender(Options.Create(new EmailOptions { LocalFolder = mailDir })));
            });
        }
    }

    const string Owner1 = "10000001";
    const string InOk = """{"type":"object","properties":{"eqptId":{"type":"string"}}}""";
    const string OutOk = """{"type":"object","properties":{"status":{"type":"string"}}}""";
    static JsonElement Schema(string json) => JsonDocument.Parse(json).RootElement.Clone();
    static Tool GoodTool() => new() { Name = "get_order", InputSchema = Schema(InOk), OutputSchema = Schema(OutOk) };

    static object Spec(string kind = "New", string? owner = Owner1, bool withRef = true) => new Dictionary<string, object?>
    {
        ["kind"] = kind, ["purpose"] = "查工單", ["system"] = "MES", ["side_effect"] = "read",
        ["system_owner"] = kind == "Reuse" ? null : owner,
        ["ref"] = withRef && kind != "New" ? new { server = "gw", tool = "get_order" } : null,
        ["inputs"] = new { type = "object", properties = new { eqptId = new { type = "string" } } },
        ["outputs"] = new { type = "object", properties = new { status = new { type = "string" } } },
    };

    async Task<Factory> Make(params (string Id, object Spec)[] specs)
    {
        File.WriteAllText(path, """{ "mcpServers": { "gw": { "type": "http", "url": "http://gw/mcp" } }, "systems": { "MES": "10000001" } }""");
        tools.TryAdd("gw", [GoodTool()]);
        var f = new Factory(path, src, time, mailDir, tools);
        f.Names[Owner1] = "MES 負責人";
        await f.Seed();
        var a = Agent(f);
        await a.PutAsJsonAsync("/internal/tools/workflow-steps/s1", new { description = "查工單", tool_spec = specs.Length > 0 ? specs[0].Id : null });
        foreach (var (id, spec) in specs) (await a.PutAsJsonAsync($"/internal/tools/tool-specs/{id}", spec)).EnsureSuccessStatusCode();
        return f;
    }

    static HttpClient Agent(InterviewFactory f, string ticket = "T-1", string who = "CIM1")
    {
        var c = f.CreateClient();
        c.DefaultRequestHeaders.Add("X-Run-Token", f.Services.GetRequiredService<AiAgentStudio.Api.Interviews.RunTokenService>().Issue(ticket, who));
        return c;
    }
    static Task<HttpResponseMessage> Submit(InterviewFactory f) => f.As("CIM1").PostAsync("/api/tickets/T-1/submit", null);
    static Task<HttpResponseMessage> Act(InterviewFactory f, string who, string action, string spec = "ts1", object? body = null) =>
        f.As(who).PostAsJsonAsync($"/api/tickets/T-1/specs/{spec}/{action}", body ?? new { });
    static Task<HttpResponseMessage> Review(InterviewFactory f, object body, string spec = "ts1") => f.As(Owner1).PutAsJsonAsync($"/api/reviews/T-1/{spec}", body);
    static Task<HttpResponseMessage> Implemented(InterviewFactory f, string spec = "ts1") => Act(f, "CIM1", "implemented", spec, new { serverKey = "gw", toolName = "get_order" });
    static async Task<JsonElement> State(InterviewFactory f) => await TicketFactory.Json(await f.As("CIM1").GetAsync("/api/tickets/T-1/submission"));
    static string[] Codes(JsonElement s) => s.GetProperty("blockers").EnumerateArray().Select(b => b.GetProperty("code").GetString()!).ToArray();
    static List<EmailOutbox> Completed(InterviewFactory f)
    {
        using var s = f.Services.CreateScope();
        return s.ServiceProvider.GetRequiredService<StudioDb>().EmailOutbox.AsNoTracking().Where(e => e.EventType == EmailEvents.Completed).ToList();
    }
    static readonly object Infeasible = new { result = "Infeasible", reason = "做不到" };

    // 1. §3.3 唯讀
    [Fact]
    public async Task Other_sso_users_read_ticket_workflow_specs_and_reviews_but_not_the_conversation()
    {
        using var f = await Make(("ts1", Spec()));
        var before = await TicketFactory.Json(await f.As("SSO1").GetAsync("/api/tickets/T-1/readonly"));
        Assert.Equal(0, before.GetProperty("toolSpecs").GetArrayLength());
        (await Submit(f)).EnsureSuccessStatusCode();
        (await Review(f, new { result = "Feasible", estimatedDays = 2 })).EnsureSuccessStatusCode();

        foreach (var who in new[] { "SSO1", "BIZ1", "CIM2" })
        {
            var r = await f.As(who).GetAsync("/api/tickets/T-1/readonly");
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            var j = await TicketFactory.Json(r);
            Assert.Equal("查工單", j.GetProperty("workflow")[0].GetProperty("description").GetString());
            var spec = j.GetProperty("toolSpecs")[0];
            Assert.Equal("查工單", spec.GetProperty("content").GetProperty("purpose").GetString());
            Assert.Equal("Feasible", spec.GetProperty("reviews")[0].GetProperty("result").GetString());
            Assert.False(j.TryGetProperty("messages", out _));
        }
        Assert.Equal(HttpStatusCode.Forbidden, (await f.As("SSO1").GetAsync("/api/tickets/T-1/interview")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await f.As("SSO1").GetAsync("/api/tickets/NOPE/readonly")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.CreateClient().GetAsync("/api/tickets/T-1/readonly")).StatusCode);
    }

    // 2. §5.3-8 完成通知
    [Fact]
    public async Task Reuse_only_submit_queues_completed_mail_once()
    {
        using var f = await Make(("ts1", Spec("Reuse")));
        (await Submit(f)).EnsureSuccessStatusCode();
        Assert.Equal(new[] { "BIZ1", "CIM1" }, Completed(f).Select(m => m.RecipientEmpNo).Order().ToArray());
    }

    [Fact]
    public async Task Deleting_the_last_unimplemented_spec_completes_the_ticket_and_queues_mail()
    {
        using var f = await Make(("ts1", Spec("Reuse")), ("ts2", Spec()));
        (await Submit(f)).EnsureSuccessStatusCode();
        Assert.Empty(Completed(f));
        (await Review(f, Infeasible, "ts2")).EnsureSuccessStatusCode();
        (await f.As("CIM1").DeleteAsync("/api/tickets/T-1/specs/ts2")).EnsureSuccessStatusCode();
        Assert.Equal(new[] { "BIZ1", "CIM1" }, Completed(f).Select(m => m.RecipientEmpNo).Order().ToArray());
    }

    // 3. §6.2 ref 必填
    [Theory]
    [InlineData("Reuse")]
    [InlineData("Extend")]
    public async Task Reuse_and_extend_without_ref_block_submit(string kind)
    {
        using var f = await Make(("ts1", Spec(kind, withRef: false)));
        var s = await State(f);
        Assert.Equal(["missing_field"], Codes(s));
        Assert.Contains("ref", s.GetProperty("blockers")[0].GetProperty("message").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await Submit(f)).StatusCode);
    }

    // 4. §7.3 已實作不重檢
    [Fact]
    public async Task Implemented_unchanged_specs_are_not_rechecked_but_unimplemented_ones_are()
    {
        using var f = await Make(("ts1", Spec()), ("ts2", Spec()));
        (await Submit(f)).EnsureSuccessStatusCode();
        (await Review(f, new { result = "Feasible", estimatedDays = 1 })).EnsureSuccessStatusCode();
        (await Act(f, "CIM1", "implementing")).EnsureSuccessStatusCode();
        (await Implemented(f)).EnsureSuccessStatusCode();
        (await f.As("CIM1").PostAsJsonAsync("/api/tickets/T-1/interview/reopen", new { reason = "x" })).EnsureSuccessStatusCode();

        src.Sha = "sha2"; // eqptId 變成別名
        src.Text = "class T { static object[] E = new[] { new GlossaryEntry(\"equipmentId\", \"EquipmentId\", \"d\", new[] { \"eqptId\" }), new GlossaryEntry(\"status\", \"Status\", \"d\", new string[0]) }; }";
        time.Advance(TimeSpan.FromMinutes(10));

        var s = await State(f);
        var b = Assert.Single(s.GetProperty("blockers").EnumerateArray());
        Assert.Equal(("rename_reason", "ts2"), (b.GetProperty("code").GetString(), b.GetProperty("specId").GetString()));
        var draft = await TicketFactory.Json(await f.As("CIM1").GetAsync("/api/tickets/T-1/tool-spec-draft"));
        var shas = draft.GetProperty("steps").EnumerateArray().ToDictionary(x => x.GetProperty("id").GetString()!, x => x.GetProperty("naming_check").GetProperty("sha").GetString());
        Assert.Equal(("sha1", "sha2"), (shas["ts1"], shas["ts2"]));
    }

    // 5. Revise 規則
    [Fact]
    public async Task Revise_runs_naming_check_on_changed_fields_and_demands_a_reason_for_renames()
    {
        using var f = await Make(("ts1", Spec()));
        (await Submit(f)).EnsureSuccessStatusCode();
        (await Review(f, Infeasible)).EnsureSuccessStatusCode();
        object Fields(string name, object? reasons = null) => new
        {
            fields = new Dictionary<string, object?>
            {
                ["inputs"] = new { type = "object", properties = new Dictionary<string, object> { [name] = new { type = "string" } } },
                ["naming_reasons"] = reasons,
            },
        };

        var term = await Act(f, "CIM1", "revise", body: Fields("brandNewTerm"));
        Assert.Equal(HttpStatusCode.BadRequest, term.StatusCode);
        Assert.Contains("brandNewTerm", await term.Content.ReadAsStringAsync());
        var rename = await Act(f, "CIM1", "revise", body: Fields("eqptIdAlias"));
        Assert.Equal(HttpStatusCode.BadRequest, rename.StatusCode);
        Assert.Contains("eqptIdAlias", await rename.Content.ReadAsStringAsync());
        Assert.Equal(1, (await State(f)).GetProperty("toolSpecs")[0].GetProperty("revision").GetInt32());

        var ok = await Act(f, "CIM1", "revise", body: Fields("eqptIdAlias", new Dictionary<string, string> { ["inputs.eqptIdAlias"] = "沿用舊系統" }));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(2, (await State(f)).GetProperty("toolSpecs")[0].GetProperty("revision").GetInt32());
    }

    // 6. Markdown 用 interface_snapshot
    [Fact]
    public async Task Markdown_uses_the_snapshot_server_and_tool_for_implemented_new_specs()
    {
        using var f = await Make(("ts1", Spec()));
        (await Submit(f)).EnsureSuccessStatusCode();
        (await Review(f, new { result = "Feasible", estimatedDays = 1 })).EnsureSuccessStatusCode();
        (await Act(f, "CIM1", "implementing")).EnsureSuccessStatusCode();
        (await Implemented(f)).EnsureSuccessStatusCode();
        var md = await (await f.As("BIZ1").GetAsync("/api/tickets/T-1/markdown")).Content.ReadAsStringAsync();
        Assert.Contains("MCP server `gw`，工具 `get_order`", md);
        Assert.DoesNotContain("待實作後指定", md);
    }

    // 7. 實作檢查要抓最新工具介面
    [Fact]
    public async Task Mark_implemented_bypasses_the_tool_catalog_cache()
    {
        tools["gw"] = [new Tool { Name = "get_order", InputSchema = Schema("""{"type":"object","properties":{"eqptId":{"type":"integer"}}}"""), OutputSchema = Schema(OutOk) }];
        using var f = await Make(("ts1", Spec()));
        (await Agent(f).GetAsync("/internal/tools/mcp-tools")).EnsureSuccessStatusCode(); // 快取舊介面
        (await Submit(f)).EnsureSuccessStatusCode();
        (await Review(f, new { result = "Feasible", estimatedDays = 1 })).EnsureSuccessStatusCode();
        (await Act(f, "CIM1", "implementing")).EnsureSuccessStatusCode();
        tools["gw"] = [GoodTool()]; // 工具已修正，快取仍是舊的
        var r = await TicketFactory.Json(await Implemented(f));
        Assert.True(r.GetProperty("passed").GetBoolean());
    }

    // 8. search 排除已完成
    [Fact]
    public async Task Search_tool_specs_excludes_completed_tickets()
    {
        using var f = await Make(("ts1", Spec("Reuse")));
        (await Submit(f)).EnsureSuccessStatusCode(); // 已完成
        (await f.Create("CIM2", "T-2", "別張")).EnsureSuccessStatusCode();
        var hits = await TicketFactory.Json(await Agent(f, "T-2", "CIM2").GetAsync("/internal/tools/tool-specs?keyword=查工單"));
        Assert.Equal(0, hits.GetArrayLength());

        (await f.Create("CIM2", "T-3", "進行中")).EnsureSuccessStatusCode();
        (await Agent(f, "T-3", "CIM2").PutAsJsonAsync("/internal/tools/tool-specs/x", new { purpose = "查工單" })).EnsureSuccessStatusCode();
        hits = await TicketFactory.Json(await Agent(f, "T-2", "CIM2").GetAsync("/internal/tools/tool-specs?keyword=查工單"));
        Assert.Equal("T-3", Assert.Single(hits.EnumerateArray()).GetProperty("ticketId").GetString());
    }

    // 9. 送出後人工草稿存檔要先重開 Interview
    [Fact]
    public async Task Human_draft_saves_after_submit_are_rejected_until_the_interview_is_reopened()
    {
        using var f = await Make(("ts1", Spec()));
        (await Submit(f)).EnsureSuccessStatusCode();
        foreach (var doc in new[] { "workflow-draft", "tool-spec-draft" })
        {
            var d = await TicketFactory.Json(await f.As("CIM1").GetAsync($"/api/tickets/T-1/{doc}"));
            var r = await f.As("CIM1").PutAsJsonAsync($"/api/tickets/T-1/{doc}", new { version = d.GetProperty("version").GetInt32(), steps = d.GetProperty("steps") });
            Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
            Assert.Contains("重開", await r.Content.ReadAsStringAsync());
        }
        (await f.As("CIM1").PostAsJsonAsync("/api/tickets/T-1/interview/reopen", new { reason = "x" })).EnsureSuccessStatusCode();
        var d2 = await TicketFactory.Json(await f.As("CIM1").GetAsync("/api/tickets/T-1/workflow-draft"));
        Assert.Equal(HttpStatusCode.OK, (await f.As("CIM1").PutAsJsonAsync("/api/tickets/T-1/workflow-draft", new { version = d2.GetProperty("version").GetInt32(), steps = d2.GetProperty("steps") })).StatusCode);
    }

    // 10. run token 寫入限制
    [Fact]
    public async Task Run_token_writes_are_rejected_after_submit_or_cancel_but_reads_still_work()
    {
        using var f = await Make(("ts1", Spec()));
        (await Submit(f)).EnsureSuccessStatusCode();
        var a = Agent(f);
        Assert.Equal(HttpStatusCode.Conflict, (await a.PutAsJsonAsync("/internal/tools/workflow-steps/s9", new { description = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await a.DeleteAsync("/internal/tools/workflow-steps/s1")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await a.PutAsJsonAsync("/internal/tools/tool-specs/ts9", new { purpose = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await a.DeleteAsync("/internal/tools/tool-specs/ts1")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await a.GetAsync("/internal/tools/requirement-ticket")).StatusCode);

        (await f.Create("CIM1", "T-2", "x")).EnsureSuccessStatusCode();
        (await f.As("CIM1").PostAsJsonAsync("/api/tickets/T-2/cancel", new { reason = "x" })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, (await Agent(f, "T-2").PutAsJsonAsync("/internal/tools/workflow-steps/s1", new { description = "x" })).StatusCode);
    }
}
