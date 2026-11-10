using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AiAgentStudio.Api.Tickets;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AiAgentStudio.Api.Interviews;

public sealed record SseEvent(int Id, string Type, string Data);

/// <summary>一個進行中的 run：事件只存記憶體（spec 2.1，單一 instance 的已知上限），run 結束即從 runner 移除。</summary>
public sealed class ActiveRun(string runId, string ticketId, string empNo)
{
    public string RunId { get; } = runId;
    public string TicketId { get; } = ticketId;
    public string EmpNo { get; } = empNo;
    public CancellationTokenSource Cts { get; } = new();
    readonly List<SseEvent> events = [];
    bool completed;
    TaskCompletionSource tick = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Publish(string type, object data) => Push(type, JsonSerializer.Serialize(data));

    void Push(string type, string json)
    {
        TaskCompletionSource old;
        lock (events) { events.Add(new SseEvent(events.Count + 1, type, json)); old = tick; tick = new(TaskCreationOptions.RunContinuationsAsynchronously); }
        old.TrySetResult();
    }

    public void Complete()
    {
        TaskCompletionSource old;
        lock (events) { completed = true; old = tick; }
        old.TrySetResult();
    }

    /// <summary>回放 id 大於 afterId 的事件，之後跟著即時事件，直到 run 結束。</summary>
    public async IAsyncEnumerable<SseEvent> Stream(int afterId, [EnumeratorCancellation] CancellationToken ct)
    {
        while (true)
        {
            SseEvent[] batch; bool done; Task wait;
            lock (events) { batch = events.Skip(afterId).ToArray(); done = completed; wait = tick.Task; }
            foreach (var e in batch) { afterId = e.Id; yield return e; }
            if (batch.Length == 0 && done) yield break;
            if (batch.Length == 0) await wait.WaitAsync(ct);
        }
    }
}

public sealed class InterviewRunner(IServiceScopeFactory scopes, IAgentClient agent, RunTokenService tokens, TimeProvider time, IConfiguration cfg, ILogger<InterviewRunner> log)
{
    readonly ConcurrentDictionary<string, ActiveRun> active = new(); // key = ticketId：同一個 Interview 同時只能有一個 run

    public ActiveRun? Find(string ticketId) => active.GetValueOrDefault(ticketId);

    /// <summary>占住這張單的 run 名額；已有 run 時回 null。</summary>
    public ActiveRun? TryReserve(string ticketId, string empNo)
    {
        var run = new ActiveRun(Guid.NewGuid().ToString("N"), ticketId, empNo);
        return active.TryAdd(ticketId, run) ? run : null;
    }

    /// <summary>呼叫端必須已寫入使用者訊息並把 interview.IsRunning 設為 true。</summary>
    public void Launch(ActiveRun run, long interviewId) => _ = Task.Run(() => Execute(run, interviewId));

    public void Release(string ticketId) => active.TryRemove(ticketId, out _);

    public async Task CancelAsync(ActiveRun run)
    {
        run.Cts.Cancel();
        await agent.CancelAsync(run.RunId);
    }

    async Task Execute(ActiveRun run, long interviewId)
    {
        var started = time.GetUtcNow();
        var rec = new AgentRun { RunId = run.RunId, TicketId = run.TicketId, EmpNo = run.EmpNo, StartedAt = started, Result = "錯誤" };
        JsonArray? newMessages = null;
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<StudioDb>();
            var iv = await db.Interviews.FirstAsync(x => x.Id == interviewId);
            var messages = JsonNode.Parse(iv.ConversationJson)!.AsArray().Select(m => JsonSerializer.SerializeToElement(m)).ToArray();
            var req = new AgentRunRequest(run.RunId, messages, iv.OpeningContext, tokens.Issue(run.TicketId, run.EmpNo), cfg["Agent:ToolApiBaseUrl"] ?? "http://127.0.0.1:5080");

            try
            {
                var finished = false;
                await foreach (var e in agent.RunAsync(req, run.Cts.Token))
                {
                    if (e.Type == "text") run.Publish("text", new { text = e.Data.GetProperty("text").GetString() });
                    else if (e.Type == "error")
                    {
                        run.Publish("error", new { code = e.Data.TryGetProperty("code", out var c) ? c.GetString() : "error", message = Str(e.Data, "message") });
                        rec.Result = e.Data.TryGetProperty("code", out var c2) && c2.GetString() == "cancelled" ? "取消" : "錯誤";
                        finished = true; break;
                    }
                    else if (e.Type == "done")
                    {
                        newMessages = JsonNode.Parse(e.Data.GetProperty("messages").GetRawText())!.AsArray();
                        var u = e.Data.GetProperty("usage");
                        rec.Requests = u.GetProperty("requests").GetInt32();
                        rec.InputTokens = u.GetProperty("input_tokens").GetInt64();
                        rec.OutputTokens = u.GetProperty("output_tokens").GetInt64();
                        rec.Model = e.Data.TryGetProperty("model", out var m) ? m.GetString() : null;
                        rec.Result = "完成"; finished = true; break;
                    }
                }
                if (!finished) run.Publish("error", new { code = "interrupted", message = "與 agent service 的連線中斷。" });
            }
            catch (OperationCanceledException)
            {
                rec.Result = "取消";
                run.Publish("error", new { code = "cancelled", message = "已中止" });
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "agent run {RunId} failed", run.RunId);
                run.Publish("error", new { code = "error", message = "agent service 發生錯誤，請稍後再試。" });
            }

            // 正常結束才寫入 agent 新增的 messages；失敗或中止則整回合的回覆捨棄
            if (newMessages != null)
            {
                var all = JsonNode.Parse(iv.ConversationJson)!.AsArray();
                foreach (var m in newMessages.ToArray()) { newMessages.Remove(m); all.Add(m); }
                iv.ConversationJson = all.ToJsonString();
            }
            iv.IsRunning = false;
            rec.EndedAt = time.GetUtcNow();
            db.AgentRuns.Add(rec);
            await db.SaveChangesAsync();
            if (rec.Result == "完成") run.Publish("done", new { usage = new { requests = rec.Requests, input_tokens = rec.InputTokens, output_tokens = rec.OutputTokens } });
        }
        catch (Exception ex)
        {
            log.LogError(ex, "saving agent run {RunId} failed", run.RunId);
            run.Publish("error", new { code = "error", message = "儲存對話失敗。" });
        }
        finally
        {
            Release(run.TicketId);
            run.Complete();
        }
    }

    static string Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) ? v.GetString() ?? "" : "";
}
