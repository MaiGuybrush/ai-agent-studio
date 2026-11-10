using System;
using System.Linq;
using System.Net;
using System.Text.Json;

namespace AiAgentStudio.Api.Notifications;

/// <summary>每個事件一封繁體中文 HTML（spec 10.2）。範本寫在程式碼中；只放事件、需求單號、Tool Spec 名稱與直達連結，不附 Tool Spec 內容。</summary>
public static class EmailTemplates
{
    /// <summary>未知的事件類型拋 <see cref="EmailSendException"/>（不可重試）。新增事件時在這裡加一個 case。</summary>
    public static (string Subject, string Html) Render(EmailOutbox row, string studioUrl)
    {
        var payload = JsonDocument.Parse(row.PayloadJson).RootElement;
        var specs = payload.TryGetProperty("specs", out var s) ? s.EnumerateArray().Select(x => x.GetString() ?? "").ToArray() : [];
        var link = $"{studioUrl.TrimEnd('/')}/tickets/{Uri.EscapeDataString(row.TicketId)}";
        return row.EventType switch
        {
            EmailEvents.Submitted => Spec("需求單送評", "需求單已送出，請您評估下列 Tool Spec", row.TicketId, specs, link),
            EmailEvents.Resubmitted => Spec("需求單重新送評", "需求單已重新送出，請您重新評估下列 Tool Spec", row.TicketId, specs, link),
            _ => throw new EmailSendException($"未知的事件類型 '{row.EventType}'。", false),
        };
    }

    static (string, string) Spec(string event_, string lead, string ticketId, string[] specs, string link)
    {
        static string E(string s) => WebUtility.HtmlEncode(s);
        var items = string.Concat(specs.Select(x => $"<li>{E(x)}</li>"));
        var html = $"<html><body><p>事件：{E(event_)}</p><p>需求單號：{E(ticketId)}</p><p>{E(lead)}：</p><ul>{items}</ul>" +
                   $"<p><a href=\"{E(link)}\">前往 AI Agent Studio</a></p></body></html>";
        return ($"[AI Agent Studio] {event_}：{ticketId}", html);
    }
}
