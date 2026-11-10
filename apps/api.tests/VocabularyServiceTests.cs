using AiAgentStudio.Api.Vocabulary;
using Microsoft.Extensions.Time.Testing;

namespace AiAgentStudio.Api.Tests;

/// <summary>#22：詞彙快取（spec 7.1）。Gitea 在這裡是假的（FakeSource）。</summary>
public class VocabularyServiceTests
{
    public class FakeSource : IVocabularySource
    {
        public string Sha = "sha1";
        public string Text = Src("a");
        public Exception? Fail;
        public int HeadCalls, FileCalls;
        public Task<string> GetHeadShaAsync(CancellationToken ct) { HeadCalls++; return Fail is null ? Task.FromResult(Sha) : Task.FromException<string>(Fail); }
        public Task<string> GetFileAsync(string sha, CancellationToken ct) { FileCalls++; return Fail is null ? Task.FromResult(Text) : Task.FromException<string>(Fail); }
    }

    public class FakeAlert : IVocabularyAlert
    {
        public List<string> Messages = [];
        public Task NotifyAsync(string message, CancellationToken ct) { Messages.Add(message); return Task.CompletedTask; }
    }

    public static string Src(params string[] camels) =>
        "class T { static object[] E = new[] { " + string.Join(",", camels.Select(c => $"new GlossaryEntry(\"{c}\", \"{char.ToUpper(c[0])}{c[1..]}\", \"d\", new string[0])")) + " }; }";

    readonly FakeSource src = new();
    readonly FakeAlert alert = new();
    readonly FakeTimeProvider time = new(new DateTimeOffset(2026, 11, 10, 8, 0, 0, TimeSpan.Zero));
    VocabularyService Svc() => new(src, time, alert);

    [Fact]
    public async Task Cache_is_reused_within_ttl_and_only_sha_is_compared_after_expiry()
    {
        var s = Svc();
        var first = await s.GetAsync();
        Assert.Equal(("sha1", null), (first.Sha, first.Warning));
        Assert.Equal("a", first.Entries.Single().StandardCamel);
        Assert.Equal((1, 1), (src.HeadCalls, src.FileCalls));

        time.Advance(TimeSpan.FromMinutes(4));
        await s.GetAsync();
        Assert.Equal((1, 1), (src.HeadCalls, src.FileCalls)); // TTL 內完全不碰 Gitea

        time.Advance(TimeSpan.FromMinutes(2));
        await s.GetAsync();
        Assert.Equal((2, 1), (src.HeadCalls, src.FileCalls)); // 過期只比對 sha，沒變就不抓原文

        time.Advance(TimeSpan.FromMinutes(1));
        await s.GetAsync();
        Assert.Equal(2, src.HeadCalls); // 比對過後 TTL 重新起算
    }

    [Fact]
    public async Task New_sha_refetches_and_replaces_entries()
    {
        var s = Svc();
        await s.GetAsync();
        (src.Sha, src.Text) = ("sha2", Src("b"));
        time.Advance(TimeSpan.FromMinutes(6));
        var snap = await s.GetAsync();
        Assert.Equal(("sha2", "b"), (snap.Sha, snap.Entries.Single().StandardCamel));
        Assert.Equal(time.GetUtcNow(), snap.FetchedAt);
    }

    [Theory]
    [InlineData("fetch")]
    [InlineData("parse")]
    public async Task Failure_keeps_last_good_version_with_warning_and_notifies_cim_engineer(string kind)
    {
        var s = Svc();
        var good = await s.GetAsync();
        time.Advance(TimeSpan.FromMinutes(6));
        if (kind == "fetch") src.Fail = new HttpRequestException("gitea down");
        else (src.Sha, src.Text) = ("sha2", Src("a", "A")); // camel 重複，驗證失敗

        var snap = await s.GetAsync();
        Assert.Equal(("sha1", good.FetchedAt), (snap.Sha, snap.FetchedAt)); // 沿用上一份，sha 與時間照舊
        Assert.Equal("a", snap.Entries.Single().StandardCamel);
        Assert.Contains("sha1", snap.Warning);
        Assert.Single(alert.Messages);

        time.Advance(TimeSpan.FromMinutes(1));
        await s.GetAsync();
        Assert.Single(alert.Messages); // TTL 內不重複打 Gitea、也不重複通知

        src.Fail = null; (src.Sha, src.Text) = ("sha3", Src("c"));
        time.Advance(TimeSpan.FromMinutes(6));
        var ok = await s.GetAsync();
        Assert.Equal(("sha3", null), (ok.Sha, ok.Warning)); // 恢復後警告消失
    }

    [Fact]
    public async Task No_cache_and_fetch_fails_blocks_naming_check()
    {
        src.Fail = new HttpRequestException("gitea down");
        await Assert.ThrowsAsync<VocabularyException>(() => Svc().GetAsync());
    }
}
