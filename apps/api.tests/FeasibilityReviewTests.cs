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

/// <summary>#26：System Owner 的 Feasibility Review 回覆（spec 5.1、5.2、9、10.1）。只透過 HTTP 介面觀察。</summary>
public class FeasibilityReviewTests : IDisposable
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
    static async Task<string> SpecState(InterviewFactory f, string spec = "ts1") =>
        (await TicketFactory.Json(await f.As("CIM1").GetAsync("/api/tickets/T-1/submission"))).GetProperty("toolSpecs").EnumerateArray().First(x => x.GetProperty("specId").GetString() == spec).GetProperty("state").GetString()!;
    static async Task<string> Status(InterviewFactory f) => (await TicketFactory.Json(await f.As("CIM1").GetAsync("/api/tickets/T-1"))).GetProperty("status").GetString()!;
    static List<EmailOutbox> Outbox(InterviewFactory f)
    {
        using var s = f.Services.CreateScope();
        return s.ServiceProvider.GetRequiredService<StudioDb>().EmailOutbox.AsNoTracking().OrderBy(x => x.Id).ToList();
    }
    static void MarkHandled(InterviewFactory f, string spec = "ts1")
    {
        using var s = f.Services.CreateScope();
        var db = s.ServiceProvider.GetRequiredService<StudioDb>();
        db.ToolSpecRecords.First(x => x.SpecId == spec).Handled = true; db.SaveChanges();
    }

    // ---- 「待我評估」清單 ----

    [Fact]
    public async Task Pending_list_shows_only_my_assigned_specs_with_context_and_waiting_days_and_no_conversation()
    {
        using var f = await Make();
        time.Advance(TimeSpan.FromDays(3) + TimeSpan.FromHours(5));
        var list = await TicketFactory.Json(await f.As(Owner1).GetAsync("/api/reviews/pending"));
        var item = Assert.Single(list.EnumerateArray());
        Assert.Equal(("T-1", "ts1", 1, 3), (item.GetProperty("ticketId").GetString(), item.GetProperty("specId").GetString(), item.GetProperty("revision").GetInt32(), item.GetProperty("waitingDays").GetInt32()));
        Assert.Equal(("工程師一", "業務一"), (item.GetProperty("cimEngineerName").GetString(), item.GetProperty("businessUserName").GetString()));
        Assert.Equal("查工單", item.GetProperty("workflow")[0].GetProperty("description").GetString());
        Assert.False(item.TryGetProperty("conversation", out _));
        Assert.Empty((await TicketFactory.Json(await f.As("SSO1").GetAsync("/api/reviews/pending"))).EnumerateArray());
    }

    [Fact]
    public async Task Replied_spec_leaves_the_pending_list()
    {
        using var f = await Make();
        (await Reply(f, Owner1, Ok)).EnsureSuccessStatusCode();
        Assert.Empty((await TicketFactory.Json(await f.As(Owner1).GetAsync("/api/reviews/pending"))).EnumerateArray());
    }

    // ---- 回覆：狀態、通知、歷史 ----

    [Theory]
    [InlineData("Feasible")]
    [InlineData("Infeasible")]
    [InlineData("Conditional")]
    public async Task Reply_updates_spec_state_and_notifies_the_cim_engineer_by_empNo(string result)
    {
        using var f = await Make();
        var before = Outbox(f).Count;
        var r = await Reply(f, Owner1, new { result, reason = "原因", condition = "條件", estimatedDays = 2 });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(result, await SpecState(f));
        var mail = Outbox(f).Skip(before).Single();
        Assert.Equal((EmailEvents.Replied, "CIM1", "T-1"), (mail.EventType, mail.RecipientEmpNo, mail.TicketId));
        Assert.Contains("ts1", mail.PayloadJson);
        var (subject, html) = EmailTemplates.Render(mail, "http://studio");
        Assert.Contains("T-1", subject); Assert.Contains("ts1", html);
    }

    [Fact]
    public async Task Ticket_status_follows_the_spec_replies()
    {
        using var f = await Make();
        Assert.Equal("評估中", await Status(f));
        await Reply(f, Owner1, Ok);
        Assert.Equal("評估中", await Status(f));                // ts2 仍待評估
        await Reply(f, Owner2, new { result = "Infeasible", reason = "做不到" }, "ts2");
        Assert.Equal("評估中", await Status(f));                // 不可行仍是評估中
        await Reply(f, Owner2, new { result = "Feasible", estimatedDays = 1 }, "ts2"); // 修改回覆
        Assert.Equal("實作中", await Status(f));                // 全部可行（ts3 Reuse 已實作）
    }

    [Fact]
    public async Task Each_modification_adds_a_history_row_bound_to_the_revision()
    {
        using var f = await Make();
        await Reply(f, Owner1, Ok);
        await Reply(f, Owner1, new { result = "Infeasible", reason = "改變心意" });
        var d = await TicketFactory.Json(await f.As(Owner1).GetAsync("/api/reviews/T-1/ts1"));
        var hist = d.GetProperty("history").EnumerateArray().ToArray();
        Assert.Equal(["Feasible", "Infeasible"], hist.Select(h => h.GetProperty("result").GetString()));
        Assert.All(hist, h => Assert.Equal(1, h.GetProperty("revision").GetInt32()));
        Assert.Equal("Infeasible", d.GetProperty("state").GetString());
        Assert.True(d.GetProperty("canReply").GetBoolean());
        Assert.Equal("查工單", d.GetProperty("workflow")[0].GetProperty("description").GetString()); // 唯讀 Workflow Definition
        Assert.Equal("查工單", d.GetProperty("content").GetProperty("purpose").GetString());
        Assert.False(d.TryGetProperty("conversation", out _));
    }

    // ---- 權限與鎖定 ----

    [Theory]
    [InlineData("CIM1")]   // 負責的 CIM Engineer 不是被指派的 System Owner
    [InlineData("SSO1")]
    [InlineData(Owner2)]   // 另一位 System Owner
    public async Task Only_the_assigned_system_owner_may_reply(string who)
    {
        using var f = await Make();
        Assert.Equal(HttpStatusCode.Forbidden, (await Reply(f, who, Ok)).StatusCode);
        Assert.Equal("Pending", await SpecState(f));
        Assert.Equal(HttpStatusCode.Forbidden, (await f.As(who).GetAsync("/api/reviews/T-1/ts1")).StatusCode);
    }

    [Fact]
    public async Task Reply_is_rejected_after_the_cim_engineer_has_handled_it()
    {
        using var f = await Make();
        await Reply(f, Owner1, Ok);
        MarkHandled(f);
        Assert.Equal(HttpStatusCode.Conflict, (await Reply(f, Owner1, new { result = "Infeasible", reason = "x" })).StatusCode);
        Assert.Equal("Feasible", await SpecState(f));
        Assert.False((await TicketFactory.Json(await f.As(Owner1).GetAsync("/api/reviews/T-1/ts1"))).GetProperty("canReply").GetBoolean());
    }

    [Fact]
    public async Task Reuse_spec_and_unknown_spec_cannot_be_reviewed_and_system_owner_cannot_submit()
    {
        using var f = await Make();
        Assert.Equal(HttpStatusCode.NotFound, (await Reply(f, Owner1, Ok, "ts3")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Reply(f, Owner1, Ok, "nope")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await f.As(Owner1).PostAsync("/api/tickets/T-1/submit", null)).StatusCode);
    }

    [Fact]
    public async Task Reply_is_rejected_on_a_cancelled_ticket()
    {
        using var f = await Make();
        (await f.As("CIM1").PostAsJsonAsync("/api/tickets/T-1/cancel", new { reason = "不做了" })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, (await Reply(f, Owner1, Ok)).StatusCode);
    }

    // ---- 欄位驗證矩陣（spec 9）----

    public static IEnumerable<object?[]> Matrix() => new (string? result, string? reason, string? condition, double? days, bool valid)[]
    {
        (null, "r", "c", 1, false), ("", "r", "c", 1, false), ("Nope", "r", "c", 1, false),
        // 可行：原因選填、條件免、工時必填
        ("Feasible", null, null, 1, true), ("Feasible", "r", null, 1, true), ("Feasible", "  ", null, 1, true),
        ("Feasible", null, null, null, false), ("Feasible", null, null, 0, false), ("Feasible", null, null, -1, false),
        // 附條件：原因、條件、工時皆必填
        ("Conditional", "r", "c", 1, true), ("Conditional", null, "c", 1, false), ("Conditional", "  ", "c", 1, false),
        ("Conditional", "r", null, 1, false), ("Conditional", "r", " ", 1, false), ("Conditional", "r", "c", null, false), ("Conditional", "r", "c", 0, false),
        // 不可行：原因必填、工時免填
        ("Infeasible", "r", null, null, true), ("Infeasible", "r", null, 5, true), ("Infeasible", null, null, null, false), ("Infeasible", " ", null, null, false),
    }.Select(x => new object?[] { x.result, x.reason, x.condition, x.days, x.valid });

    [Theory]
    [MemberData(nameof(Matrix))]
    public async Task Reply_field_validation_matrix(string? result, string? reason, string? condition, double? days, bool valid)
    {
        using var f = await Make();
        var r = await Reply(f, Owner1, new { result, reason, condition, estimatedDays = days });
        Assert.Equal(valid ? HttpStatusCode.OK : HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal(valid ? result : "Pending", await SpecState(f));
        Assert.Equal(valid ? 1 : 0, Outbox(f).Count(x => x.EventType == EmailEvents.Replied)); // 驗證失敗不寄信
    }

    [Fact]
    public async Task Irrelevant_fields_are_not_kept()
    {
        using var f = await Make();
        await Reply(f, Owner1, new { result = "Infeasible", reason = "r", condition = "不該留", estimatedDays = 9 });
        var h = (await TicketFactory.Json(await f.As(Owner1).GetAsync("/api/reviews/T-1/ts1"))).GetProperty("history")[0];
        Assert.Equal(JsonValueKind.Null, h.GetProperty("condition").ValueKind);
        Assert.Equal(JsonValueKind.Null, h.GetProperty("estimatedDays").ValueKind);
    }
}
