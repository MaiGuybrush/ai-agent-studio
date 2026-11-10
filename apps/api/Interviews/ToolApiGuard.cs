using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AiAgentStudio.Api.Tickets;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AiAgentStudio.Api.Interviews;

/// <summary>tool API 的寫入守門：run token 還沒過期，但單已取消或已送出（不在訪談中）就不能再寫草稿；讀取不受影響。</summary>
public static class ToolApiGuard
{
    /// <summary>可寫回 null；不可寫回 409。</summary>
    public static async Task<IActionResult?> RejectWriteAsync(StudioDb db, string ticketId, CancellationToken ct)
    {
        var status = await db.Tickets.AsNoTracking().Where(t => t.Id == ticketId).Select(t => t.Status).FirstOrDefaultAsync(ct);
        if (status is null or TicketStatus.Interviewing) return null; // 找不到單由後續流程處理
        var why = status == TicketStatus.Cancelled ? "需求單已取消" : "需求單已送出、不在訪談中";
        return new ConflictObjectResult(new { message = $"{why}，不能再寫入草稿。" });
    }
}
