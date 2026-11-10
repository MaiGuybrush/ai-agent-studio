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

/// <summary>#27：Tool Spec 留言與 System Owner 轉派（spec 9、10.1）。只透過 HTTP 介面觀察。</summary>
public class SpecCommentTests : IDisposable
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
                s.RemoveAll<IMcpTransportFactory>(); s.AddSingleton<IMcpTransportFactory>(McpToolCatalogTests.GwFactory());
                s.RemoveAll<IVocabularySource>(); s.AddSingleton<IVocabularySource>(src);
                s.RemoveAll<IVocabularyAlert>(); s.AddSingleton<IVocabularyAlert>(new FakeAlert());
                s.RemoveAll<IVocabularyProposalClient>(); s.AddSingleton<IVocabularyProposalClient>(new FakeProposals());
                s.RemoveAll<TimeProvider>(); s.AddSingleton(time);
                s.RemoveAll<IEmailSender>(); s.AddSingleton<IEmailSender>(new LocalFolderEmailSender(Options.Create(new EmailOptions { LocalFolder = mailDir })));
            });
        }
    }

    const string Owner1 = "10000001", Owner2 = "10000002";

    /// <summary>送出一張有三份 Tool Spec 的單：ts1 → Owner1、ts2 → Owner2；ts3 為 Reuse。</summary>
    async Task<Factory> Make()
    {
        File.WriteAllText(path, """{ "mcpServers": { "gw": { "type": "http", "url": "http://gw/mcp" } }, "systems": { "MES": "10000001" } }""");
        var f = new Factory(path, src, time, mailDir);
        f.Names[Owner1] = "MES 負責人"; f.Names[Owner2] = "ERP 負責人";
        await f.Seed();
        var agent = f.CreateClient();
        agent.DefaultRequestHeaders.Add("X-Run-Token", f.Services.GetRequiredService<AiAgentStudio.Api.Interviews.RunTokenService>().Issue("T-1", "CIM1"));
        await agent.PutAsJsonAsync("/internal/tools/workflow-steps/s1", new { description = "查工單", tool_spec = "ts1" });
        await agent.PutAsJsonAsync("/internal/tools/tool-specs/ts1", Spec("New", Owner1));
        await agent.PutAsJsonAsync("/internal/tools/tool-specs/ts2", Spec("Extend", Owner2));
        await agent.PutAsJsonAsync("/internal/tools/tool-specs/ts3", Spec("Reuse", null));
        (await f.As("CIM1").PostAsync("/api/tickets/T-1/submit", null)).EnsureSuccessStatusCode();
        return f;
    }

    static object Spec(string kind, string? owner) => new Dictionary<string, object?>
    {
        ["kind"] = kind, ["purpose"] = "查工單", ["system"] = "MES", ["side_effect"] = "read", ["system_owner"] = owner,
        ["ref"] = kind is "Reuse" or "Extend" ? new { server = "gw", tool = "get_order" } : null, // §6.2：Reuse／Extend 必填 ref
        ["inputs"] = new { type = "object", properties = new { eqptId = new { type = "string" } } },
        ["outputs"] = new { type = "object", properties = new { status = new { type = "string" } } },
    };

    static Task<HttpResponseMessage> Reply(InterviewFactory f, string who, object body, string spec = "ts1") =>
        f.As(who).PutAsJsonAsync($"/api/reviews/T-1/{spec}", body);
    static readonly object Ok = new { result = "Feasible", estimatedDays = 3 };
    static Task<HttpResponseMessage> Comment(InterviewFactory f, string who, string body, string spec = "ts1") =>
        f.As(who).PostAsJsonAsync($"/api/tickets/T-1/specs/{spec}/comments", new { body });
    static Task<HttpResponseMessage> Move(InterviewFactory f, string who, string? to, string? reason, string spec = "ts1") =>
        f.As(who).PostAsJsonAsync($"/api/tickets/T-1/specs/{spec}/reassign-owner", new { newOwnerEmpNo = to, reason });
    static async Task<JsonElement[]> Thread(InterviewFactory f, string who, string spec = "ts1") =>
        (await TicketFactory.Json(await f.As(who).GetAsync($"/api/tickets/T-1/specs/{spec}/comments"))).EnumerateArray().ToArray();
    static List<EmailOutbox> Outbox(InterviewFactory f)
    {
        using var s = f.Services.CreateScope();
        return s.ServiceProvider.GetRequiredService<StudioDb>().EmailOutbox.AsNoTracking().OrderBy(x => x.Id).ToList();
    }
    static async Task<string> SpecState(InterviewFactory f) =>
        (await TicketFactory.Json(await f.As("CIM1").GetAsync("/api/tickets/T-1/submission"))).GetProperty("toolSpecs").EnumerateArray().First(x => x.GetProperty("specId").GetString() == "ts1").GetProperty("state").GetString()!;
    static async Task<int> PendingCount(InterviewFactory f, string who) =>
        (await TicketFactory.Json(await f.As(who).GetAsync("/api/reviews/pending"))).GetArrayLength();

    [Fact]
    public async Task Both_sides_can_comment_and_thread_is_visible_to_both_in_order_without_changing_state()
    {
        using var f = await Make();
        (await Comment(f, Owner1, "請問 eqptId 是哪個欄位？")).EnsureSuccessStatusCode();
        (await Comment(f, "CIM1", "是設備代號。")).EnsureSuccessStatusCode();
        foreach (var who in new[] { Owner1, "CIM1" })
        {
            var t = await Thread(f, who);
            Assert.Equal(["請問 eqptId 是哪個欄位？", "是設備代號。"], t.Select(x => x.GetProperty("body").GetString()));
            Assert.Equal([Owner1, "CIM1"], t.Select(x => x.GetProperty("authorEmpNo").GetString()));
            Assert.All(t, x => Assert.Equal(1, x.GetProperty("revision").GetInt32()));
        }
        Assert.Equal("Pending", await SpecState(f));
    }

    [Theory]
    [InlineData("BIZ1")]
    [InlineData("SSO1")]
    [InlineData("CIM2")] // 非負責的 CIM Engineer
    [InlineData(Owner2)] // 非被指派的 System Owner
    public async Task Others_cannot_comment_or_read(string who)
    {
        using var f = await Make();
        Assert.Equal(HttpStatusCode.Forbidden, (await Comment(f, who, "hi")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await f.As(who).GetAsync("/api/tickets/T-1/specs/ts1/comments")).StatusCode);
    }

    [Fact]
    public async Task Blank_comment_is_rejected()
    {
        using var f = await Make();
        Assert.Equal(HttpStatusCode.BadRequest, (await Comment(f, Owner1, "  ")).StatusCode);
    }

    [Fact]
    public async Task New_comment_notifies_the_other_side_only()
    {
        using var f = await Make();
        var n = Outbox(f).Count;
        await Comment(f, Owner1, "問題");
        var m1 = Outbox(f).Skip(n).Single();
        Assert.Equal((EmailEvents.Commented, "CIM1", "T-1"), (m1.EventType, m1.RecipientEmpNo, m1.TicketId));
        await Comment(f, "CIM1", "答覆");
        var m2 = Outbox(f).Skip(n + 1).Single();
        Assert.Equal((EmailEvents.Commented, Owner1, "T-1"), (m2.EventType, m2.RecipientEmpNo, m2.TicketId));
        var (subject, html) = EmailTemplates.Render(m2, "http://studio");
        Assert.Contains("T-1", subject); Assert.Contains("ts1", html);
    }

    [Fact]
    public async Task System_owner_reassigns_with_reason_new_owner_is_notified_and_old_owner_loses_pending_entry()
    {
        using var f = await Make();
        var n = Outbox(f).Count;
        Assert.Equal(HttpStatusCode.OK, (await Move(f, Owner1, Owner2, "不是我們系統")).StatusCode);
        Assert.Equal(0, await PendingCount(f, Owner1));
        Assert.Equal(2, await PendingCount(f, Owner2)); // ts1 + 原本的 ts2
        var mail = Outbox(f).Skip(n).Single();
        Assert.Equal((EmailEvents.OwnerReassigned, Owner2, "T-1"), (mail.EventType, mail.RecipientEmpNo, mail.TicketId));
        Assert.Contains("ts1", mail.PayloadJson);
        Assert.Contains("T-1", EmailTemplates.Render(mail, "http://studio").Subject);
        // 舊 owner 不能再看評估畫面、回覆、留言；新 owner 可以
        Assert.Equal(HttpStatusCode.Forbidden, (await f.As(Owner1).GetAsync("/api/reviews/T-1/ts1")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Comment(f, Owner1, "x")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await f.As(Owner2).GetAsync("/api/reviews/T-1/ts1")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Comment(f, Owner2, "我接手")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await f.As(Owner2).PutAsJsonAsync("/api/reviews/T-1/ts1", new { result = "Feasible", estimatedDays = 1 })).StatusCode);
    }

    [Fact]
    public async Task Responsible_cim_engineer_can_reassign_and_comment_thread_survives()
    {
        using var f = await Make();
        await Comment(f, Owner1, "問題");
        Assert.Equal(HttpStatusCode.OK, (await Move(f, "CIM1", Owner2, "改由 ERP 評估")).StatusCode);
        Assert.Single(await Thread(f, Owner2));
        Assert.Equal(0, await PendingCount(f, Owner1));
    }

    [Theory]
    [InlineData("BIZ1")]
    [InlineData("SSO1")]
    [InlineData("CIM2")]
    [InlineData(Owner2)]
    public async Task Others_cannot_reassign(string who)
    {
        using var f = await Make();
        Assert.Equal(HttpStatusCode.Forbidden, (await Move(f, who, "CIM2", "x")).StatusCode);
    }

    [Theory]
    [InlineData(Owner2, "")]      // 缺原因
    [InlineData(null, "原因")]    // 缺對象
    [InlineData("nobody", "原因")] // 查無此人 → 422
    [InlineData(Owner1, "原因")]  // 轉給自己
    public async Task Reassign_validates_input(string? to, string reason)
    {
        using var f = await Make();
        var r = await Move(f, Owner1, to, reason);
        Assert.Equal(to == "nobody" ? HttpStatusCode.UnprocessableEntity : HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal(1, await PendingCount(f, Owner1));
    }

    [Fact]
    public async Task Reassign_is_recorded_and_chained_owner_can_hand_off_again()
    {
        using var f = await Make();
        await Move(f, Owner1, Owner2, "a");
        Assert.Equal(HttpStatusCode.Forbidden, (await Move(f, Owner1, "CIM2", "b")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Move(f, Owner2, Owner1, "c")).StatusCode);
        Assert.Equal(1, await PendingCount(f, Owner1));
        using var s = f.Services.CreateScope();
        var rows = s.ServiceProvider.GetRequiredService<StudioDb>().ReviewAssignments.OrderBy(x => x.Id).ToList();
        Assert.Equal([(Owner1, Owner2, "a"), (Owner2, Owner1, "c")], rows.Select(x => (x.FromEmpNo, x.ToEmpNo, x.Reason)));
    }
}
