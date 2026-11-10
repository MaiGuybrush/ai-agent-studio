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

    // ---- 修改後重評 ----

    [Theory]
    [InlineData("Infeasible")]
    [InlineData("Conditional")]
    public async Task Revise_creates_a_new_pending_revision_notifies_the_owner_and_leaves_other_specs_alone(string result)
    {
        using var f = await Make();
        await Reply(f, Owner1, result == "Infeasible" ? Inf : Cond);
        var before = Outbox(f).Count;
        var r = await Act(f, "CIM1", "revise", body: new { fields = new { purpose = "改過的用途" } });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var info = await SpecInfo(f);
        Assert.Equal(("Pending", 2, false), (info.GetProperty("state").GetString(), info.GetProperty("revision").GetInt32(), info.GetProperty("handled").GetBoolean()));
        Assert.Equal(1, (await SpecInfo(f, "ts2")).GetProperty("revision").GetInt32());
        var mail = Outbox(f).Skip(before).Single();
        Assert.Equal((EmailEvents.Resubmitted, Owner1, "T-1"), (mail.EventType, mail.RecipientEmpNo, mail.TicketId));
        Assert.Contains("ts1", mail.PayloadJson);
        var detail = await TicketFactory.Json(await f.As(Owner1).GetAsync("/api/reviews/T-1/ts1"));
        Assert.Equal(("改過的用途", 2, true), (detail.GetProperty("content").GetProperty("purpose").GetString(), detail.GetProperty("revision").GetInt32(), detail.GetProperty("canReply").GetBoolean()));
        Assert.Equal("評估中", await Status(f));
        var ev = f.Events().Last();
        Assert.Equal(("ToolSpecRevise", "CIM1", "ts1", result, "Pending"), (ev.Type, ev.ActorEmpNo, ev.Reason, ev.FromValue, ev.ToValue));
    }

    [Fact]
    public async Task Revise_keeps_the_draft_in_sync_so_a_later_edit_does_not_regress_the_spec()
    {
        using var f = await Make();
        await Reply(f, Owner1, Inf);
        (await Act(f, "CIM1", "revise", body: new { fields = new { purpose = "改過的用途" } })).EnsureSuccessStatusCode();
        var draft = await TicketFactory.Json(await f.As("CIM1").GetAsync("/api/tickets/T-1/tool-spec-draft"));
        Assert.Equal("改過的用途", draft.GetProperty("steps").EnumerateArray().First(s => s.GetProperty("id").GetString() == "ts1").GetProperty("purpose").GetString());
    }

    [Fact]
    public async Task Revise_can_reassign_the_system_owner_and_the_new_owner_is_notified()
    {
        using var f = await Make();
        await Reply(f, Owner1, Inf);
        (await Act(f, "CIM1", "revise", body: new { fields = new { system_owner = Owner2 } })).EnsureSuccessStatusCode();
        Assert.Equal(Owner2, Outbox(f).Last().RecipientEmpNo);
    }

    [Fact]
    public async Task Revise_rejects_feasible_unchanged_invalid_and_reuse_edits()
    {
        using var f = await Make();
        await Reply(f, Owner2, Fea, "ts2");
        Assert.Equal(HttpStatusCode.Conflict, (await Act(f, "CIM1", "revise", "ts2", new { fields = new { purpose = "x" } })).StatusCode); // 可行不用改
        await Reply(f, Owner1, Inf);
        Assert.Equal(HttpStatusCode.BadRequest, (await Act(f, "CIM1", "revise", body: new { fields = new { purpose = "查工單" } })).StatusCode); // 沒有改動
        Assert.Equal(HttpStatusCode.BadRequest, (await Act(f, "CIM1", "revise", body: new { fields = new { purpose = "" } })).StatusCode); // 必填欄位被清空
        Assert.Equal(HttpStatusCode.BadRequest, (await Act(f, "CIM1", "revise", body: new { fields = new { kind = "Reuse" } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Act(f, "CIM1", "revise", body: new { fields = new { system_owner = "99999999" } })).StatusCode); // 查無此人
        Assert.Equal(1, (await SpecInfo(f)).GetProperty("revision").GetInt32());
    }

    // ---- 刪除不可行的 Tool Spec ----

    [Fact]
    public async Task Delete_removes_an_infeasible_spec_and_unlinks_workflow_steps_that_use_it()
    {
        using var f = await Make();
        await Reply(f, Owner1, Inf);
        var r = await f.As("CIM1").DeleteAsync("/api/tickets/T-1/specs/ts1");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var sub = await TicketFactory.Json(await f.As("CIM1").GetAsync("/api/tickets/T-1/submission"));
        Assert.DoesNotContain(sub.GetProperty("toolSpecs").EnumerateArray(), x => x.GetProperty("specId").GetString() == "ts1");
        var steps = (await TicketFactory.Json(await f.As("CIM1").GetAsync("/api/tickets/T-1/workflow-draft"))).GetProperty("steps").EnumerateArray().ToList();
        Assert.False(steps.First(s => s.GetProperty("id").GetString() == "s1").TryGetProperty("tool_spec", out _)); // 調整為人工步驟
        Assert.Equal("ts2", steps.First(s => s.GetProperty("id").GetString() == "s2").GetProperty("tool_spec").GetString());
        var specs = (await TicketFactory.Json(await f.As("CIM1").GetAsync("/api/tickets/T-1/tool-spec-draft"))).GetProperty("steps").EnumerateArray();
        Assert.DoesNotContain(specs, s => s.GetProperty("id").GetString() == "ts1");
        // 最新一次 submission 的 Workflow Definition 快照已調整，舊的保持不變
        var subs = sub.GetProperty("submissions").EnumerateArray().ToList();
        Assert.False(subs.Last().GetProperty("workflow")[0].TryGetProperty("tool_spec", out _));
        Assert.Equal("ts1", subs.First().GetProperty("workflow")[0].GetProperty("tool_spec").GetString());
        var ev = f.Events().Last();
        Assert.Equal(("ToolSpecDelete", "CIM1", "ts1", "Infeasible"), (ev.Type, ev.ActorEmpNo, ev.Reason, ev.FromValue));
        Assert.Equal("評估中", await Status(f)); // ts2 還在待評估
    }

    [Fact]
    public async Task Delete_only_applies_to_infeasible_specs_and_only_for_the_responsible_engineer()
    {
        using var f = await Make();
        await Reply(f, Owner1, Cond);
        Assert.Equal(HttpStatusCode.Conflict, (await f.As("CIM1").DeleteAsync("/api/tickets/T-1/specs/ts1")).StatusCode);
        await Reply(f, Owner1, Inf);
        Assert.Equal(HttpStatusCode.Forbidden, (await f.As("CIM2").DeleteAsync("/api/tickets/T-1/specs/ts1")).StatusCode);
    }

    [Fact]
    public async Task When_all_specs_are_infeasible_the_state_says_so_and_the_last_one_cannot_be_deleted()
    {
        using var f = await Make();
        Assert.Equal(HttpStatusCode.Conflict, (await f.As("CIM1").DeleteAsync("/api/tickets/T-1/specs/ts3")).StatusCode); // Reuse 不是不可行，不能刪
        await Reply(f, Owner1, Inf); await Reply(f, Owner2, Inf, "ts2");
        Assert.False((await TicketFactory.Json(await f.As("CIM1").GetAsync("/api/tickets/T-1/submission"))).GetProperty("allInfeasible").GetBoolean()); // ts3 是 Reuse
        // 把 Reuse 從資料庫移除，模擬「只有兩份、全部不可行」
        using (var s = f.Services.CreateScope()) { var db = s.ServiceProvider.GetRequiredService<StudioDb>(); db.ToolSpecRecords.Remove(db.ToolSpecRecords.First(x => x.SpecId == "ts3")); db.SaveChanges(); }
        Assert.True((await TicketFactory.Json(await f.As("CIM1").GetAsync("/api/tickets/T-1/submission"))).GetProperty("allInfeasible").GetBoolean());
        Assert.Equal("評估中", await Status(f)); // 不自動取消
        Assert.Equal(HttpStatusCode.OK, (await f.As("CIM1").DeleteAsync("/api/tickets/T-1/specs/ts1")).StatusCode);
        var last = await f.As("CIM1").DeleteAsync("/api/tickets/T-1/specs/ts2");
        Assert.Equal(HttpStatusCode.Conflict, last.StatusCode);
        Assert.Contains("Cancellation", await last.Content.ReadAsStringAsync());
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

