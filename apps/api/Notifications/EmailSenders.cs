using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;

namespace AiAgentStudio.Api.Notifications;

/// <summary>開發／測試用：把每封信寫成本機資料夾裡的一個 .html 檔，不連任何外部服務。</summary>
public sealed class LocalFolderEmailSender(IOptions<EmailOptions> options) : IEmailSender
{
    public async Task SendAsync(EmailMessage m, CancellationToken ct)
    {
        var dir = options.Value.LocalFolder;
        Directory.CreateDirectory(dir);
        var safeKey = string.Concat(m.IdempotencyKey.Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '_'));
        var file = Path.Combine(dir, $"{DateTime.UtcNow:yyyyMMddHHmmssfff}-{safeKey}.html");
        await File.WriteAllTextAsync(file, $"<!-- To: {m.To}\n     Subject: {m.Subject} -->\n{m.Html}", ct);
    }
}

/// <summary>
/// 公司內部 email API（spec 14）。<b>以下全是假設，待確認</b>，所以 endpoint、認證、標頭名稱都放設定：
/// POST {Endpoint}，JSON { from, fromName, to:[位址], subject, html, idempotencyKey }，認證 Bearer 或自訂標頭，另帶冪等鍵標頭；2xx 視為成功。
/// 重試判斷（逾時、連線失敗、5xx、429 重試；其他 4xx 直接失敗）是 spec 10.2 的規則，與 API 細節無關。
/// </summary>
public sealed class InternalApiEmailSender(HttpClient http, IOptions<EmailOptions> options) : IEmailSender
{
    public async Task SendAsync(EmailMessage m, CancellationToken ct)
    {
        var o = options.Value;
        var api = o.InternalApi;
        if (string.IsNullOrWhiteSpace(api.Endpoint)) throw new EmailSendException("Email:InternalApi:Endpoint 未設定。", false);

        using var req = new HttpRequestMessage(HttpMethod.Post, api.Endpoint)
        {
            Content = JsonContent.Create(new { from = o.FromAddress, fromName = o.FromName, to = new[] { m.To }, subject = m.Subject, html = m.Html, idempotencyKey = m.IdempotencyKey }),
        };
        if (api.AuthType.Equals("Bearer", StringComparison.OrdinalIgnoreCase)) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", api.AuthToken);
        else if (api.AuthType.Equals("ApiKey", StringComparison.OrdinalIgnoreCase)) req.Headers.TryAddWithoutValidation(api.AuthHeader, api.AuthToken);
        if (!string.IsNullOrEmpty(api.IdempotencyHeader)) req.Headers.TryAddWithoutValidation(api.IdempotencyHeader, m.IdempotencyKey);

        HttpResponseMessage res;
        try { res = await http.SendAsync(req, ct); }
        catch (Exception e) when ((e is HttpRequestException or TaskCanceledException) && !ct.IsCancellationRequested)
        {
            throw new EmailSendException($"email API 連線失敗或逾時：{e.Message}", true, e); // 連線失敗／逾時
        }
        using (res)
        {
            if (res.IsSuccessStatusCode) return;
            var code = (int)res.StatusCode;
            var retry = code >= 500 || res.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout;
            throw new EmailSendException($"email API 回應 {code}", retry);
        }
    }
}
