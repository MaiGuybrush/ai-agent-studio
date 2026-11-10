using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace AiAgentStudio.Api.Vocabulary;

/// <summary>設定區段 Gitea:Vocabulary。BaseUrl 是 Gitea 根網址；Token 是唯讀 API token。</summary>
public sealed class GiteaOptions
{
    public string BaseUrl { get; set; } = "";
    public string Owner { get; set; } = "";
    public string Repo { get; set; } = "McpGateway.Core";
    public string Branch { get; set; } = "main";
    public string Path { get; set; } = "src/McpGateway.Analyzers/Tier1Vocabulary.cs";
    public string Token { get; set; } = "";
}

/// <summary>用 Gitea API v1 讀 main 的 commit sha 與該 sha 的檔案原文。</summary>
public sealed class GiteaVocabularySource(HttpClient http, GiteaOptions o) : IVocabularySource
{
    string Repo => $"api/v1/repos/{o.Owner}/{o.Repo}";

    public async Task<string> GetHeadShaAsync(CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await GetAsync($"{Repo}/branches/{o.Branch}", ct));
        return doc.RootElement.GetProperty("commit").GetProperty("id").GetString() ?? throw new VocabularyException("Gitea 回應缺少 commit id。");
    }

    public Task<string> GetFileAsync(string sha, CancellationToken ct) => GetAsync($"{Repo}/raw/{o.Path}?ref={sha}", ct);

    async Task<string> GetAsync(string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (o.Token.Length > 0) req.Headers.Authorization = new AuthenticationHeaderValue("token", o.Token);
        using var res = await http.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadAsStringAsync(ct);
    }
}

/// <summary>通知 CIM Engineer 的管道目前只有 log（尚無 email／推播機制）；寫成 Error 讓監控抓得到。</summary>
public sealed class LogVocabularyAlert(ILogger<LogVocabularyAlert> log) : IVocabularyAlert
{
    public Task NotifyAsync(string message, CancellationToken ct) { log.LogError("Tier1Vocabulary 同步失敗（需通知 CIM Engineer）：{Message}", message); return Task.CompletedTask; }
}
