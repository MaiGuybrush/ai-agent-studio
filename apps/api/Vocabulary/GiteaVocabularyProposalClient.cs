using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AiAgentStudio.Api.Vocabulary;

/// <summary>
/// 用服務帳號 token（Gitea:Vocabulary:ProposalToken，需要 issue 寫入權限）在 McpGateway.Core 開 issue。
/// 尚未對真實 Gitea 驗證（內網不可達）：label 以名稱查 id（Gitea 建 issue 要 label id）。
/// </summary>
public sealed class GiteaVocabularyProposalClient(HttpClient http, GiteaOptions o) : IVocabularyProposalClient
{
    public const string Label = "vocabulary-proposal";
    string Repo => $"api/v1/repos/{o.Owner}/{o.Repo}";

    public async Task<ProposalIssue> CreateAsync(VocabularyProposal p, CancellationToken ct)
    {
        var labels = await Send<JsonElement>(HttpMethod.Get, $"{Repo}/labels?limit=100", null, ct);
        var label = labels.EnumerateArray().FirstOrDefault(l => l.GetProperty("name").GetString() == Label);
        if (label.ValueKind == JsonValueKind.Undefined) throw new HttpRequestException($"Gitea 上找不到 label {Label}。");

        var body = $"""
            ## 名詞
            {p.Term}

            ## 說明
            {p.Explanation}

            ## 可能的別名
            {(p.Aliases.Count == 0 ? "（無）" : string.Join(", ", p.Aliases))}

            ## 來源需求單
            {p.TicketId}
            """;
        var issue = await Send<JsonElement>(HttpMethod.Post, $"{Repo}/issues",
            new { title = $"[vocabulary-proposal] {p.Term}", body, labels = new[] { label.GetProperty("id").GetInt64() } }, ct);
        return new(issue.GetProperty("number").GetInt32(), issue.GetProperty("html_url").GetString() ?? "");
    }

    async Task<T> Send<T>(HttpMethod method, string url, object? json, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, url);
        if (o.ProposalToken.Length > 0) req.Headers.Authorization = new AuthenticationHeaderValue("token", o.ProposalToken);
        if (json is not null) req.Content = JsonContent.Create(json);
        using var res = await http.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<T>(ct))!;
    }
}
