using System.Net;
using AiAgentStudio.Api.Vocabulary;

namespace AiAgentStudio.Api.Tests;

/// <summary>#22：Gitea API 的請求形狀（用假的 HttpMessageHandler）。真實的 Gitea 無法在此連線，未對真實伺服器驗證。</summary>
public class GiteaVocabularySourceTests
{
    class Stub(Func<HttpRequestMessage, HttpResponseMessage> f) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Seen = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) { Seen.Add(r); return Task.FromResult(f(r)); }
    }

    static GiteaVocabularySource Make(Stub h) => new(new HttpClient(h) { BaseAddress = new("https://gitea.example/") },
        new GiteaOptions { Owner = "cim", Repo = "McpGateway.Core", Token = "tok" });

    [Fact]
    public async Task Reads_main_head_sha_then_raw_file_at_that_sha_with_token()
    {
        var h = new Stub(r => r.RequestUri!.AbsolutePath.EndsWith("/branches/main")
            ? new(HttpStatusCode.OK) { Content = new StringContent("""{"name":"main","commit":{"id":"abc123"}}""") }
            : new(HttpStatusCode.OK) { Content = new StringContent("// 原文") });
        var s = Make(h);

        Assert.Equal("abc123", await s.GetHeadShaAsync(default));
        Assert.Equal("// 原文", await s.GetFileAsync("abc123", default));

        Assert.Equal("/api/v1/repos/cim/McpGateway.Core/branches/main", h.Seen[0].RequestUri!.AbsolutePath);
        Assert.Equal("/api/v1/repos/cim/McpGateway.Core/raw/src/McpGateway.Analyzers/Tier1Vocabulary.cs", h.Seen[1].RequestUri!.AbsolutePath);
        Assert.Equal("?ref=abc123", h.Seen[1].RequestUri!.Query);
        Assert.Equal("token tok", h.Seen[0].Headers.Authorization!.ToString());
    }

    [Fact]
    public async Task Http_error_surfaces_as_exception() =>
        await Assert.ThrowsAsync<HttpRequestException>(() => Make(new Stub(_ => new(HttpStatusCode.Unauthorized))).GetHeadShaAsync(default));
}
