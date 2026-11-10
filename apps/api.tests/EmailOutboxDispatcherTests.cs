using AiAgentStudio.Api.Notifications;
using AiAgentStudio.Api.Tickets;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace AiAgentStudio.Api.Tests;

/// <summary>#25：outbox 寄送、退避重試、清除（spec 10.2）。用假時鐘與假 sender，不碰任何外部服務。</summary>
public class EmailOutboxDispatcherTests
{
    readonly FakeTimeProvider time = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    readonly DbContextOptions<StudioDb> dbOptions = new DbContextOptionsBuilder<StudioDb>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
    readonly FakeSender sender = new();
    readonly FakeUsers users = new();
    readonly EmailOptions options = new();

    class FakeSender : IEmailSender
    {
        public readonly List<EmailMessage> Sent = [];
        public Func<EmailMessage, Exception?> Fail = _ => null;
        public Action? OnCall;
        public Task SendAsync(EmailMessage m, CancellationToken ct)
        {
            OnCall?.Invoke();
            if (Fail(m) is { } e) throw e;
            Sent.Add(m);
            return Task.CompletedTask;
        }
    }

    class FakeUsers : IUserDirectory
    {
        public Dictionary<string, DirectoryUser> Map = new() { ["10000001"] = new("10000001", "王大明", "dm.wang") };
        public Exception? Throw;
        public Task<DirectoryUser?> FindAsync(string empNo, CancellationToken ct) =>
            Throw is not null ? throw Throw : Task.FromResult(Map.GetValueOrDefault(empNo));
    }

    StudioDb NewDb() => new(dbOptions);
    EmailOutboxDispatcher Dispatcher(StudioDb db) =>
        new(db, sender, users, Options.Create(options), time, NullLogger<EmailOutboxDispatcher>.Instance);

    long Add(string emp = "10000001", string type = EmailEvents.Submitted)
    {
        using var db = NewDb();
        var row = new EmailOutbox
        {
            EventType = type, RecipientEmpNo = emp, TicketId = "T-1", PayloadJson = """{"specs":["ts1"],"number":1}""",
            NextAttemptAt = time.GetUtcNow(), CreatedAt = time.GetUtcNow(),
        };
        db.EmailOutbox.Add(row);
        db.SaveChanges();
        return row.Id;
    }

    EmailOutbox Row(long id) { using var db = NewDb(); return db.EmailOutbox.AsNoTracking().Single(x => x.Id == id); }
    async Task<int> Run() { using var db = NewDb(); return await Dispatcher(db).DispatchDueAsync(default); }

    static EmailSendException Transient() => new("503", true);

    [Fact]
    public async Task Due_row_is_sent_to_address_built_from_basic_info_adId_and_marked_sent()
    {
        var id = Add();
        Assert.Equal(1, await Run());

        var m = Assert.Single(sender.Sent);
        Assert.Equal("dm.wang@innolux.com", m.To);
        Assert.Contains("T-1", m.Html);
        var row = Row(id);
        Assert.Equal((EmailOutbox.Sent, 1), (row.Status, row.Attempts));
        Assert.Equal(time.GetUtcNow(), row.SentAt);
        await Run();
        Assert.Single(sender.Sent); // 已寄出不再寄
    }

    [Fact]
    public async Task Email_domain_comes_from_configuration()
    {
        options.EmailDomain = "example.test";
        Add(); await Run();
        Assert.Equal("dm.wang@example.test", sender.Sent[0].To);
    }

    [Fact]
    public async Task Recipient_format_EmpNo_skips_directory_lookup()
    {
        options.RecipientFormat = "EmpNo"; users.Throw = new HttpRequestException("down");
        Add(); await Run();
        Assert.Equal("10000001", sender.Sent[0].To);
    }

    [Fact]
    public async Task Idempotency_key_is_stable_per_outbox_row()
    {
        var id = Add();
        sender.Fail = _ => Transient();
        await Run();
        time.Advance(TimeSpan.FromMinutes(1));
        await Run();
        sender.Fail = _ => null; time.Advance(TimeSpan.FromMinutes(5)); await Run();
        Assert.Equal($"email-outbox-{id}", sender.Sent[0].IdempotencyKey);
    }

    [Fact]
    public async Task Transient_failures_back_off_1_5_15_60_minutes_then_fail_after_8_attempts()
    {
        var id = Add();
        sender.Fail = _ => Transient();
        var waits = new List<double>();
        for (var i = 0; i < 8; i++)
        {
            Assert.Equal(1, await Run());
            var row = Row(id);
            Assert.Equal(i + 1, row.Attempts);
            if (row.Status == EmailOutbox.Failed) break;
            var wait = row.NextAttemptAt - time.GetUtcNow();
            waits.Add(wait.TotalMinutes);
            time.Advance(wait - TimeSpan.FromSeconds(1));
            Assert.Equal(0, await Run()); // 還沒到時間
            time.Advance(TimeSpan.FromSeconds(1));
        }
        Assert.Equal([1, 5, 15, 60, 60, 60, 60], waits);
        var last = Row(id);
        Assert.Equal((EmailOutbox.Failed, 8), (last.Status, last.Attempts));
        Assert.Contains("503", last.LastError);
        Assert.Equal(0, await Run());
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task Row_recovers_when_a_retry_succeeds()
    {
        var id = Add();
        sender.Fail = _ => Transient();
        await Run();
        sender.Fail = _ => null;
        time.Advance(TimeSpan.FromMinutes(1));
        await Run();
        var row = Row(id);
        Assert.Equal((EmailOutbox.Sent, 2), (row.Status, row.Attempts));
    }

    [Fact]
    public async Task Permanent_failure_fails_immediately_with_error_recorded()
    {
        var id = Add();
        sender.Fail = _ => new EmailSendException("400 bad recipient", false);
        await Run();
        var row = Row(id);
        Assert.Equal((EmailOutbox.Failed, 1), (row.Status, row.Attempts));
        Assert.Equal("400 bad recipient", row.LastError);
    }

    [Fact]
    public async Task Unknown_employee_fails_without_calling_sender()
    {
        var id = Add("99999999");
        await Run();
        Assert.Empty(sender.Sent);
        var row = Row(id);
        Assert.Equal(EmailOutbox.Failed, row.Status);
        Assert.Contains("99999999", row.LastError);
    }

    [Fact]
    public async Task Employee_without_adId_fails_when_address_needs_it()
    {
        users.Map["10000002"] = new("10000002", "無帳號");
        var id = Add("10000002");
        await Run();
        Assert.Equal(EmailOutbox.Failed, Row(id).Status);
    }

    [Fact]
    public async Task Directory_outage_is_retried()
    {
        var id = Add();
        users.Throw = new HttpRequestException("uac down");
        await Run();
        var row = Row(id);
        Assert.Equal((EmailOutbox.Pending, 1), (row.Status, row.Attempts));
        Assert.Contains("uac down", row.LastError);
    }

    [Fact]
    public async Task Unexpected_sender_exception_is_retried_and_one_bad_row_does_not_block_others()
    {
        var bad = Add(); var good = Add();
        sender.Fail = m => m.IdempotencyKey.EndsWith($"-{bad}") ? new InvalidOperationException("boom") : null;
        Assert.Equal(2, await Run());
        Assert.Equal(EmailOutbox.Pending, Row(bad).Status);
        Assert.Equal(EmailOutbox.Sent, Row(good).Status);
    }

    [Fact]
    public async Task Attempt_is_recorded_before_the_sender_is_called()
    {
        var id = Add();
        int seen = -1;
        sender.OnCall = () => seen = Row(id).Attempts;
        await Run();
        Assert.Equal(1, seen);
    }

    [Fact]
    public async Task Unknown_event_type_fails_permanently()
    {
        var id = Add(type: "Nope");
        await Run();
        Assert.Equal(EmailOutbox.Failed, Row(id).Status);
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task Purge_removes_sent_rows_older_than_90_days_only()
    {
        var oldSent = Add(); var freshSent = Add(); var oldFailed = Add(); var oldPending = Add();
        using (var db = NewDb())
        {
            var now = time.GetUtcNow();
            void Set(long id, string st, int days) { var r = db.EmailOutbox.Single(x => x.Id == id); r.Status = st; r.SentAt = now.AddDays(-days); }
            Set(oldSent, EmailOutbox.Sent, 91); Set(freshSent, EmailOutbox.Sent, 89);
            db.EmailOutbox.Single(x => x.Id == oldFailed).Status = EmailOutbox.Failed;
            db.SaveChanges();
        }
        using (var db = NewDb()) Assert.Equal(1, await Dispatcher(db).PurgeAsync(default));
        using var chk = NewDb();
        Assert.Equal(new[] { freshSent, oldFailed, oldPending }.Order().ToList(), chk.EmailOutbox.Select(x => x.Id).ToList().Order().ToList());
    }
}
