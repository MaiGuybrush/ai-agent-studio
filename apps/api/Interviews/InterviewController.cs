using Microsoft.AspNetCore.Http;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiAgentStudio.Api.Submissions;
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
public class InterviewController(ICurrentUserService user, IdentityService identity, StudioDb db, InterviewRunner runner, TimeProvider time, AiAgentStudio.Api.Workflow.WorkflowDraftService drafts) : ControllerBase
{
    public record ReopenRequest(string? Reason);

    /// <summary>
    /// 重開 Interview（spec 5.3-3、8.6）：負責的 CIM Engineer、須附原因；任何已送出的狀態（含已完成）都可以。
    /// 開一筆新的 interview（新對話，開場脈絡 = 上次送出的快照＋原因），舊的保留；需求單的儲存標記回到訪談中，顯示狀態重新推導。
    /// </summary>
    [HttpPost("reopen")]
    public async Task<IActionResult> Reopen(string ticketId, ReopenRequest req, CancellationToken ct)
    {
        if (await WhoAsync(ct) is not { IsCimEngineer: true } me) return Forbid();
        var t = await db.Tickets.FirstOrDefaultAsync(x => x.Id == ticketId, ct);
        if (t is null) return NotFound();
        if (t.CimEngineerEmpNo != me.EmpNo) return Forbid();
        if (string.IsNullOrWhiteSpace(req.Reason)) return Problem("重開須附原因。", statusCode: 400);
        if (t.Status == TicketStatus.Cancelled) return Problem("需求單已取消，請先重開取消。", statusCode: 409);
        if (t.Status == TicketStatus.Interviewing) return Problem("需求單仍在訪談中，無須重開。", statusCode: 409);
        var reason = req.Reason.Trim();

        var sub = await db.Submissions.AsNoTracking().Where(x => x.TicketId == ticketId).OrderByDescending(x => x.Number).FirstAsync(ct);
        var recs = await db.ToolSpecRecords.AsNoTracking().Where(r => r.TicketId == ticketId).ToListAsync(ct);
        var revs = await db.ToolSpecRevisions.AsNoTracking().Where(r => r.TicketId == ticketId).ToListAsync(ct);
        var specs = new JsonArray();
        var draftSpecs = (await drafts.GetAsync(ticketId, AiAgentStudio.Api.Workflow.WorkflowDraft.ToolSpecsDoc)).Steps.OfType<JsonObject>().ToList();
        foreach (var r in recs.OrderBy(r => r.SpecId))
        {
            var content = (JsonObject)JsonNode.Parse(revs.First(v => v.SpecId == r.SpecId && v.Revision == r.CurrentRevision).ContentJson)!;
            specs.Add(new JsonObject { ["id"] = r.SpecId, ["revision"] = r.CurrentRevision, ["state"] = r.State, ["content"] = content.DeepClone() });
            // 草稿以最新 revision 為準（含 #28 送出後的修改）；只在內容真的不同時才同步，避免無謂遞增版本
            static JsonObject Norm(JsonObject o) { var c = (JsonObject)o.DeepClone(); c.Remove("id"); c.Remove("naming_check"); c.Remove("system_owner_name"); return c; }
            if (draftSpecs.FirstOrDefault(d => d["id"]?.GetValue<string>() == r.SpecId) is not { } cur || !JsonNode.DeepEquals(Norm(cur), Norm(content)))
                await drafts.SyncSpecAsync(ticketId, r.SpecId, content);
        }
        var context = new JsonObject
        {
            ["reopen_reason"] = reason, ["submission_number"] = sub.Number,
            ["workflow"] = JsonNode.Parse(sub.WorkflowJson), ["tool_specs"] = specs,
        };
        db.Interviews.Add(new Interview { TicketId = ticketId, ReopenReason = reason, OpeningContext = context.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }), CreatedAt = time.GetUtcNow() });
        db.TicketEvents.Add(new TicketEvent { TicketId = ticketId, Type = "ReopenInterview", ActorEmpNo = me.EmpNo, At = time.GetUtcNow(), FromValue = t.Status, ToValue = TicketStatus.Interviewing, Reason = reason });
        t.Status = TicketStatus.Interviewing;
        await db.SaveChangesAsync(ct);
        await drafts.ClearHumanEditsAsync(ticketId); // 人工修改標記在新一次 Interview 重新開始
        return Ok(new { status = TicketStatus.Label(TicketStatus.Interviewing) });
    }

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
        var previous = await db.Interviews.AsNoTracking().Where(x => x.TicketId == ticketId && (iv == null || x.Id != iv.Id)).OrderByDescending(x => x.Id)
            .Select(x => new { x.Id, x.ReopenReason, x.CreatedAt }).ToListAsync(ct);
        return Ok(new
        {
            running = runner.Find(ticketId) != null, messages = iv is null ? [] : Display(iv.ConversationJson),
            interviewId = iv?.Id, reopenReason = iv?.ReopenReason, openingContext = iv?.ReopenReason is null ? null : iv.OpeningContext, previous, // 舊對話連結（spec 8.6）
        });
    }

    /// <summary>讀指定的（舊）對話：任何 CIM Engineer。</summary>
    [HttpGet("{interviewId:long}")]
    public async Task<IActionResult> GetOne(string ticketId, long interviewId, CancellationToken ct)
    {
        if (await WhoAsync(ct) is not { IsCimEngineer: true }) return Forbid();
        var iv = await db.Interviews.AsNoTracking().FirstOrDefaultAsync(x => x.TicketId == ticketId && x.Id == interviewId, ct);
        return iv is null ? NotFound() : Ok(new { interviewId = iv.Id, reopenReason = iv.ReopenReason, createdAt = iv.CreatedAt, messages = Display(iv.ConversationJson) });
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
        if (t.Status != TicketStatus.Interviewing) return Problem("Interview 已送出，無法再對話。", statusCode: 409);
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
