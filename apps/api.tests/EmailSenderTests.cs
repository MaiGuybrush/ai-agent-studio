using System.Net;
using System.Text.Json;
using AiAgentStudio.Api.Notifications;
using Microsoft.Extensions.Options;

namespace AiAgentStudio.Api.Tests;

/// <summary>#25：兩種 IEmailSender 與範本。InternalApi 只對假的 HttpMessageHandler 測試（真實 API 規格待確認）。</summary>
public class EmailSenderTests
{
    static readonly EmailMessage Msg = new("a.b@innolux.com", "主旨", "<p>內文</p>", "email-outbox-7");

    class Handler(Func<HttpRequestMessage, HttpResponseMessage> f) : HttpMessageHandler
    {
        public HttpRequestMessage? Request; public string? Body;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Request = r; Body = r.Content is null ? null : await r.Content.ReadAsStringAsync(ct);
            return f(r);
        }
    }

    static (InternalApiEmailSender, Handler) Api(Func<HttpRequestMessage, HttpResponseMessage> f, Action<EmailOptions>? cfg = null)
    {
        var o = new EmailOptions { InternalApi = { Endpoint = "http://mail.test/send", AuthToken = "tok" } };
        cfg?.Invoke(o);
        var h = new Handler(f);
        return (new InternalApiEmailSender(new HttpClient(h), Options.Create(o)), h);
    }

    static HttpResponseMessage Status(HttpStatusCode c) => new(c);

    [Fact]
    public async Task Api_posts_json_with_bearer_and_idempotency_key()
    {
        var (s, h) = Api(_ => Status(HttpStatusCode.OK));
        await s.SendAsync(Msg, default);

        Assert.Equal("http://mail.test/send", h.Request!.RequestUri!.ToString());
        Assert.Equal("Bearer tok", h.Request.Headers.Authorization!.ToString());
        Assert.Equal("email-outbox-7", h.Request.Headers.GetValues("Idempotency-Key").Single());
        var j = JsonDocument.Parse(h.Body!).RootElement;
        Assert.Equal("ai-agent-studio@innolux.com", j.GetProperty("from").GetString());
        Assert.Equal("a.b@innolux.com", j.GetProperty("to")[0].GetString());
        Assert.Equal("主旨", j.GetProperty("subject").GetString());
        Assert.Equal("<p>內文</p>", j.GetProperty("html").GetString());
    }

    [Fact]
    public async Task Api_key_auth_uses_configured_header()
    {
        var (s, h) = Api(_ => Status(HttpStatusCode.OK), o => { o.InternalApi.AuthType = "ApiKey"; o.InternalApi.AuthHeader = "X-Key"; });
        await s.SendAsync(Msg, default);
        Assert.Equal("tok", h.Request!.Headers.GetValues("X-Key").Single());
        Assert.Null(h.Request.Headers.Authorization);
    }

    [Theory]
    [InlineData(500)] [InlineData(502)] [InlineData(503)] [InlineData(429)] [InlineData(408)]
    public async Task Api_retryable_statuses(int code)
    {
        var (s, _) = Api(_ => Status((HttpStatusCode)code));
        var e = await Assert.ThrowsAsync<EmailSendException>(() => s.SendAsync(Msg, default));
        Assert.True(e.Retryable);
        Assert.Contains(code.ToString(), e.Message);
    }

    [Theory]
    [InlineData(400)] [InlineData(401)] [InlineData(403)] [InlineData(404)] [InlineData(422)]
    public async Task Api_other_4xx_is_permanent(int code)
    {
        var (s, _) = Api(_ => Status((HttpStatusCode)code));
        var e = await Assert.ThrowsAsync<EmailSendException>(() => s.SendAsync(Msg, default));
        Assert.False(e.Retryable);
    }

    [Fact]
    public async Task Api_connection_failure_and_timeout_are_retryable()
    {
        var (s1, _) = Api(_ => throw new HttpRequestException("refused"));
        Assert.True((await Assert.ThrowsAsync<EmailSendException>(() => s1.SendAsync(Msg, default))).Retryable);
        var (s2, _) = Api(_ => throw new TaskCanceledException("timeout"));
        Assert.True((await Assert.ThrowsAsync<EmailSendException>(() => s2.SendAsync(Msg, default))).Retryable);
    }

    [Fact]
    public async Task Api_without_endpoint_fails_permanently_without_any_request()
    {
        var (s, h) = Api(_ => Status(HttpStatusCode.OK), o => o.InternalApi.Endpoint = "");
        var e = await Assert.ThrowsAsync<EmailSendException>(() => s.SendAsync(Msg, default));
        Assert.False(e.Retryable);
        Assert.Null(h.Request);
    }

    [Fact]
    public async Task Local_folder_writes_one_html_file_with_recipient_and_subject()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"mail-{Guid.NewGuid():N}");
        try
        {
            await new LocalFolderEmailSender(Options.Create(new EmailOptions { LocalFolder = dir })).SendAsync(Msg, default);
            var file = Assert.Single(Directory.GetFiles(dir, "*.html"));
            var text = File.ReadAllText(file);
            Assert.Contains("a.b@innolux.com", text);
            Assert.Contains("主旨", text);
            Assert.Contains("<p>內文</p>", text);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    static EmailOutbox Row(string type = EmailEvents.Submitted) => new() { EventType = type, TicketId = "T-9", PayloadJson = """{"specs":["查工單<b>","ts2"]}""" };

    [Fact]
    public void Template_has_event_ticket_spec_names_escaped_and_direct_link_in_traditional_chinese()
    {
        var (subject, html) = EmailTemplates.Render(Row(), "https://studio.test/");
        Assert.Contains("T-9", subject);
        Assert.Contains("需求單送評", html);
        Assert.Contains("T-9", html);
        Assert.Contains("查工單&lt;b&gt;", html);
        Assert.Contains("ts2", html);
        Assert.Contains("href=\"https://studio.test/tickets/T-9\"", html);
    }

    [Fact]
    public void Resubmit_template_says_reevaluation()
    {
        Assert.Contains("重新送評", EmailTemplates.Render(Row(EmailEvents.Resubmitted), "https://studio.test").Html);
    }
}
