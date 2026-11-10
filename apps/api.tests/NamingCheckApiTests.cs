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

/// <summary>#22：naming_check tool API 與 Tool Spec 草稿畫面顯示的欄位狀態（Gitea 用假的）。</summary>
public class NamingCheckApiTests : IDisposable
{
    readonly string path = Path.Combine(Path.GetTempPath(), $"studio-{Guid.NewGuid():N}.json");
    public void Dispose() => File.Delete(path);

    readonly FakeSource src = new() { Sha = "sha1", Text = Entries("eqptId", "status") };
    readonly FakeTimeProvider time = new(DateTimeOffset.UtcNow);

    static string Entries(params string[] camels) =>
        "class T { static object[] E = new[] { " + string.Join(",", camels.Select(c => $"new GlossaryEntry(\"{c}\", \"{char.ToUpper(c[0])}{c[1..]}\", \"d\", new[] {{ \"{c}Alias\" }})")) + " }; }";

    class Factory(string path, FakeSource src, TimeProvider time) : InterviewFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder b)
        {
            base.ConfigureWebHost(b);
            b.ConfigureServices(s =>
            {
                s.RemoveAll<StudioConfigProvider>(); s.AddSingleton(new StudioConfigProvider(path, _ => "k"));
                s.RemoveAll<IVocabularySource>(); s.AddSingleton<IVocabularySource>(src);
                s.RemoveAll<IVocabularyAlert>(); s.AddSingleton<IVocabularyAlert>(new FakeAlert());
                s.RemoveAll<TimeProvider>(); s.AddSingleton(time);
            });
        }
    }

    Factory Make()
    {
        File.WriteAllText(path, """{ "mcpServers": {}, "systems": { "MES": "10000001" } }""");
        return new Factory(path, src, time);
    }

    static HttpClient Agent(InterviewFactory f)
    {
        var c = f.CreateClient();
        c.DefaultRequestHeaders.Add("X-Run-Token", f.Services.GetRequiredService<RunTokenService>().Issue("T-1", "CIM1"));
        return c;
    }
    static async Task<JsonElement> Draft(InterviewFactory f) => await TicketFactory.Json(await f.As("CIM1").GetAsync("/api/tickets/T-1/tool-spec-draft"));
    static Task<HttpResponseMessage> Upsert(HttpClient c, string id, object body) => c.PutAsJsonAsync($"/internal/tools/tool-specs/{id}", body);

    static object SpecWith(object inputs) => new { kind = "New", purpose = "p", system = "MES", inputs, outputs = new { type = "object", properties = new { status = new { type = "string" } } } };
    static readonly object Nested = new
    {
        type = "object",
        properties = new
        {
            eqptIdAlias = new { type = "string" },
            detail = new { type = "object", properties = new Dictionary<string, object> { ["Status"] = new { type = "string" }, ["brand_new"] = new { type = "string" } } },
        },
    };

    [Fact]
    public async Task Agent_naming_check_tool_returns_status_per_field_with_vocabulary_sha()
    {
        using var f = Make(); await f.Seed();
        var r = await Agent(f).GetAsync("/internal/tools/naming-check?fields=eqptId&fields=EQPTID&fields=statusAlias&fields=whatever");
        var j = await TicketFactory.Json(r);
        Assert.Equal("sha1", j.GetProperty("sha").GetString());
        Assert.Equal(
            ["eqptId:pass:", "EQPTID:rename:eqptId", "statusAlias:rename:status", "whatever:new:"],
            j.GetProperty("results").EnumerateArray().Select(x => $"{x.GetProperty("field").GetString()}:{x.GetProperty("status").GetString()}:{(x.TryGetProperty("suggestion", out var sg) ? sg.GetString() : null)}").ToArray());
    }

    [Fact]
    public async Task Naming_check_tool_is_blocked_only_when_there_is_no_cache_at_all()
    {
        src.Fail = new HttpRequestException("gitea down");
        using var f = Make(); await f.Seed();
        var r = await Agent(f).GetAsync("/internal/tools/naming-check?fields=a");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, r.StatusCode);
        Assert.Contains("Tier1Vocabulary", (await TicketFactory.Json(r)).GetProperty("message").GetString());
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.CreateClient().GetAsync("/internal/tools/naming-check?fields=a")).StatusCode);
    }

    [Fact]
    public async Task Draft_shows_status_for_every_field_including_nested_and_records_the_vocabulary_sha()
    {
        using var f = Make(); await f.Seed();
        Assert.Equal(HttpStatusCode.OK, (await Upsert(Agent(f), "ts1", SpecWith(Nested))).StatusCode);

        var d = await Draft(f);
        Assert.Equal("sha1", d.GetProperty("vocabulary").GetProperty("sha").GetString());
        var nc = d.GetProperty("steps")[0].GetProperty("naming_check");
        Assert.Equal("sha1", nc.GetProperty("sha").GetString());
        var fields = nc.GetProperty("fields");
        string Show(string p) => fields.GetProperty(p).GetProperty("status").GetString() + ":" + (fields.GetProperty(p).TryGetProperty("suggestion", out var sg) ? sg.GetString() : null); // null 不輸出
        Assert.Equal("rename:eqptId", Show("inputs.eqptIdAlias"));
        Assert.Equal("new:", Show("inputs.detail"));
        Assert.Equal("rename:status", Show("inputs.detail.Status"));
        Assert.Equal("new:", Show("inputs.detail.brand_new"));
        Assert.Equal("pass:", Show("outputs.status"));
    }

    [Fact]
    public async Task Draft_is_rechecked_when_vocabulary_sha_moves_and_a_new_hit_only_warns()
    {
        using var f = Make(); await f.Seed();
        await Upsert(Agent(f), "ts1", SpecWith(Nested));
        await Draft(f);

        (src.Sha, src.Text) = ("sha2", Entries("eqptId", "status", "brand_new"));
        time.Advance(TimeSpan.FromMinutes(6));
        var d = await Draft(f);
        var nc = d.GetProperty("steps")[0].GetProperty("naming_check");
        Assert.Equal("sha2", nc.GetProperty("sha").GetString());
        Assert.Equal("pass", nc.GetProperty("fields").GetProperty("inputs.detail.brand_new").GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.OK, (await f.As("CIM1").GetAsync("/api/tickets/T-1")).StatusCode); // 不影響單的狀態
    }

    [Fact]
    public async Task Draft_keeps_last_good_check_and_shows_warning_when_vocabulary_refresh_fails()
    {
        using var f = Make(); await f.Seed();
        await Upsert(Agent(f), "ts1", SpecWith(Nested));
        await Draft(f);

        src.Fail = new HttpRequestException("gitea down");
        time.Advance(TimeSpan.FromMinutes(6));
        var d = await Draft(f);
        Assert.Contains("sha1", d.GetProperty("vocabulary").GetProperty("warning").GetString());
        Assert.Equal("sha1", d.GetProperty("steps")[0].GetProperty("naming_check").GetProperty("sha").GetString());
    }

    [Fact]
    public async Task Draft_without_any_vocabulary_still_loads_and_reports_the_error()
    {
        src.Fail = new HttpRequestException("gitea down");
        using var f = Make(); await f.Seed();
        await Upsert(Agent(f), "ts1", SpecWith(Nested));
        var d = await Draft(f);
        Assert.Contains("Tier1Vocabulary", d.GetProperty("vocabularyError").GetString());
        Assert.False(d.GetProperty("steps")[0].TryGetProperty("naming_check", out _));
    }
}
