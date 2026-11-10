using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AiAgentStudio.Api.Interviews;
using AiAgentStudio.Api.Studio;
using AiAgentStudio.Api.Tickets;
using AiAgentStudio.Api.Vocabulary;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using static AiAgentStudio.Api.Tests.VocabularyServiceTests;

namespace AiAgentStudio.Api.Tests;

/// <summary>#30：重開 Interview 與 revision diff（spec 5.3-3、6.5、8.6）。</summary>
public class ReopenInterviewTests : IDisposable
{
    readonly string path = Path.Combine(Path.GetTempPath(), $"studio-{Guid.NewGuid():N}.json");
    public void Dispose() => File.Delete(path);

    class FakeProposals : IVocabularyProposalClient
    {
        public Task<ProposalIssue> CreateAsync(VocabularyProposal p, CancellationToken ct) => Task.FromResult(new ProposalIssue(1, "https://gitea.test/issues/1"));
    }

    class Factory(string path) : InterviewFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder b)
        {
            base.ConfigureWebHost(b);
            b.ConfigureServices(s =>
            {
                s.RemoveAll<StudioConfigProvider>(); s.AddSingleton(new StudioConfigProvider(path, _ => "k"));
                s.RemoveAll<IMcpTransportFactory>(); s.AddSingleton<IMcpTransportFactory>(McpToolCatalogTests.GwFactory());
                s.RemoveAll<IVocabularySource>(); s.AddSingleton<IVocabularySource>(new FakeSource { Sha = "sha1", Text = "class T { static object[] E = new[] { new GlossaryEntry(\"eqptId\", \"EqptId\", \"d\", new[] { \"a\" }), new GlossaryEntry(\"status\", \"Status\", \"d\", new[] { \"b\" }) }; }" });
                s.RemoveAll<IVocabularyAlert>(); s.AddSingleton<IVocabularyAlert>(new FakeAlert());
                s.RemoveAll<IVocabularyProposalClient>(); s.AddSingleton<IVocabularyProposalClient>(new FakeProposals());
            });
        }
    }

    async Task<Factory> Make()
    {
        File.WriteAllText(path, """{ "mcpServers": { "gw": { "type": "http", "url": "http://gw/mcp" } }, "systems": { "MES": "10000001" } }""");
        var f = new Factory(path);
        f.Names["10000001"] = "MES 負責人";
        await f.Seed();
        return f;
    }

    static HttpClient Agent(InterviewFactory f)
    {
        var c = f.CreateClient();
        c.DefaultRequestHeaders.Add("X-Run-Token", f.Services.GetRequiredService<RunTokenService>().Issue("T-1", "CIM1"));
        return c;
    }
    static object Spec(string purpose = "查工單", string kind = "New") => new Dictionary<string, object?>
    {
        ["kind"] = kind, ["purpose"] = purpose, ["system"] = "MES", ["side_effect"] = "read",
        ["system_owner"] = kind == "Reuse" ? null : "10000001",
        ["ref"] = kind is "Reuse" or "Extend" ? new { server = "gw", tool = "get_order" } : null, // §6.2：Reuse／Extend 必填 ref
        ["inputs"] = new { type = "object", properties = new { eqptId = new { type = "string" } } },
        ["outputs"] = new { type = "object", properties = new { status = new { type = "string" } } },
    };
    static Task<HttpResponseMessage> Upsert(HttpClient c, string id, object body) => c.PutAsJsonAsync($"/internal/tools/tool-specs/{id}", body);
    static Task<HttpResponseMessage> Submit(HttpClient c) => c.PostAsync("/api/tickets/T-1/submit", null);
    static Task<HttpResponseMessage> Reopen(HttpClient c, string? reason = "需求變更") => c.PostAsJsonAsync("/api/tickets/T-1/interview/reopen", new { reason });
    static async Task<string> Status(InterviewFactory f) => (await TicketFactory.Json(await f.As("CIM1").GetAsync("/api/tickets/T-1"))).GetProperty("status").GetString()!;
    static async Task<JsonElement> Specs(InterviewFactory f) => (await TicketFactory.Json(await f.As("CIM1").GetAsync("/api/tickets/T-1/submission"))).GetProperty("toolSpecs");

    async Task<Factory> Submitted()
    {
        var f = await Make();
        await Upsert(Agent(f), "ts1", Spec());
        await Upsert(Agent(f), "ts2", Spec("查設備"));
        Assert.Equal(HttpStatusCode.OK, (await Submit(f.As("CIM1"))).StatusCode);
        return f;
    }

    [Fact]
    public async Task Reopen_needs_a_reason_the_responsible_engineer_and_a_submitted_ticket()
    {
        using var f = await Make();
        Assert.Equal(HttpStatusCode.Conflict, (await Reopen(f.As("CIM1"))).StatusCode); // 訪談中：沒有可重開的送出
        await Upsert(Agent(f), "ts1", Spec());
        await Submit(f.As("CIM1"));
        Assert.Equal(HttpStatusCode.BadRequest, (await Reopen(f.As("CIM1"), " ")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Reopen(f.As("CIM2"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Reopen(f.As("BIZ1"))).StatusCode);
        await f.As("CIM1").PostAsJsonAsync("/api/tickets/T-1/cancel", new { reason = "x" });
        Assert.Equal(HttpStatusCode.Conflict, (await Reopen(f.As("CIM1"))).StatusCode); // 已取消要先走取消的重開
    }

    [Fact]
    public async Task Reopen_opens_a_new_interview_with_snapshot_and_reason_keeps_the_old_one_and_logs_an_event()
    {
        using var f = await Submitted();
        using (var s = f.Services.CreateScope()) // 第一段對話（平常由第一則訊息建立）
        {
            var db = s.ServiceProvider.GetRequiredService<StudioDb>();
            db.Interviews.Add(new Interview { TicketId = "T-1", OpeningContext = "需求", ConversationJson = """[{"kind":"request","parts":[{"part_kind":"user-prompt","content":"舊訊息"}]}]""" });
            await db.SaveChangesAsync();
        }
        var perms = (await TicketFactory.Json(await f.As("CIM1").GetAsync("/api/tickets/T-1"))).GetProperty("permissions");
        Assert.True(perms.GetProperty("canReopenInterview").GetBoolean());
        Assert.False((await TicketFactory.Json(await f.As("CIM2").GetAsync("/api/tickets/T-1"))).GetProperty("permissions").GetProperty("canReopenInterview").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await Reopen(f.As("CIM1"))).StatusCode);
        Assert.Equal("訪談中", await Status(f));

        var cur = await TicketFactory.Json(await f.As("CIM1").GetAsync("/api/tickets/T-1/interview"));
        Assert.Equal("需求變更", cur.GetProperty("reopenReason").GetString());
        Assert.Empty(cur.GetProperty("messages").EnumerateArray());
        var ctx = cur.GetProperty("openingContext").GetString()!;
        Assert.Contains("需求變更", ctx);
        Assert.Contains("查設備", ctx); // 上次送出的快照
        var old = Assert.Single(cur.GetProperty("previous").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, old.GetProperty("reopenReason").ValueKind);

        var oldView = await f.As("CIM2").GetAsync($"/api/tickets/T-1/interview/{old.GetProperty("id").GetInt64()}"); // 任何 CIM Engineer 可讀舊對話
        Assert.Equal(HttpStatusCode.OK, oldView.StatusCode);
        Assert.Equal("舊訊息", (await TicketFactory.Json(oldView)).GetProperty("messages")[0].GetProperty("text").GetString());

        var e = f.Events().Last();
        Assert.Equal(("ReopenInterview", "CIM1", "需求變更"), (e.Type, e.ActorEmpNo, e.Reason));
    }

    [Fact]
    public async Task Reopen_is_allowed_on_a_completed_ticket_and_status_is_rederived_after_resubmit()
    {
        using var f = await Make();
        await Upsert(Agent(f), "ts1", Spec(kind: "Reuse"));
        await Submit(f.As("CIM1"));
        Assert.Equal("已完成", await Status(f));
        Assert.Equal(HttpStatusCode.OK, (await Reopen(f.As("CIM1"))).StatusCode);
        Assert.Equal("訪談中", await Status(f));
        await Upsert(Agent(f), "ts2", Spec("新的"));
        await Submit(f.As("CIM1"));
        Assert.Equal("評估中", await Status(f)); // Reuse 已實作、新增的 ts2 待評估
    }

    [Fact]
    public async Task Resubmit_after_reopen_new_revision_only_for_changed_specs_with_shared_submission_number_including_description_only_change()
    {
        using var f = await Submitted();
        await Reopen(f.As("CIM1"));
        await Upsert(Agent(f), "ts1", new { purpose = "只改說明" });
        Assert.Equal(HttpStatusCode.OK, (await Submit(f.As("CIM1"))).StatusCode);

        var specs = (await Specs(f)).EnumerateArray().ToDictionary(x => x.GetProperty("specId").GetString()!);
        Assert.Equal(2, specs["ts1"].GetProperty("revision").GetInt32());
        Assert.Equal(1, specs["ts2"].GetProperty("revision").GetInt32());
        var sub = await TicketFactory.Json(await f.As("CIM1").GetAsync("/api/tickets/T-1/submission"));
        var s2 = sub.GetProperty("submissions")[1];
        Assert.Equal(2, s2.GetProperty("number").GetInt32());
        Assert.Equal(2, s2.GetProperty("specs")[0].GetProperty("submissionNumber").GetInt32());
    }

    [Fact]
    public async Task Draft_is_loaded_from_the_latest_revisions_on_reopen()
    {
        using var f = await Submitted();
        using (var s = f.Services.CreateScope()) // 草稿與凍結內容分歧（例如舊版本遺留）
        {
            var db = s.ServiceProvider.GetRequiredService<StudioDb>();
            var d = db.WorkflowDrafts.Single(x => x.Doc == "tool_specs");
            d.DraftJson = d.DraftJson.Replace("查工單", "髒的草稿"); await db.SaveChangesAsync();
        }
        await Reopen(f.As("CIM1"));
        var draft = await TicketFactory.Json(await f.As("CIM1").GetAsync("/api/tickets/T-1/tool-spec-draft"));
        Assert.DoesNotContain("髒的草稿", draft.ToString());
        Assert.Contains("查工單", draft.ToString());
    }

    [Fact]
    public async Task Reviewer_sees_the_diff_against_the_previous_revision_of_the_same_spec()
    {
        using var f = await Submitted();
        f.Names["10000001"] = "MES 負責人";
        var first = await TicketFactory.Json(await f.As("10000001").GetAsync("/api/reviews/T-1/ts1"));
        Assert.Equal(JsonValueKind.Null, first.GetProperty("diff").ValueKind); // 第一版沒有可比的

        await Reopen(f.As("CIM1"));
        await Upsert(Agent(f), "ts1", new { purpose = "改過的用途" });
        await Submit(f.As("CIM1"));
        var d = await TicketFactory.Json(await f.As("10000001").GetAsync("/api/reviews/T-1/ts1"));
        Assert.Equal(1, d.GetProperty("diff").GetProperty("fromRevision").GetInt32());
        var ch = Assert.Single(d.GetProperty("diff").GetProperty("changes").EnumerateArray());
        Assert.Equal(("purpose", "查工單", "改過的用途"), (ch.GetProperty("path").GetString(), ch.GetProperty("before").GetString(), ch.GetProperty("after").GetString()));
    }
}
