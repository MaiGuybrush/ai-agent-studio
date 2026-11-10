using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AiAgentStudio.Api.Interviews;
using AiAgentStudio.Api.Tickets;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AiAgentStudio.Api.Tests;

/// <summary>假的 Python agent：由測試腳本決定事件，並可用 Gate 讓 run 停在中途。</summary>
public class FakeAgent : IAgentClient
{
    public Func<AgentRunRequest, FakeAgent, IAsyncEnumerable<AgentEvent>> Script = (_, a) => a.Reply("哈囉");
    public List<AgentRunRequest> Requests = [];
    public List<string> Cancelled = [];
    public TaskCompletionSource Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static AgentEvent Ev(string type, object data) =>
        new(type, JsonSerializer.SerializeToElement(data));

    public static AgentEvent Done(string reply) => Ev("done", new
    {
        messages = new object[] { new { kind = "response", parts = new object[] { new { part_kind = "text", content = reply } } } },
        usage = new { requests = 1, input_tokens = 10, output_tokens = 5 },
    });

    public async IAsyncEnumerable<AgentEvent> Reply(string text)
    {
        yield return Ev("text", new { text });
        yield return Done(text);
        await Task.CompletedTask;
    }

    public IAsyncEnumerable<AgentEvent> RunAsync(AgentRunRequest req, CancellationToken ct)
    {
        Requests.Add(req);
        return Wrap(Script(req, this), ct);
    }

    static async IAsyncEnumerable<AgentEvent> Wrap(IAsyncEnumerable<AgentEvent> src, [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var e in src.WithCancellation(ct)) yield return e;
    }

    public Task CancelAsync(string runId) { Cancelled.Add(runId); return Task.CompletedTask; }

    /// <summary>先吐一個 text，然後等 Gate 放行（或被取消）再完成。</summary>
    public async IAsyncEnumerable<AgentEvent> Blocked(string first, string rest, [EnumeratorCancellation] CancellationToken ct = default)
    {
        yield return Ev("text", new { text = first });
        await Gate.Task.WaitAsync(ct);
        yield return Ev("text", new { text = rest });
        yield return Done(first + rest);
    }
}

public class InterviewFactory : TicketFactory
{
    public readonly FakeAgent Agent = new();

    protected override void ConfigureWebHost(IWebHostBuilder b)
    {
        base.ConfigureWebHost(b);
        b.ConfigureServices(s =>
        {
            s.RemoveAll<IAgentClient>();
            s.AddSingleton<IAgentClient>(Agent);
        });
    }

    /// <summary>送訊息；成功時回應本身就是這個 run 的 SSE 串流。</summary>
    public async Task<HttpResponseMessage> Send(string who, string? text, string ticket = "T-1") =>
        await As(who).SendAsync(new HttpRequestMessage(HttpMethod.Post, $"/api/tickets/{ticket}/interview/messages") { Content = JsonContent.Create(new { text }) },
            HttpCompletionOption.ResponseHeadersRead);

    public async Task<HttpResponseMessage> OpenEvents(string who, string? lastEventId = null, string ticket = "T-1")
    {
        var c = As(who);
        var req = new HttpRequestMessage(HttpMethod.Get, $"/api/tickets/{ticket}/interview/events");
        if (lastEventId != null) req.Headers.Add("Last-Event-ID", lastEventId);
        return await c.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
    }

    public async Task<JsonElement> Conversation(string who, string ticket = "T-1") =>
        await Json(await As(who).GetAsync($"/api/tickets/{ticket}/interview"));

    public List<AgentRun> Runs()
    {
        using var s = Services.CreateScope();
        return s.ServiceProvider.GetRequiredService<StudioDb>().AgentRuns.AsNoTracking().ToList();
    }

    /// <summary>建一張 CIM1 負責的單。</summary>
    public async Task Seed() => (await Create("CIM1", "T-1", "貼上的需求描述")).EnsureSuccessStatusCode();
}

public record SseEvent(string? Id, string Type, JsonElement Data);

public class SseReader(Stream stream) : IDisposable
{
    readonly StreamReader rd = new(stream);
    public void Dispose() => rd.Dispose();

    /// <summary>讀到 count 個事件或串流結束為止。</summary>
    public async Task<List<SseEvent>> Next(int count = int.MaxValue)
    {
        var list = new List<SseEvent>();
        string? id = null, type = "message", data = null;
        while (list.Count < count && await rd.ReadLineAsync() is { } line)
        {
            if (line.StartsWith("id:")) id = line[3..].Trim();
            else if (line.StartsWith("event:")) type = line[6..].Trim();
            else if (line.StartsWith("data:")) data = line[5..].Trim();
            else if (line == "" && data != null)
            {
                list.Add(new SseEvent(id, type, JsonDocument.Parse(data).RootElement.Clone()));
                id = null; type = "message"; data = null;
            }
        }
        return list;
    }
}

public static class Sse
{
    public static async Task<SseReader> Open(HttpResponseMessage r) => new(await r.Content.ReadAsStreamAsync());

    /// <summary>讀完整個串流。</summary>
    public static async Task<List<SseEvent>> Read(HttpResponseMessage r)
    {
        using var rd = await Open(r);
        return await rd.Next();
    }
}
