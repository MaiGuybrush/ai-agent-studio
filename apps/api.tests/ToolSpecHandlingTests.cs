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
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using static AiAgentStudio.Api.Tests.VocabularyServiceTests;

namespace AiAgentStudio.Api.Tests;

/// <summary>#28：CIM Engineer 處理評估結果（spec 5.1、5.3 第 4–6 點、9）。只透過 HTTP 介面觀察。</summary>
public class ToolSpecHandlingTests : IDisposable
{
    readonly string path = Path.Combine(Path.GetTempPath(), $"studio-{Guid.NewGuid():N}.json");
    readonly string mailDir = Path.Combine(Path.GetTempPath(), $"mail-{Guid.NewGuid():N}");
    public void Dispose() { File.Delete(path); if (Directory.Exists(mailDir)) Directory.Delete(mailDir, true); }

    readonly FakeSource src = new() { Sha = "sha1", Text = "class T { static object[] E = new[] { new GlossaryEntry(\"eqptId\", \"EqptId\", \"d\", new[] { \"eqptIdAlias\" }), new GlossaryEntry(\"status\", \"Status\", \"d\", new[] { \"statusAlias\" }) }; }" };
    readonly FakeTimeProvider time = new(DateTimeOffset.UtcNow);

    class FakeProposals : IVocabularyProposalClient
    {
        public Task<ProposalIssue> CreateAsync(VocabularyProposal p, CancellationToken ct) => Task.FromResult(new ProposalIssue(1, "https://gitea.test/issues/1"));
    }

    class Factory(string path, FakeSource src, TimeProvider time, string mailDir) : InterviewFactory
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
                s.RemoveAll<IEmailSender>(); s.AddSingleton<IEmailSender>(new LocalFolderEmailSender(Options.Create(new EmailOptions { LocalFolder = mailDir })));
            });
        }
    }

    const string Owner1 = "10000001", Owner2 = "10000002";

    /// <summary>送出一張單：s1 → ts1；ts1 → Owner1、ts2 → Owner2、ts3 為 Reuse。</summary>
    async Task<Factory> Make()
    {
        File.WriteAllText(path, """{ "mcpServers": {}, "systems": { "MES": "10000001" } }""");
        var f = new Factory(path, src, time, mailDir);
        f.Names[Owner1] = "MES 負責人"; f.Names[Owner2] = "ERP 負責人";
        await f.Seed();
        var agent = f.CreateClient();
        agent.DefaultRequestHeaders.Add("X-Run-Token", f.Services.GetRequiredService<AiAgentStudio.Api.Interviews.RunTokenService>().Issue("T-1", "CIM1"));
        await agent.PutAsJsonAsync("/internal/tools/workflow-steps/s1", new { description = "查工單", tool_spec = "ts1" });
        await agent.PutAsJsonAsync("/internal/tools/workflow-steps/s2", new { description = "查狀態", tool_spec = "ts2" });
        await agent.PutAsJsonAsync("/internal/tools/tool-specs/ts1", Spec("New", Owner1));
        await agent.PutAsJsonAsync("/internal/tools/tool-specs/ts2", Spec("Extend", Owner2));
        await agent.PutAsJsonAsync("/internal/tools/tool-specs/ts3", Spec("Reuse", null));
        (await f.As("CIM1").PostAsync("/api/tickets/T-1/submit", null)).EnsureSuccessStatusCode();
        return f;
    }

    static object Spec(string kind, string? owner) => new Dictionary<string, object?>
    {
        ["kind"] = kind, ["purpose"] = "查工單", ["system"] = "MES", ["side_effect"] = "read", ["system_owner"] = owner,
        ["inputs"] = new { type = "object", properties = new { eqptId = new { type = "string" } } },
        ["outputs"] = new { type = "object", properties = new { status = new { type = "string" } } },
    };

    static Task<HttpResponseMessage> Reply(InterviewFactory f, string who, object body, string spec = "ts1") =>
        f.As(who).PutAsJsonAsync($"/api/reviews/T-1/{spec}", body);
    static Task<HttpResponseMessage> Act(InterviewFactory f, string who, string action, string spec = "ts1", object? body = null) =>
        f.As(who).PostAsJsonAsync($"/api/tickets/T-1/specs/{spec}/{action}", body ?? new { });
    static async Task<JsonElement> SpecInfo(InterviewFactory f, string spec = "ts1") =>
        (await TicketFactory.Json(await f.As("CIM1").GetAsync("/api/tickets/T-1/submission"))).GetProperty("toolSpecs").EnumerateArray().First(x => x.GetProperty("specId").GetString() == spec);
    static async Task<string> Status(InterviewFactory f) => (await TicketFactory.Json(await f.As("CIM1").GetAsync("/api/tickets/T-1"))).GetProperty("status").GetString()!;
    static List<EmailOutbox> Outbox(InterviewFactory f)
    {
        using var s = f.Services.CreateScope();
        return s.ServiceProvider.GetRequiredService<StudioDb>().EmailOutbox.AsNoTracking().OrderBy(x => x.Id).ToList();
    }
    static readonly object Cond = new { result = "Conditional", reason = "r", condition = "需先升級 API", estimatedDays = 2 };
    static readonly object Fea = new { result = "Feasible", estimatedDays = 1 };
    static readonly object Inf = new { result = "Infeasible", reason = "做不到" };

    // ---- 接受條件 ----

    [Fact]
    public async Task Accept_condition_records_it_on_the_spec_locks_owner_edits_and_logs_the_event()
    {
        using var f = await Make();
        await Reply(f, Owner1, Cond);
        Assert.Equal(HttpStatusCode.OK, (await Act(f, "CIM1", "accept-condition")).StatusCode);
        var info = await SpecInfo(f);
        Assert.Equal(("ConditionAccepted", "需先升級 API", true), (info.GetProperty("state").GetString(), info.GetProperty("acceptedCondition").GetString(), info.GetProperty("handled").GetBoolean()));
        Assert.Equal(HttpStatusCode.Conflict, (await Reply(f, Owner1, Fea)).StatusCode); // System Owner 不能再改
        var ev = f.Events().Last();
        Assert.Equal(("ToolSpecAcceptCondition", "CIM1", "ts1", "Conditional", "ConditionAccepted"), (ev.Type, ev.ActorEmpNo, ev.Reason, ev.FromValue, ev.ToValue));
    }

    [Fact]
    public async Task Accept_condition_only_applies_to_conditional_specs()
    {
        using var f = await Make();
        Assert.Equal(HttpStatusCode.Conflict, (await Act(f, "CIM1", "accept-condition")).StatusCode); // 待評估
        await Reply(f, Owner1, Fea);
        Assert.Equal(HttpStatusCode.Conflict, (await Act(f, "CIM1", "accept-condition")).StatusCode); // 可行
    }

    [Theory]
    [InlineData("CIM2")]   // 非負責的 CIM Engineer
    [InlineData(Owner1)]   // System Owner
    [InlineData("BIZ1")]
    public async Task Only_the_responsible_cim_engineer_can_handle(string who)
    {
        using var f = await Make();
        await Reply(f, Owner1, Cond);
        Assert.Equal(HttpStatusCode.Forbidden, (await Act(f, who, "accept-condition")).StatusCode);
        Assert.Equal("Conditional", (await SpecInfo(f)).GetProperty("state").GetString());
    }

    // ---- 實作中 ----

    [Fact]
    public async Task Feasible_and_accepted_specs_can_be_marked_implementing_and_ticket_status_follows()
    {
        using var f = await Make();
        await Reply(f, Owner1, Fea); await Reply(f, Owner2, Cond, "ts2");
        Assert.Equal("評估中", await Status(f));
        (await Act(f, "CIM1", "implementing")).EnsureSuccessStatusCode();
        Assert.Equal("Implementing", (await SpecInfo(f)).GetProperty("state").GetString());
        Assert.Equal("評估中", await Status(f)); // ts2 還沒接受
        (await Act(f, "CIM1", "accept-condition", "ts2")).EnsureSuccessStatusCode();
        Assert.Equal("實作中", await Status(f));
        (await Act(f, "CIM1", "implementing", "ts2")).EnsureSuccessStatusCode();
        var ev = f.Events().Last();
        Assert.Equal(("ToolSpecImplementing", "ts2", "ConditionAccepted", "Implementing"), (ev.Type, ev.Reason, ev.FromValue, ev.ToValue));
    }

    [Theory]
    [InlineData("Conditional")]
    [InlineData("Infeasible")]
    public async Task Unaccepted_or_infeasible_specs_cannot_be_marked_implementing(string result)
    {
        using var f = await Make();
        await Reply(f, Owner1, result == "Conditional" ? Cond : Inf);
        Assert.Equal(HttpStatusCode.Conflict, (await Act(f, "CIM1", "implementing")).StatusCode);
    }
}

