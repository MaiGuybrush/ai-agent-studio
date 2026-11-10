using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AiAgentStudio.Api.Interviews;
using AiAgentStudio.Api.Notifications;
using Microsoft.Extensions.Options;
using AiAgentStudio.Api.Studio;
using AiAgentStudio.Api.Submissions;
using AiAgentStudio.Api.Tickets;
using AiAgentStudio.Api.Vocabulary;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using static AiAgentStudio.Api.Tests.VocabularyServiceTests;

namespace AiAgentStudio.Api.Tests;

/// <summary>#24：送出 Interview（spec 5、6.5、8.5、8.6）。Gitea 與 Python agent 都用假的。</summary>
public class SubmitInterviewTests : IDisposable
{
    readonly string path = Path.Combine(Path.GetTempPath(), $"studio-{Guid.NewGuid():N}.json");
    readonly string mailDir = Path.Combine(Path.GetTempPath(), $"mail-{Guid.NewGuid():N}");
    public void Dispose() { File.Delete(path); if (Directory.Exists(mailDir)) Directory.Delete(mailDir, true); }

    readonly FakeSource src = new() { Sha = "sha1", Text = Entries("eqptId", "status") };
    readonly FakeTimeProvider time = new(DateTimeOffset.UtcNow);

    static string Entries(params string[] camels) =>
        "class T { static object[] E = new[] { " + string.Join(",", camels.Select(c => $"new GlossaryEntry(\"{c}\", \"{char.ToUpper(c[0])}{c[1..]}\", \"d\", new[] {{ \"{c}Alias\" }})")) + " }; }";

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

    async Task<Factory> Make()
    {
        File.WriteAllText(path, """{ "mcpServers": { "gw": { "type": "http", "url": "http://gw/mcp" } }, "systems": { "MES": "10000001", "ERP": "10000002" } }""");
        var f = new Factory(path, src, time, mailDir);
        f.Names["10000001"] = "MES 負責人"; f.Names["10000002"] = "ERP 負責人";
        await f.Seed();
        return f;
    }

    static HttpClient Agent(InterviewFactory f)
    {
        var c = f.CreateClient();
        c.DefaultRequestHeaders.Add("X-Run-Token", f.Services.GetRequiredService<RunTokenService>().Issue("T-1", "CIM1"));
        return c;
    }
    static Task<HttpResponseMessage> Upsert(HttpClient c, string id, object body) => c.PutAsJsonAsync($"/internal/tools/tool-specs/{id}", body);
    static Task<HttpResponseMessage> Submit(HttpClient c) => c.PostAsync("/api/tickets/T-1/submit", null);
    static async Task<JsonElement> State(InterviewFactory f, string who = "CIM1") => await TicketFactory.Json(await f.As(who).GetAsync("/api/tickets/T-1/submission"));
    static async Task<string> Status(InterviewFactory f) => (await TicketFactory.Json(await f.As("CIM1").GetAsync("/api/tickets/T-1"))).GetProperty("status").GetString()!;
    static string[] Codes(JsonElement j) => j.GetProperty("blockers").EnumerateArray().Select(b => b.GetProperty("code").GetString()!).ToArray();

    static object Spec(string kind = "New", string? owner = "10000001", object? inputs = null, string system = "MES") => new Dictionary<string, object?>
    {
        ["kind"] = kind, ["purpose"] = "查工單", ["system"] = system, ["side_effect"] = "read",
        ["system_owner"] = kind == "Reuse" ? null : owner,
        ["ref"] = kind is "Reuse" or "Extend" ? new { server = "gw", tool = "get_order" } : null, // §6.2：Reuse／Extend 必填 ref
        ["inputs"] = inputs ?? new { type = "object", properties = new { eqptId = new { type = "string" } } },
        ["outputs"] = new { type = "object", properties = new { status = new { type = "string" } } },
    };

    [Fact]
    public async Task Submit_freezes_workflow_and_spec_revision_with_shared_submission_number_and_new_spec_awaits_evaluation()
    {
        using var f = await Make();
        await Agent(f).PutAsJsonAsync("/internal/tools/workflow-steps/s1", new { description = "查工單", tool_spec = "ts1" });
        await Upsert(Agent(f), "ts1", Spec());

        var r = await Submit(f.As("CIM1"));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var j = await TicketFactory.Json(r);
        Assert.Equal(1, j.GetProperty("number").GetInt32());
        Assert.Equal("評估中", await Status(f));

        var s = await State(f);
        var sub = Assert.Single(s.GetProperty("submissions").EnumerateArray());
        Assert.Equal(1, sub.GetProperty("number").GetInt32());
        Assert.Equal("查工單", sub.GetProperty("workflow")[0].GetProperty("description").GetString());
        var spec = Assert.Single(sub.GetProperty("specs").EnumerateArray());
        Assert.Equal(("ts1", 1, 1, "Pending"), (spec.GetProperty("specId").GetString(), spec.GetProperty("revision").GetInt32(), spec.GetProperty("submissionNumber").GetInt32(), spec.GetProperty("state").GetString()));
        Assert.Equal("MES 負責人", spec.GetProperty("content").GetProperty("system_owner_name").GetString());
    }

    [Fact]
    public async Task Reuse_is_implemented_at_submit_and_a_reuse_only_ticket_is_completed()
    {
        using var f = await Make();
        await Upsert(Agent(f), "ts1", Spec("Reuse"));
        Assert.Equal(HttpStatusCode.OK, (await Submit(f.As("CIM1"))).StatusCode);
        Assert.Equal("已完成", await Status(f));
        Assert.Equal("Implemented", (await State(f)).GetProperty("submissions")[0].GetProperty("specs")[0].GetProperty("state").GetString());
    }

    [Fact]
    public async Task Mixed_reuse_and_new_is_evaluating()
    {
        using var f = await Make();
        await Upsert(Agent(f), "ts1", Spec("Reuse"));
        await Upsert(Agent(f), "ts2", Spec("Extend"));
        await Submit(f.As("CIM1"));
        Assert.Equal("評估中", await Status(f));
    }

    [Fact]
    public async Task Extend_and_new_without_system_owner_block_submit_but_reuse_does_not()
    {
        using var f = await Make();
        await Upsert(Agent(f), "ts1", Spec("Reuse"));
        await Upsert(Agent(f), "ts2", Spec("New", owner: null));
        var s = await State(f);
        Assert.Equal(["owner"], Codes(s));
        Assert.Equal("ts2", s.GetProperty("blockers")[0].GetProperty("specId").GetString());
        Assert.Equal(1, s.GetProperty("pendingCount").GetInt32());
        var r = await Submit(f.As("CIM1"));
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Equal(["owner"], Codes(await TicketFactory.Json(r)));
        Assert.Equal("訪談中", await Status(f));
    }

    [Fact]
    public async Task Unknown_system_owner_blocks_submit()
    {
        using var f = await Make();
        await Upsert(Agent(f), "ts1", Spec(owner: "99999999"));
        Assert.Equal(["owner"], Codes(await State(f)));
    }

    [Fact]
    public async Task Unconfirmed_new_term_blocks_submit_until_cim_engineer_confirms_it()
    {
        using var f = await Make();
        await Upsert(Agent(f), "ts1", Spec(inputs: new { type = "object", properties = new { lotGrade = new { type = "string" } } }));
        var s = await State(f);
        Assert.Equal(["new_term"], Codes(s));
        Assert.Equal("inputs.lotGrade", s.GetProperty("blockers")[0].GetProperty("path").GetString());

        (await f.As("CIM1").PostAsJsonAsync("/api/tickets/T-1/pending-terms", new { term = "lotGrade", explanation = "批號等級" })).EnsureSuccessStatusCode();
        Assert.Empty(Codes(await State(f)));
        Assert.Equal(HttpStatusCode.OK, (await Submit(f.As("CIM1"))).StatusCode);
    }

    [Fact]
    public async Task Rename_suggestion_blocks_submit_until_renamed_or_a_reason_is_recorded_on_the_field()
    {
        using var f = await Make();
        await Upsert(Agent(f), "ts1", Spec(inputs: new { type = "object", properties = new { eqptIdAlias = new { type = "string" } } }));
        var s = await State(f);
        Assert.Equal(["rename_reason"], Codes(s));
        Assert.Equal("inputs.eqptIdAlias", s.GetProperty("blockers")[0].GetProperty("path").GetString());

        // 理由記在欄位上（spec 的 naming_reasons[欄位路徑]）；空白不算
        await Upsert(Agent(f), "ts1", new { naming_reasons = new { inputs_eqptIdAlias = "x" } });
        Assert.Equal(["rename_reason"], Codes(await State(f)));
        await Upsert(Agent(f), "ts1", new Dictionary<string, object> { ["naming_reasons"] = new Dictionary<string, string> { ["inputs.eqptIdAlias"] = "  " } });
        Assert.Equal(["rename_reason"], Codes(await State(f)));
        await Upsert(Agent(f), "ts1", new Dictionary<string, object> { ["naming_reasons"] = new Dictionary<string, string> { ["inputs.eqptIdAlias"] = "舊系統沿用此名稱" } });
        Assert.Empty(Codes(await State(f)));
        Assert.Equal(HttpStatusCode.OK, (await Submit(f.As("CIM1"))).StatusCode);
    }

    [Fact]
    public async Task Without_any_vocabulary_submit_is_blocked_because_naming_cannot_be_verified()
    {
        src.Fail = new HttpRequestException("gitea down");
        using var f = await Make();
        await Upsert(Agent(f), "ts1", Spec());
        Assert.Equal(["naming_unavailable"], Codes(await State(f)));
    }

    [Fact]
    public async Task Ticket_without_tool_specs_cannot_be_submitted()
    {
        using var f = await Make();
        var raw = await f.As("CIM1").GetAsync("/api/tickets/T-1/submission"); Assert.True(raw.IsSuccessStatusCode, await raw.Content.ReadAsStringAsync()); Assert.Equal(["no_tool_specs"], Codes(await TicketFactory.Json(raw)));
    }

    [Fact]
    public async Task Missing_required_fields_block_submit()
    {
        using var f = await Make();
        await Upsert(Agent(f), "ts1", new { kind = "New", purpose = "p", system = "MES", system_owner = "10000001" });
        Assert.Equal(["missing_field"], Codes(await State(f)));
    }

    [Fact]
    public async Task Only_the_responsible_cim_engineer_can_submit_and_the_agent_cannot()
    {
        using var f = await Make();
        await Upsert(Agent(f), "ts1", Spec("Reuse"));
        Assert.Equal(HttpStatusCode.Forbidden, (await Submit(f.As("CIM2"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Submit(f.As("BIZ1"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Submit(Agent(f))).StatusCode); // run token 不是登入憑證
        Assert.Equal(HttpStatusCode.Unauthorized, (await Submit(f.CreateClient())).StatusCode);
        Assert.Equal("訪談中", await Status(f));
    }

    [Fact]
    public async Task Non_cim_users_cannot_read_the_submission_state()
    {
        using var f = await Make();
        Assert.Equal(HttpStatusCode.Forbidden, (await f.As("SSO1").GetAsync("/api/tickets/T-1/submission")).StatusCode);
    }

    [Fact]
    public async Task Submit_clears_human_edit_marks_and_reruns_naming_check_without_changing_status()
    {
        using var f = await Make();
        await Upsert(Agent(f), "ts1", Spec());
        var d = await TicketFactory.Json(await f.As("CIM1").GetAsync("/api/tickets/T-1/tool-spec-draft"));
        var steps = d.GetProperty("steps").EnumerateArray().Select(x => JsonSerializer.Deserialize<Dictionary<string, object>>(x.GetRawText())!).ToList();
        steps[0]["purpose"] = "人工改過";
        (await f.As("CIM1").PutAsJsonAsync("/api/tickets/T-1/tool-spec-draft", new { version = d.GetProperty("version").GetInt32(), steps })).EnsureSuccessStatusCode();
        Assert.NotEmpty((await TicketFactory.Json(await f.As("CIM1").GetAsync("/api/tickets/T-1/tool-spec-draft"))).GetProperty("humanEdits").EnumerateArray());

        (src.Sha, src.Text) = ("sha2", Entries("eqptId", "status", "other"));
        time.Advance(TimeSpan.FromMinutes(6));
        Assert.Equal(HttpStatusCode.OK, (await Submit(f.As("CIM1"))).StatusCode);

        var after = await TicketFactory.Json(await f.As("CIM1").GetAsync("/api/tickets/T-1/tool-spec-draft"));
        Assert.Empty(after.GetProperty("humanEdits").EnumerateArray());
        Assert.Equal("sha2", after.GetProperty("steps")[0].GetProperty("naming_check").GetProperty("sha").GetString());
        Assert.Equal("人工改過", (await State(f)).GetProperty("submissions")[0].GetProperty("specs")[0].GetProperty("content").GetProperty("purpose").GetString());
    }

    [Fact]
    public async Task After_submit_the_interview_input_is_disabled_and_resubmit_is_rejected()
    {
        using var f = await Make();
        await Upsert(Agent(f), "ts1", Spec("Reuse"));
        Assert.True((await TicketFactory.Json(await f.As("CIM1").GetAsync("/api/tickets/T-1"))).GetProperty("permissions").GetProperty("canChat").GetBoolean());
        await Submit(f.As("CIM1"));
        var t = await TicketFactory.Json(await f.As("CIM1").GetAsync("/api/tickets/T-1"));
        Assert.False(t.GetProperty("permissions").GetProperty("canChat").GetBoolean());
        Assert.Equal(HttpStatusCode.Conflict, (await f.Send("CIM1", "還要改")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Submit(f.As("CIM1"))).StatusCode);
        Assert.Equal("Submit", f.Events().Last().Type);
    }

    [Fact]
    public async Task Cancelling_after_submit_shows_cancelled_and_reopen_returns_to_the_derived_status()
    {
        using var f = await Make();
        await Upsert(Agent(f), "ts1", Spec());
        await Submit(f.As("CIM1"));
        (await f.As("CIM1").PostAsJsonAsync("/api/tickets/T-1/cancel", new { reason = "不做了" })).EnsureSuccessStatusCode();
        Assert.Equal("已取消", await Status(f));
        (await f.As("CIM1").PostAsync("/api/tickets/T-1/reopen", null)).EnsureSuccessStatusCode();
        Assert.Equal("評估中", await Status(f));
    }

    [Fact]
    public async Task Status_filter_in_the_ticket_list_is_derived_per_ticket()
    {
        using var f = await Make();
        (await f.Create("CIM1", "T-2")).EnsureSuccessStatusCode();
        await Upsert(Agent(f), "ts1", Spec("Reuse"));
        await Submit(f.As("CIM1"));
        var list = await TicketFactory.Json(await f.As("CIM1").GetAsync("/api/tickets"));
        Assert.Equal(["T-1:已完成", "T-2:訪談中"], list.EnumerateArray().Select(x => $"{x.GetProperty("id").GetString()}:{x.GetProperty("status").GetString()}").Order().ToArray());
    }

    [Fact]
    public async Task State_suggests_system_owners_from_the_system_table()
    {
        using var f = await Make();
        var o = (await State(f)).GetProperty("suggestedOwners").EnumerateArray().ToDictionary(x => x.GetProperty("system").GetString()!);
        Assert.Equal(("10000001", "MES 負責人"), (o["MES"].GetProperty("empNo").GetString(), o["MES"].GetProperty("name").GetString()));
        Assert.Equal("10000002", o["ERP"].GetProperty("empNo").GetString());
    }

    [Fact]
    public async Task State_reports_the_interview_stage_so_the_ui_can_expand_blockers_only_at_stages_5_and_6()
    {
        using var f = await Make();
        async Task<int> Stage() => (await State(f)).GetProperty("stage").GetInt32();
        Assert.Equal(1, await Stage());                                                   // 還沒有任何步驟
        await Agent(f).PutAsJsonAsync("/internal/tools/workflow-steps/s1", new { description = "查工單" });
        Assert.Equal(2, await Stage());                                                   // 有步驟、還沒有 Tool Spec
        await Upsert(Agent(f), "ts1", new { kind = "New", purpose = "p", system = "MES" });
        Assert.Equal(4, await Stage());                                                   // Tool Spec 欄位還沒補齊
        await Upsert(Agent(f), "ts1", Spec(owner: null));
        Assert.Equal(5, await Stage());                                                   // 剩 System Owner 等待處理
        await Upsert(Agent(f), "ts1", new { system_owner = "10000001" });
        Assert.Equal(6, await Stage());                                                   // 可以送出
    }

    // ---- #25：送出時寫入 email_outbox（同一交易），寄給 System Owner ----

    static List<EmailOutbox> Outbox(InterviewFactory f)
    {
        using var s = f.Services.CreateScope();
        return s.ServiceProvider.GetRequiredService<StudioDb>().EmailOutbox.AsNoTracking().OrderBy(x => x.Id).ToList();
    }
    static async Task Dispatch(InterviewFactory f)
    {
        using var s = f.Services.CreateScope();
        await s.ServiceProvider.GetRequiredService<EmailOutboxDispatcher>().DispatchDueAsync(default);
    }

    [Fact]
    public async Task Submit_queues_one_email_per_system_owner_by_empNo_and_local_folder_sender_delivers_it()
    {
        using var f = await Make();
        await Upsert(Agent(f), "ts1", Spec(owner: "10000001"));
        await Upsert(Agent(f), "ts2", Spec("Extend", owner: "10000001"));
        await Upsert(Agent(f), "ts3", Spec(owner: "10000002", system: "ERP"));
        await Upsert(Agent(f), "ts4", Spec("Reuse"));
        Assert.Empty(Outbox(f));

        Assert.Equal(HttpStatusCode.OK, (await Submit(f.As("CIM1"))).StatusCode);

        var rows = Outbox(f);
        Assert.Equal(["10000001", "10000002"], rows.Select(r => r.RecipientEmpNo).ToArray()); // 只存工號
        Assert.All(rows, r => Assert.Equal((EmailEvents.Submitted, "T-1", EmailOutbox.Pending), (r.EventType, r.TicketId, r.Status)));

        await Dispatch(f);
        Assert.All(Outbox(f), r => Assert.Equal(EmailOutbox.Sent, r.Status));
        var mails = Directory.GetFiles(mailDir).Select(File.ReadAllText).ToList();
        Assert.Equal(2, mails.Count);
        var owner1 = Assert.Single(mails, m => m.Contains("ad10000001@innolux.com"));
        Assert.Contains("ts1", owner1); Assert.Contains("ts2", owner1); Assert.DoesNotContain("ts3", owner1); Assert.DoesNotContain("ts4", owner1);
        Assert.Contains("T-1", owner1);
        Assert.Contains("/tickets/T-1", owner1);
        Assert.DoesNotContain("查工單", owner1); // 不附 Tool Spec 內容
    }

    [Fact]
    public async Task Reuse_only_or_blocked_submit_queues_nothing()
    {
        using var f = await Make();
        await Upsert(Agent(f), "ts1", Spec("Reuse"));
        await Submit(f.As("CIM1"));
        Assert.DoesNotContain(Outbox(f), e => e.EventType is EmailEvents.Submitted or EmailEvents.Resubmitted); // 沒有人要評估；只有完成通知（見 ReviewFixTests）

        using var g = await Make();
        await Upsert(Agent(g), "ts1", Spec(owner: null));
        Assert.Equal(HttpStatusCode.Conflict, (await Submit(g.As("CIM1"))).StatusCode);
        Assert.Empty(Outbox(g));
    }

    [Fact]
    public async Task Resubmit_queues_a_reevaluation_email_only_for_changed_specs()
    {
        using var f = await Make();
        await Upsert(Agent(f), "ts1", Spec(owner: "10000001"));
        await Upsert(Agent(f), "ts2", Spec(owner: "10000002", system: "ERP"));
        await Submit(f.As("CIM1"));

        using (var s = f.Services.CreateScope()) // 模擬重開訪談（之後的 ticket 才會提供入口）
        {
            var db = s.ServiceProvider.GetRequiredService<StudioDb>();
            db.Tickets.Single().Status = TicketStatus.Interviewing;
            await db.SaveChangesAsync();
        }
        await Upsert(Agent(f), "ts1", new { purpose = "改過的用途" });
        Assert.Equal(HttpStatusCode.OK, (await Submit(f.As("CIM1"))).StatusCode);

        var again = Outbox(f).Where(r => r.EventType == EmailEvents.Resubmitted).ToList();
        var row = Assert.Single(again);
        Assert.Equal("10000001", row.RecipientEmpNo);
        Assert.Equal(3, Outbox(f).Count);
    }
}
