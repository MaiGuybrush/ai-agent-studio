using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AiAgentStudio.Api.Interviews;
using Microsoft.Extensions.DependencyInjection;

namespace AiAgentStudio.Api.Tests;

public class InterviewTests
{
    static string Text(JsonElement conv, int i) => conv.GetProperty("messages")[i].GetProperty("text").GetString()!;
    static string Role(JsonElement conv, int i) => conv.GetProperty("messages")[i].GetProperty("role").GetString()!;

    [Fact]
    public async Task Responsible_engineer_chats_and_conversation_is_kept()
    {
        using var f = new InterviewFactory(); await f.Seed();
        var send = await f.Send("CIM1", "我要做換線通知");
        Assert.Equal(HttpStatusCode.OK, send.StatusCode);
        Assert.Equal("text/event-stream", send.Content.Headers.ContentType!.MediaType);

        var events = await Sse.Read(send);
        Assert.Equal(["text", "done"], events.Select(e => e.Type));
        Assert.Equal(["1", "2"], events.Select(e => e.Id));
        Assert.Equal("哈囉", events[0].Data.GetProperty("text").GetString());
        // 給瀏覽器的 done 不帶 messages（由 C# 存 DB）
        Assert.False(events[1].Data.TryGetProperty("messages", out _));

        // 重新整理／隔天回來：從 DB 讀得到完整對話
        var conv = await f.Conversation("CIM1");
        Assert.False(conv.GetProperty("running").GetBoolean());
        Assert.Equal(2, conv.GetProperty("messages").GetArrayLength());
        Assert.Equal(("user", "我要做換線通知"), (Role(conv, 0), Text(conv, 0)));
        Assert.Equal(("assistant", "哈囉"), (Role(conv, 1), Text(conv, 1)));

        // 傳給 Python 的內容：整段對話（最後一則是剛送的）、建單描述、run token
        var req = Assert.Single(f.Agent.Requests);
        Assert.Equal("貼上的需求描述", req.OpeningContext);
        Assert.Single(req.Messages);
        var claims = f.Services.GetRequiredService<RunTokenService>().Validate(req.RunToken)!;
        Assert.Equal(("T-1", "CIM1"), (claims.TicketId, claims.EmpNo));

        // 第二回合：歷史也要帶上
        await Sse.Read(await f.Send("CIM1", "再問一個"));
        Assert.Equal(3, f.Agent.Requests[1].Messages.Length);
    }

    [Fact]
    public async Task User_message_saved_before_run_but_agent_reply_only_at_normal_end()
    {
        using var f = new InterviewFactory(); await f.Seed();
        f.Agent.Script = (_, a) => a.Blocked("前半", "後半");
        using var stream = await Sse.Open(await f.Send("CIM1", "問題"));
        await stream.Next(1); // 已經串流出第一段文字

        var mid = await f.Conversation("CIM1");
        Assert.True(mid.GetProperty("running").GetBoolean());
        Assert.Equal(1, mid.GetProperty("messages").GetArrayLength());
        Assert.Equal("問題", Text(mid, 0));

        f.Agent.Gate.SetResult();
        await stream.Next();
        Assert.Equal(2, (await f.Conversation("CIM1")).GetProperty("messages").GetArrayLength());
    }

    [Fact]
    public async Task Other_cim_engineer_can_read_but_not_send_and_others_see_nothing()
    {
        using var f = new InterviewFactory(); await f.Seed();
        await Sse.Read(await f.Send("CIM1", "hi"));

        Assert.Equal(HttpStatusCode.OK, (await f.As("CIM2").GetAsync("/api/tickets/T-1/interview")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await f.Send("CIM2", "插嘴")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await f.As("BIZ1").GetAsync("/api/tickets/T-1/interview")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await f.OpenEvents("BIZ1")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await f.Send("BIZ1", "x")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await f.As("SSO1").GetAsync("/api/tickets/T-1/interview")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await f.As("CIM1").GetAsync("/api/tickets/NOPE/interview")).StatusCode);
    }

    [Fact]
    public async Task Ticket_permissions_tell_the_ui_who_can_chat()
    {
        using var f = new InterviewFactory(); await f.Seed();
        async Task<bool> CanChat(string who) =>
            (await TicketFactory.Json(await f.As(who).GetAsync("/api/tickets/T-1"))).GetProperty("permissions").GetProperty("canChat").GetBoolean();
        Assert.True(await CanChat("CIM1"));
        Assert.False(await CanChat("CIM2"));
        Assert.False(await CanChat("BIZ1"));
        (await f.As("CIM1").PostAsJsonAsync("/api/tickets/T-1/cancel", new { reason = "x" })).EnsureSuccessStatusCode();
        Assert.False(await CanChat("CIM1"));
    }
}
