using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AiAgentStudio.Api.Interviews;
using AiAgentStudio.Api.Studio;
using AiAgentStudio.Api.Vocabulary;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using static AiAgentStudio.Api.Tests.VocabularyServiceTests;

namespace AiAgentStudio.Api.Tests;

/// <summary>#23：Pending Term（spec 7.3）。Gitea（詞彙來源與提案 issue）一律用假的，沒有碰真實外部服務。</summary>
public class PendingTermTests : IDisposable
{
    readonly string path = Path.Combine(Path.GetTempPath(), $"studio-{Guid.NewGuid():N}.json");
    public void Dispose() => File.Delete(path);

    class FakeProposals : IVocabularyProposalClient
    {
        public List<VocabularyProposal> Calls = [];
        public Exception? Fail;
        public Task<ProposalIssue> CreateAsync(VocabularyProposal p, CancellationToken ct)
        {
            if (Fail is not null) return Task.FromException<ProposalIssue>(Fail);
            Calls.Add(p);
            return Task.FromResult(new ProposalIssue(100 + Calls.Count, $"https://gitea.test/issues/{100 + Calls.Count}"));
        }
    }

    readonly FakeSource src = new() { Sha = "sha1", Text = Src("eqptId") };
    readonly FakeProposals proposals = new();
    readonly FakeTimeProvider time = new(DateTimeOffset.UtcNow);

    class Factory(string path, FakeSource src, FakeProposals proposals, TimeProvider time) : InterviewFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder b)
        {
            base.ConfigureWebHost(b);
            b.ConfigureServices(s =>
            {
                s.RemoveAll<StudioConfigProvider>(); s.AddSingleton(new StudioConfigProvider(path, _ => "k"));
                s.RemoveAll<IVocabularySource>(); s.AddSingleton<IVocabularySource>(src);
                s.RemoveAll<IVocabularyAlert>(); s.AddSingleton<IVocabularyAlert>(new FakeAlert());
                s.RemoveAll<IVocabularyProposalClient>(); s.AddSingleton<IVocabularyProposalClient>(proposals);
                s.RemoveAll<TimeProvider>(); s.AddSingleton(time);
            });
        }
    }

    async Task<Factory> Make()
    {
        File.WriteAllText(path, """{ "mcpServers": {}, "systems": { "MES": "10000001" } }""");
        var f = new Factory(path, src, proposals, time);
        await f.Seed(); // T-1，負責人 CIM1
        return f;
    }

    static Task<HttpResponseMessage> Confirm(HttpClient c, object body, string ticket = "T-1") => c.PostAsJsonAsync($"/api/tickets/{ticket}/pending-terms", body);
    static object Body(string term = "lotGrade", string? explanation = "批號等級", string[]? aliases = null) => new { term, explanation, aliases = aliases ?? ["grade"] };

    [Fact]
    public async Task Responsible_cim_engineer_confirms_new_term_which_opens_a_proposal_issue_and_becomes_pending()
    {
        using var f = await Make();
        var r = await Confirm(f.As("CIM1"), Body());
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);

        var p = Assert.Single(proposals.Calls);
        Assert.Equal(("lotGrade", "批號等級", "T-1"), (p.Term, p.Explanation, p.TicketId));
        Assert.Equal(["grade"], p.Aliases);

        var list = await TicketFactory.Json(await f.As("CIM1").GetAsync("/api/pending-terms"));
        var t = Assert.Single(list.EnumerateArray());
        Assert.Equal(("lotGrade", "批號等級", "CIM1", "Pending", "https://gitea.test/issues/101"),
            (t.GetProperty("term").GetString(), t.GetProperty("explanation").GetString(), t.GetProperty("confirmedBy").GetString(),
             t.GetProperty("status").GetString(), t.GetProperty("giteaIssueUrl").GetString()));
    }

    [Theory]
    [InlineData("CIM2")] // 別張單的 CIM Engineer
    [InlineData("BIZ1")] // Business User
    public async Task Only_the_responsible_cim_engineer_can_confirm(string who)
    {
        using var f = await Make();
        Assert.Equal(HttpStatusCode.Forbidden, (await Confirm(f.As(who), Body())).StatusCode);
        Assert.Empty(proposals.Calls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public async Task Explanation_is_required(string? explanation)
    {
        using var f = await Make();
        Assert.Equal(HttpStatusCode.BadRequest, (await Confirm(f.As("CIM1"), Body(explanation: explanation))).StatusCode);
        Assert.Empty(proposals.Calls);
    }

    [Fact]
    public async Task Agent_has_no_way_to_confirm_a_term()
    {
        using var f = await Make();
        var agent = f.CreateClient();
        agent.DefaultRequestHeaders.Add("X-Run-Token", f.Services.GetRequiredService<RunTokenService>().Issue("T-1", "CIM1"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await Confirm(agent, Body())).StatusCode); // run token 不是登入
        Assert.False((await agent.PostAsJsonAsync("/internal/tools/confirm-term", Body())).IsSuccessStatusCode); // 沒有這個工具
        Assert.Empty(proposals.Calls);
    }

    [Fact]
    public async Task Gitea_failure_gives_a_clear_error_and_leaves_no_pending_term_so_retry_opens_exactly_one_issue()
    {
        using var f = await Make();
        proposals.Fail = new HttpRequestException("gitea down");
        var r = await Confirm(f.As("CIM1"), Body());
        Assert.Equal(HttpStatusCode.BadGateway, r.StatusCode);
        Assert.Contains("Gitea", (await TicketFactory.Json(r)).GetProperty("detail").GetString());
        Assert.Empty((await TicketFactory.Json(await f.As("CIM1").GetAsync("/api/pending-terms"))).EnumerateArray());

        proposals.Fail = null;
        Assert.Equal(HttpStatusCode.Created, (await Confirm(f.As("CIM1"), Body())).StatusCode);
        Assert.Single(proposals.Calls);
    }

    [Fact]
    public async Task Confirming_the_same_term_again_does_not_open_a_duplicate_issue()
    {
        using var f = await Make();
        await Confirm(f.As("CIM1"), Body());
        var r = await Confirm(f.As("CIM1"), Body(term: "LOTGRADE"));
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Contains("https://gitea.test/issues/101", (await TicketFactory.Json(r)).GetProperty("detail").GetString());
        Assert.Single(proposals.Calls);
    }

    [Theory]
    [InlineData("eqptId")]
    [InlineData("EQPTID")]
    public async Task A_term_the_vocabulary_already_knows_is_not_a_new_term(string term)
    {
        using var f = await Make();
        Assert.Equal(HttpStatusCode.Conflict, (await Confirm(f.As("CIM1"), Body(term: term))).StatusCode);
        Assert.Empty(proposals.Calls);
    }
}
