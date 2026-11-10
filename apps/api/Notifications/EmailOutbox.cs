using System;
using System.Threading;
using System.Threading.Tasks;
using AiAgentStudio.Api.Tickets;

namespace AiAgentStudio.Api.Notifications;

/// <summary>email_outbox（spec 10.2、13）：只存收件人工號與事件資料；email 位址在寄送時才查。</summary>
public class EmailOutbox
{
    public const string Pending = "Pending", Sent = "Sent", Failed = "Failed";

    public long Id { get; set; }
    /// <summary>事件類型（見 <see cref="EmailEvents"/>）；之後的事件只要加類型與範本，不需改資料表。</summary>
    public string EventType { get; set; } = "";
    public string RecipientEmpNo { get; set; } = "";
    public string TicketId { get; set; } = "";
    /// <summary>範本需要的事件資料（JSON）。</summary>
    public string PayloadJson { get; set; } = "{}";
    public string Status { get; set; } = Pending;
    public int Attempts { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? SentAt { get; set; }

    /// <summary>加入一筆待寄（不呼叫 SaveChanges）：呼叫端與狀態變更一起存檔，才是同一個交易。之後的事件（轉派、留言…）都走這個入口。</summary>
    public static EmailOutbox Queue(StudioDb db, string eventType, string recipientEmpNo, string ticketId, object payload, DateTimeOffset now) =>
        db.EmailOutbox.Add(new EmailOutbox
        {
            EventType = eventType, RecipientEmpNo = recipientEmpNo, TicketId = ticketId, CreatedAt = now, NextAttemptAt = now,
            PayloadJson = System.Text.Json.JsonSerializer.Serialize(payload, new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }),
        }).Entity;
}

/// <summary>spec 10.1 的事件類型。</summary>
public static class EmailEvents
{
    public const string Submitted = "Submitted";     // 送出（首次）→ System Owner
    public const string Resubmitted = "Resubmitted"; // 重評 → System Owner
    public const string Replied = "Replied";         // 回覆評估結果 → CIM Engineer
}

public sealed record EmailMessage(string To, string Subject, string Html, string IdempotencyKey);

/// <summary>寄信失敗。Retryable 為 true 才會退避重試（逾時、連線失敗、5xx、429）；其他直接標為失敗。</summary>
public sealed class EmailSendException(string message, bool retryable, Exception? inner = null) : Exception(message, inner)
{
    public bool Retryable { get; } = retryable;
}

public interface IEmailSender
{
    /// <summary>成功回傳；失敗拋 <see cref="EmailSendException"/>。</summary>
    Task SendAsync(EmailMessage message, CancellationToken ct);
}

public sealed class EmailOptions
{
    /// <summary>LocalFolder（預設，開發與測試）或 InternalApi。</summary>
    public string Sender { get; set; } = "LocalFolder";
    public string LocalFolder { get; set; } = "outbox-mail";
    public string FromAddress { get; set; } = "ai-agent-studio@innolux.com";
    public string? FromName { get; set; } = "AI Agent Studio";
    /// <summary>Email（adId@網域）、AdId 或 EmpNo：內部 email API 接受哪一種收件人格式（假設，待確認）。</summary>
    public string RecipientFormat { get; set; } = "Email";
    public string EmailDomain { get; set; } = "innolux.com";
    /// <summary>Studio 對外網址，用來組直達連結。</summary>
    public string StudioUrl { get; set; } = "http://localhost:4200";
    public InternalApiOptions InternalApi { get; set; } = new();
    public int PollSeconds { get; set; } = 30;
    public int MaxAttempts { get; set; } = 8;
    public int RetentionDays { get; set; } = 90;
}

public sealed class InternalApiOptions
{
    public string Endpoint { get; set; } = "";
    /// <summary>認證方式（假設，待確認）：None、Bearer、ApiKey（放 AuthHeader 指定的標頭）。</summary>
    public string AuthType { get; set; } = "Bearer";
    public string AuthHeader { get; set; } = "X-Api-Key";
    public string AuthToken { get; set; } = "";
    public string IdempotencyHeader { get; set; } = "Idempotency-Key";
    public int TimeoutSeconds { get; set; } = 30;
}
