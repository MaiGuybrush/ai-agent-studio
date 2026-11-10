using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AiAgentStudio.Api.Tickets;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AiAgentStudio.Api.Notifications;

/// <summary>寄出 email_outbox 到期的資料（spec 10.2）。業務規則（退避、重試判斷）都在這裡，sender 只負責「寄」與分類成功／可重試／不可重試。</summary>
public class EmailOutboxDispatcher(StudioDb db, IEmailSender sender, IUserDirectory directory, IOptions<EmailOptions> options, TimeProvider time, ILogger<EmailOutboxDispatcher> log)
{
    /// <summary>第 n 次失敗後的等待分鐘數（1、5、15、60、60…）；用完 MaxAttempts 就標為失敗。</summary>
    static readonly int[] BackoffMinutes = [1, 5, 15, 60];
    const int Batch = 50;

    /// <summary>處理所有到期的 Pending 資料，回傳處理（嘗試寄送）的筆數。
    /// ponytail: 假設只有一個 API 實例在跑；多實例要改成 SELECT … FOR UPDATE SKIP LOCKED。</summary>
    public async Task<int> DispatchDueAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var due = (await db.EmailOutbox.Where(x => x.Status == EmailOutbox.Pending && x.NextAttemptAt <= now).ToListAsync(ct))
            .OrderBy(x => x.NextAttemptAt).ThenBy(x => x.Id).Take(Batch).ToList();
        foreach (var row in due) await AttemptAsync(row, ct);
        return due.Count;
    }

    async Task AttemptAsync(EmailOutbox row, CancellationToken ct)
    {
        var o = options.Value;
        row.Attempts++; // 呼叫前先記嘗試次數；之後當掉最多是重複寄，不會漏記
        await db.SaveChangesAsync(ct);
        try
        {
            var (subject, html) = EmailTemplates.Render(row, o.StudioUrl);
            var to = await ResolveAddressAsync(row.RecipientEmpNo, o, ct);
            await sender.SendAsync(new EmailMessage(to, subject, html, $"email-outbox-{row.Id}"), ct);
            row.Status = EmailOutbox.Sent; row.SentAt = time.GetUtcNow(); row.LastError = null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e)
        {
            var retry = e is not EmailSendException { Retryable: false };
            row.LastError = e.Message;
            if (retry && row.Attempts < o.MaxAttempts)
                row.NextAttemptAt = time.GetUtcNow().AddMinutes(BackoffMinutes[Math.Min(row.Attempts, BackoffMinutes.Length) - 1]);
            else row.Status = EmailOutbox.Failed;
            log.LogWarning(e, "email_outbox {Id} 寄送失敗（第 {Attempts} 次，{Result}）", row.Id, row.Attempts, row.Status == EmailOutbox.Failed ? "放棄" : "稍後重試");
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>以工號組出收件地址；查無此人或沒有 adId 是不可重試的失敗，查詢本身出錯（UAC 暫時不通）則照一般例外重試。</summary>
    async Task<string> ResolveAddressAsync(string empNo, EmailOptions o, CancellationToken ct)
    {
        if (o.RecipientFormat.Equals("EmpNo", StringComparison.OrdinalIgnoreCase)) return empNo;
        var user = await directory.FindAsync(empNo, ct) ?? throw new EmailSendException($"查無工號 {empNo}。", false);
        if (string.IsNullOrWhiteSpace(user.AdId)) throw new EmailSendException($"工號 {empNo} 的 basic-info 沒有 adId。", false);
        return o.RecipientFormat.Equals("AdId", StringComparison.OrdinalIgnoreCase) ? user.AdId : $"{user.AdId}@{o.EmailDomain}";
    }

    /// <summary>刪除已寄出超過保存期限（預設 90 天）的資料。</summary>
    public async Task<int> PurgeAsync(CancellationToken ct)
    {
        var cutoff = time.GetUtcNow().AddDays(-options.Value.RetentionDays);
        var old = await db.EmailOutbox.Where(x => x.Status == EmailOutbox.Sent && x.SentAt < cutoff).ToListAsync(ct);
        db.EmailOutbox.RemoveRange(old);
        await db.SaveChangesAsync(ct);
        return old.Count;
    }
}
