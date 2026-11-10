using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiAgentStudio.Api.Interviews;
using AiAgentStudio.Api.Workflow;
using Microsoft.Extensions.DependencyInjection;

namespace AiAgentStudio.Api.Tests;

public class WorkflowDraftTests
{
    static HttpClient Agent(InterviewFactory f, string ticket = "T-1")
    {
        var c = f.CreateClient();
        c.DefaultRequestHeaders.Add("X-Run-Token", f.Services.GetRequiredService<RunTokenService>().Issue(ticket, "CIM1"));
        return c;
    }
    static Task<HttpResponseMessage> Upsert(HttpClient c, string id, object body) => c.PutAsJsonAsync($"/internal/tools/workflow-steps/{id}", body);
    static async Task<JsonElement> Draft(InterviewFactory f, string who = "CIM1") => await TicketFactory.Json(await f.As(who).GetAsync("/api/tickets/T-1/workflow-draft"));
    static string Desc(JsonElement d, int i) => d.GetProperty("steps")[i].GetProperty("description").GetString()!;
    static Task<HttpResponseMessage> Human(InterviewFactory f, int version, object steps, string who = "CIM1") =>
        f.As(who).PutAsJsonAsync("/api/tickets/T-1/workflow-draft", new { version, steps });

    [Fact]
    public async Task Agent_builds_linear_draft_step_by_step()
    {
        using var f = new InterviewFactory(); await f.Seed();
        var a = Agent(f);
        (await Upsert(a, "s1", new { description = "收單", tool_spec = (string?)null, needs_approval = false })).EnsureSuccessStatusCode();
        (await Upsert(a, "s2", new
        {
            description = "查詢",
            tool_spec = "ts-1",
            inputs = new object[] { new { name = "no", from_step = "s1", from_field = "order_no" } },
            outputs = new[] { "rows" },
            needs_approval = true,
            condition = "有資料才繼續",
        })).EnsureSuccessStatusCode();
        (await Upsert(a, "s1", new { description = "收單 v2" })).EnsureSuccessStatusCode(); // 更新不改順序

        var d = await Draft(f);
        Assert.Equal(3, d.GetProperty("version").GetInt32());
        Assert.Equal(["收單 v2", "查詢"], [Desc(d, 0), Desc(d, 1)]);
        Assert.Equal("ts-1", d.GetProperty("steps")[1].GetProperty("tool_spec").GetString());
        Assert.True(d.GetProperty("steps")[1].GetProperty("needs_approval").GetBoolean());
        Assert.Equal(0, d.GetProperty("humanEdits").GetArrayLength());

        var del = await a.DeleteAsync("/internal/tools/workflow-steps/s2");
        Assert.Equal(HttpStatusCode.OK, del.StatusCode);
        Assert.Equal(1, (await Draft(f)).GetProperty("steps").GetArrayLength());
    }

    [Fact]
    public async Task Tool_api_needs_run_token_and_stays_on_its_own_ticket()
    {
        using var f = new InterviewFactory(); await f.Seed();
        Assert.Equal(HttpStatusCode.Unauthorized, (await Upsert(f.CreateClient(), "s1", new { description = "x" })).StatusCode);
        (await f.Create("CIM2", "T-2", "別張")).EnsureSuccessStatusCode();
        (await Upsert(Agent(f, "T-2"), "s1", new { description = "他單" })).EnsureSuccessStatusCode();
        Assert.Equal(0, (await Draft(f)).GetProperty("steps").GetArrayLength());
    }

    [Fact]
    public async Task Draft_changed_event_is_inserted_into_the_run_stream()
    {
        using var f = new InterviewFactory(); await f.Seed();
        f.Agent.Script = (req, a) => WriteThenReply(f, req);
        var evs = await Sse.Read(await f.Send("CIM1", "開始"));
        Assert.Equal(["draft_changed", "text", "done"], evs.Select(e => e.Type));
        Assert.Equal(1, evs[0].Data.GetProperty("version").GetInt32());
    }
    static async IAsyncEnumerable<AgentEvent> WriteThenReply(InterviewFactory f, AgentRunRequest req)
    {
        var c = f.CreateClient(); c.DefaultRequestHeaders.Add("X-Run-Token", req.RunToken);
        (await Upsert(c, "s1", new { description = "收單" })).EnsureSuccessStatusCode();
        yield return FakeAgent.Ev("text", new { text = "ok" });
        yield return FakeAgent.Done("ok");
    }

    [Fact]
    public async Task Engineer_edit_requires_current_version_and_owner()
    {
        using var f = new InterviewFactory(); await f.Seed();
        (await Upsert(Agent(f), "s1", new { description = "A" })).EnsureSuccessStatusCode(); // v1
        var steps = new[] { new { id = "s1", description = "人工改" } };

        Assert.Equal(HttpStatusCode.Forbidden, (await Human(f, 1, steps, "CIM2")).StatusCode);
        var stale = await Human(f, 0, steps);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal(1, (await TicketFactory.Json(stale)).GetProperty("current").GetProperty("version").GetInt32()); // 衝突時回傳最新版供畫面提示

        Assert.Equal(HttpStatusCode.OK, (await Human(f, 1, steps)).StatusCode);
        var d = await Draft(f, "CIM2"); // 其他 CIM Engineer 可讀
        Assert.Equal(2, d.GetProperty("version").GetInt32());
        Assert.Equal("人工改", Desc(d, 0));
        Assert.Equal(["steps.s1.description"], d.GetProperty("humanEdits").EnumerateArray().Select(x => x.GetString()));
    }

    [Fact]
    public async Task Agent_write_to_human_edited_field_is_rejected_with_current_value_until_override()
    {
        using var f = new InterviewFactory(); await f.Seed();
        var a = Agent(f);
        await Upsert(a, "s1", new { description = "A", condition = "c" });
        await Human(f, 1, new[] { new { id = "s1", description = "人工", condition = "c" } });

        var rej = await Upsert(a, "s1", new { description = "agent 想改", condition = "c2" });
        Assert.Equal(HttpStatusCode.Conflict, rej.StatusCode);
        var r = (await TicketFactory.Json(rej)).GetProperty("rejected").EnumerateArray().Single();
        Assert.Equal(("steps.s1.description", "人工"), (r.GetProperty("path").GetString(), r.GetProperty("current").GetString()));
        var d = await Draft(f);
        Assert.Equal(("人工", "c"), (Desc(d, 0), d.GetProperty("steps")[0].GetProperty("condition").GetString())); // 整個請求不寫入

        // 沒改到人工欄位的寫入照常
        Assert.Equal(HttpStatusCode.OK, (await Upsert(a, "s1", new { condition = "c2" })).StatusCode);

        Assert.Empty(f.Events());
        var ov = await Upsert(a, "s1", new { description = "agent 覆寫", override_human_edit = true });
        Assert.Equal(HttpStatusCode.OK, ov.StatusCode);
        Assert.Equal("agent 覆寫", Desc(await Draft(f), 0));
        var ev = Assert.Single(f.Events());
        Assert.Equal(("OverrideHumanEdit", "CIM1", "steps.s1.description"), (ev.Type, ev.ActorEmpNo, ev.Reason));
        Assert.Equal(("\"人工\"", "\"agent 覆寫\""), (ev.FromValue, ev.ToValue));
        Assert.Contains("steps.s1.description", (await Draft(f)).GetProperty("humanEdits").EnumerateArray().Select(x => x.GetString())); // 標記保留
    }

    [Fact]
    public async Task Human_deleted_or_added_steps_are_protected_too()
    {
        using var f = new InterviewFactory(); await f.Seed();
        var a = Agent(f);
        await Upsert(a, "s1", new { description = "A" });
        // 人工刪掉 s1、新增 s9
        await Human(f, 1, new[] { new { id = "s9", description = "人工步驟" } });

        Assert.Equal(HttpStatusCode.Conflict, (await Upsert(a, "s1", new { description = "復活" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await a.DeleteAsync("/internal/tools/workflow-steps/s9")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Upsert(a, "s9", new { description = "改" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await a.DeleteAsync("/internal/tools/workflow-steps/s9?override_human_edit=true")).StatusCode);
        Assert.Equal(0, (await Draft(f)).GetProperty("steps").GetArrayLength());
    }

    [Fact]
    public async Task Each_run_gets_latest_draft_and_only_the_human_diff_since_last_run()
    {
        using var f = new InterviewFactory(); await f.Seed();
        await Upsert(Agent(f), "s1", new { description = "A" }); // agent 自己寫的不算 diff
        await Sse.Read(await f.Send("CIM1", "一"));
        var first = f.Agent.Requests[^1];
        Assert.Equal("A", first.Draft!["steps"]![0]!["description"]!.GetValue<string>());
        Assert.Empty(first.DraftDiff!.AsArray());

        await Human(f, 1, new[] { new { id = "s1", description = "B" } });
        await Sse.Read(await f.Send("CIM1", "二"));
        var diff = f.Agent.Requests[^1].DraftDiff!.AsArray().Single()!;
        Assert.Equal(("steps.s1.description", "A", "B"), (diff["path"]!.GetValue<string>(), diff["before"]!.GetValue<string>(), diff["after"]!.GetValue<string>()));

        await Sse.Read(await f.Send("CIM1", "三"));
        Assert.Empty(f.Agent.Requests[^1].DraftDiff!.AsArray()); // 已看過就不再重複
    }

    [Fact]
    public async Task Human_edit_marks_are_kept_until_interview_is_submitted()
    {
        using var f = new InterviewFactory(); await f.Seed();
        await Upsert(Agent(f), "s1", new { description = "A" });
        await Human(f, 1, new[] { new { id = "s1", description = "B" } });
        await Sse.Read(await f.Send("CIM1", "跨回合"));
        Assert.Equal(1, (await Draft(f)).GetProperty("humanEdits").GetArrayLength());

        using (var s = f.Services.CreateScope()) await s.ServiceProvider.GetRequiredService<WorkflowDraftService>().ClearHumanEditsAsync("T-1"); // 送出時呼叫
        Assert.Equal(0, (await Draft(f)).GetProperty("humanEdits").GetArrayLength());
        Assert.Equal(HttpStatusCode.OK, (await Upsert(Agent(f), "s1", new { description = "C" })).StatusCode);
    }
}
