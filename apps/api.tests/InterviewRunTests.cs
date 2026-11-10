using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AiAgentStudio.Api.Interviews;
using Microsoft.Extensions.DependencyInjection;

namespace AiAgentStudio.Api.Tests;

public class InterviewRunTests
{
    [Fact]
    public async Task Only_one_run_at_a_time_per_interview()
    {
        using var f = new InterviewFactory(); await f.Seed();
        f.Agent.Script = (_, a) => a.Blocked("a", "b");
        using var first = await Sse.Open(await f.Send("CIM1", "一"));
        await first.Next(1);

        var second = await f.Send("CIM1", "二");
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        var conv = await f.Conversation("CIM1");
        Assert.Equal(1, conv.GetProperty("messages").GetArrayLength()); // 被拒絕的訊息不會寫入

        f.Agent.Gate.SetResult(); await first.Next();
        Assert.Equal(HttpStatusCode.OK, (await f.Send("CIM1", "三")).StatusCode); // run 結束後可再送
    }

    [Fact]
    public async Task Reconnect_with_Last_Event_ID_replays_only_missed_events_and_buffer_is_dropped_after_run()
    {
        using var f = new InterviewFactory(); await f.Seed();
        f.Agent.Script = (_, a) => a.Blocked("前", "後");
        using (var first = await Sse.Open(await f.Send("CIM1", "q")))
            Assert.Equal("1", (await first.Next(1)).Single().Id); // 收到 id=1 後斷線

        var reconnect = await f.OpenEvents("CIM1", lastEventId: "1");
        f.Agent.Gate.SetResult();
        var resumed = await Sse.Read(reconnect);
        Assert.Equal(["2", "3"], resumed.Select(e => e.Id));
        Assert.Equal(["text", "done"], resumed.Select(e => e.Type));
        Assert.Equal("後", resumed[0].Data.GetProperty("text").GetString());

        Assert.Equal(HttpStatusCode.NotFound, (await f.OpenEvents("CIM1", lastEventId: "1")).StatusCode);
    }

    [Fact]
    public async Task Failed_run_discards_reply_keeps_user_message_and_can_be_retried_without_duplicate()
    {
        using var f = new InterviewFactory(); await f.Seed();
        f.Agent.Script = (_, _) => Fail();
        var events = await Sse.Read(await f.Send("CIM1", "問題"));
        Assert.Equal("error", events[^1].Type);
        Assert.Equal("model_busy", events[^1].Data.GetProperty("code").GetString());

        var conv = await f.Conversation("CIM1");
        Assert.Equal(1, conv.GetProperty("messages").GetArrayLength());
        Assert.Equal("錯誤", Assert.Single(f.Runs()).Result);

        f.Agent.Script = (_, a) => a.Reply("好了");
        Assert.Equal(["text", "done"], (await Sse.Read(await f.Send("CIM1", null))).Select(e => e.Type)); // 重試
        conv = await f.Conversation("CIM1");
        Assert.Equal(2, conv.GetProperty("messages").GetArrayLength());

        // 最後一則已是回覆時，沒有東西可重試
        Assert.Equal(HttpStatusCode.BadRequest, (await f.Send("CIM1", " ")).StatusCode);
    }

    static async IAsyncEnumerable<AgentEvent> Fail()
    {
        yield return FakeAgent.Ev("text", new { text = "半句" });
        yield return FakeAgent.Ev("error", new { code = "model_busy", message = "429" });
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Cancel_stops_run_and_asks_python_to_cancel()
    {
        using var f = new InterviewFactory(); await f.Seed();
        f.Agent.Script = (_, a) => a.Blocked("a", "b");
        using var stream = await Sse.Open(await f.Send("CIM1", "q"));
        await stream.Next(1);

        Assert.Equal(HttpStatusCode.Forbidden, (await f.As("CIM2").PostAsync("/api/tickets/T-1/interview/cancel", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await f.As("CIM1").PostAsync("/api/tickets/T-1/interview/cancel", null)).StatusCode);
        var rest = await stream.Next();
        Assert.Equal("cancelled", rest[^1].Data.GetProperty("code").GetString());
        Assert.Single(f.Agent.Cancelled);

        Assert.Equal(1, (await f.Conversation("CIM1")).GetProperty("messages").GetArrayLength());
        Assert.Equal("取消", Assert.Single(f.Runs()).Result);
    }

    [Fact]
    public async Task Each_run_records_usage()
    {
        using var f = new InterviewFactory(); await f.Seed();
        await Sse.Read(await f.Send("CIM1", "q"));
        var r = Assert.Single(f.Runs());
        Assert.Equal(("T-1", "CIM1", "完成"), (r.TicketId, r.EmpNo, r.Result));
        Assert.Equal((10L, 5L, 1), (r.InputTokens, r.OutputTokens, r.Requests));
        Assert.NotNull(r.EndedAt);
    }

    [Fact]
    public async Task Cancelled_ticket_cannot_chat()
    {
        using var f = new InterviewFactory(); await f.Seed();
        (await f.As("CIM1").PostAsJsonAsync("/api/tickets/T-1/cancel", new { reason = "不做了" })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, (await f.Send("CIM1", "hi")).StatusCode);
    }

    [Fact]
    public async Task Tool_api_accepts_only_a_valid_run_token_and_only_its_own_ticket()
    {
        using var f = new InterviewFactory(); await f.Seed();
        (await f.Create("CIM2", "T-2", "別張單")).EnsureSuccessStatusCode();
        var tokens = f.Services.GetRequiredService<RunTokenService>();

        HttpRequestMessage Req(string? token) { var r = new HttpRequestMessage(HttpMethod.Get, "/internal/tools/requirement-ticket"); if (token != null) r.Headers.Add("X-Run-Token", token); return r; }
        var anon = f.CreateClient(); // 沒有登入，只帶 run token

        var ok = await anon.SendAsync(Req(tokens.Issue("T-1", "CIM1")));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("貼上的需求描述", (await TicketFactory.Json(ok)).GetProperty("description").GetString()); // 只回 token 內那張單
        Assert.Equal("別張單", (await TicketFactory.Json(await anon.SendAsync(Req(tokens.Issue("T-2", "CIM2"))))).GetProperty("description").GetString());

        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.SendAsync(Req(null))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.SendAsync(Req("garbage"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.SendAsync(Req(tokens.Issue("T-1", "CIM1")[..^2] + "xx"))).StatusCode);
        // 一般使用者 JWT 不能當 run token 用
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.SendAsync(Req(TicketFactory.Token("CIM1")))).StatusCode);
    }

    class Clock : TimeProvider { public DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero); public override DateTimeOffset GetUtcNow() => Now; }

    [Fact]
    public void Run_token_is_bound_to_ticket_and_engineer_and_expires()
    {
        var clock = new Clock();
        var svc = new RunTokenService(clock, "secret");
        var t = svc.Issue("T-1", "CIM1");
        Assert.Equal(("T-1", "CIM1"), (svc.Validate(t)!.TicketId, svc.Validate(t)!.EmpNo));
        Assert.Null(new RunTokenService(clock, "other-secret").Validate(t)); // 不同金鑰簽的不算
        clock.Now += RunTokenService.Ttl + TimeSpan.FromSeconds(1);
        Assert.Null(svc.Validate(t));
    }
}
