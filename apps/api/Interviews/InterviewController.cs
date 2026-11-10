using Microsoft.AspNetCore.Http;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AiAgentStudio.Api.Identity;
using AiAgentStudio.Api.Services;
using AiAgentStudio.Api.Tickets;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AiAgentStudio.Api.Interviews;

public record SendMessageRequest(string? Text);

/// <summary>Interview 對話（spec 2.4、8.6）。瀏覽器只連這裡；C# 做完權限檢查才轉發 Python 的串流。</summary>
[ApiController]
[Route("api/tickets/{ticketId}/interview")]
[Authorize]
public class InterviewController(ICurrentUserService user, IdentityService identity, StudioDb db, InterviewRunner runner, TimeProvider time) : ControllerBase
{
    async Task<UserIdentity?> WhoAsync(CancellationToken ct)
    {
        if (user.EmpNo is not { } empNo) return null;
        var jwt = Request.Headers.Authorization.ToString()["Bearer ".Length..].Trim();
        return await identity.GetAsync(empNo, jwt, ct);
    }

    /// <summary>讀對話：任何 CIM Engineer（spec 3.3 canViewConversation）。</summary>
    [HttpGet]
    public async Task<IActionResult> Get(string ticketId, CancellationToken ct)
    {
        if (await WhoAsync(ct) is not { IsCimEngineer: true }) return Forbid();
        if (!await db.Tickets.AnyAsync(x => x.Id == ticketId, ct)) return NotFound();
        var iv = await db.Interviews.AsNoTracking().Where(x => x.TicketId == ticketId).OrderByDescending(x => x.Id).FirstOrDefaultAsync(ct);
        return Ok(new { running = runner.Find(ticketId) != null, messages = iv is null ? [] : Display(iv.ConversationJson) });
    }

    /// <summary>送訊息並以 SSE 串流本回合回覆。text 為空 = 重試上一則未得到回覆的使用者訊息。</summary>
    [HttpPost("messages")]
    public async Task<IActionResult> Send(string ticketId, SendMessageRequest req, CancellationToken ct)
    {
        if (await WhoAsync(ct) is not { IsCimEngineer: true } me) return Forbid();
        var t = await db.Tickets.AsNoTracking().FirstOrDefaultAsync(x => x.Id == ticketId, ct);
        if (t is null) return NotFound();
        if (t.CimEngineerEmpNo != me.EmpNo) return Forbid();
        if (t.Status == TicketStatus.Cancelled) return Problem("需求單已取消，無法對話。", statusCode: 409);
        var run = runner.TryReserve(ticketId, me.EmpNo);
        if (run is null) return Problem("上一個回覆尚未結束，請等待或先中止。", statusCode: 409);
        try
        {
            return await SendReserved(run, t, req, ct);
        }
        catch { runner.Release(ticketId); throw; }
    }

    async Task<IActionResult> SendReserved(ActiveRun run, RequirementTicket t, SendMessageRequest req, CancellationToken ct)
    {
        var ticketId = t.Id;
        var iv = await db.Interviews.Where(x => x.TicketId == ticketId).OrderByDescending(x => x.Id).FirstOrDefaultAsync(ct)
            ?? db.Interviews.Add(new Interview { TicketId = ticketId, OpeningContext = t.Description, CreatedAt = time.GetUtcNow() }).Entity;
        var conv = JsonNode.Parse(iv.ConversationJson)!.AsArray();
        if (string.IsNullOrWhiteSpace(req.Text))
        {
            if (conv.Count == 0 || Kind(conv[^1]) != "request") { runner.Release(ticketId); return Problem("沒有可重試的訊息。", statusCode: 400); }
        }
        else
        {
            conv.Add(JsonNode.Parse(JsonSerializer.Serialize(new { kind = "request", parts = new[] { new { part_kind = "user-prompt", content = req.Text.Trim() } } })));
            iv.ConversationJson = conv.ToJsonString();
        }
        iv.IsRunning = true;
        await db.SaveChangesAsync(ct); // 使用者訊息在 run 開始前寫入

        runner.Launch(run, iv.Id);
        return await StreamAsync(run, 0, ct);
    }

    /// <summary>斷線重連：Last-Event-ID 之後的事件（只存在記憶體，run 結束即消失）。</summary>
    [HttpGet("events")]
    public async Task<IActionResult> Events(string ticketId, CancellationToken ct)
    {
        if (await WhoAsync(ct) is not { IsCimEngineer: true }) return Forbid();
        var run = runner.Find(ticketId);
        if (run is null) return NotFound();
        _ = int.TryParse(Request.Headers["Last-Event-ID"].ToString(), out var last);
        return await StreamAsync(run, last, ct);
    }

    [HttpPost("cancel")]
    public async Task<IActionResult> Cancel(string ticketId, CancellationToken ct)
    {
        if (await WhoAsync(ct) is not { IsCimEngineer: true } me) return Forbid();
        var t = await db.Tickets.AsNoTracking().FirstOrDefaultAsync(x => x.Id == ticketId, ct);
        if (t is null) return NotFound();
        if (t.CimEngineerEmpNo != me.EmpNo) return Forbid();
        var run = runner.Find(ticketId);
        if (run is null) return Problem("目前沒有執行中的回覆。", statusCode: 409);
        await runner.CancelAsync(run);
        return Accepted();
    }

    async Task<IActionResult> StreamAsync(ActiveRun run, int afterId, CancellationToken ct)
    {
        Response.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers["X-Accel-Buffering"] = "no";
        try
        {
            await Response.Body.FlushAsync(ct); // 先送出標頭，讓重連的一方立刻知道連上了
            await foreach (var e in run.Stream(afterId, ct))
            {
                await Response.WriteAsync($"id: {e.Id}\nevent: {e.Type}\ndata: {e.Data}\n\n", Encoding.UTF8, ct);
                await Response.Body.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException) { } // 瀏覽器斷線：run 繼續跑，可重連
        return new EmptyResult();
    }

    static string? Kind(JsonNode? m) => m?["kind"]?.GetValue<string>();

    /// <summary>把 pydantic-ai 的 messages 轉成畫面用的 {role, text}；工具呼叫等其他 part 略過。</summary>
    static List<object> Display(string json)
    {
        var list = new List<object>();
        foreach (var m in JsonNode.Parse(json)!.AsArray())
        {
            var isUser = Kind(m) == "request";
            var text = string.Concat((m!["parts"]?.AsArray() ?? []).Where(p => p?["part_kind"]?.GetValue<string>() == (isUser ? "user-prompt" : "text"))
                .Select(p => p!["content"]?.GetValue<string>()));
            if (text.Length > 0) list.Add(new { role = isUser ? "user" : "assistant", text });
        }
        return list;
    }
}
