using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AiAgentStudio.Api.Interviews;
using AiAgentStudio.Api.Studio;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelContextProtocol.Protocol;

namespace AiAgentStudio.Api.Tests;

/// <summary>#21：Tool Spec 草稿（Reuse／Extend／New），沿用 #20 的人工修改優先機制。回應的 "steps" 即 Tool Spec 清單（共用 DraftView）。</summary>
public class ToolSpecDraftTests : IDisposable
{
    readonly string path = Path.Combine(Path.GetTempPath(), $"studio-{Guid.NewGuid():N}.json");
    public void Dispose() => File.Delete(path);

    class Factory(string path) : InterviewFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder b)
        {
            base.ConfigureWebHost(b);
            b.ConfigureServices(s =>
            {
                s.RemoveAll<StudioConfigProvider>(); s.AddSingleton(new StudioConfigProvider(path, _ => "k"));
                s.RemoveAll<IMcpTransportFactory>();
                s.AddSingleton<IMcpTransportFactory>(new McpToolCatalogTests.FakeServers(new()
                {
                    ["gw"] = [new Tool { Name = "get_part", Description = "查詢料件", InputSchema = McpToolCatalogTests.Obj("{\"id\":{\"type\":\"string\"}}") }],
                }));
            });
        }
    }

    Factory Make()
    {
        File.WriteAllText(path, """
        { "mcpServers": { "gw": { "type": "http", "url": "http://gw/mcp" } },
          "systems": { "MES": "10000001", "ERP": "10000002" } }
        """);
        return new Factory(path);
    }

    static HttpClient Agent(InterviewFactory f, string ticket = "T-1", string who = "CIM1")
    {
        var c = f.CreateClient();
        c.DefaultRequestHeaders.Add("X-Run-Token", f.Services.GetRequiredService<RunTokenService>().Issue(ticket, who));
        return c;
    }
    static Task<HttpResponseMessage> Upsert(HttpClient c, string id, object body) => c.PutAsJsonAsync($"/internal/tools/tool-specs/{id}", body);
    static async Task<JsonElement> Draft(InterviewFactory f, string who = "CIM1") => await TicketFactory.Json(await f.As(who).GetAsync("/api/tickets/T-1/tool-spec-draft"));
    static Task<HttpResponseMessage> Human(InterviewFactory f, int version, object specs, string who = "CIM1") =>
        f.As(who).PutAsJsonAsync("/api/tickets/T-1/tool-spec-draft", new { version, steps = specs });

    static readonly object Str = new { type = "string" };
    static object NewSpec(string purpose = "查工單") => new
    {
        kind = "New", purpose, system = "MES", system_owner = "10000001", side_effect = "read",
        inputs = new { type = "object", properties = new { work_order_no = Str }, required = new[] { "work_order_no" } },
        outputs = new { type = "object", properties = new { status = Str } },
    };

    [Fact]
    public async Task Agent_creates_spec_and_reports_missing_required_fields()
    {
        using var f = Make(); await f.Seed();
        var a = Agent(f);
        var r = await Upsert(a, "ts1", new { kind = "New", purpose = "查工單", system = "MES" });
        r.EnsureSuccessStatusCode();
        var missing = (await TicketFactory.Json(r)).GetProperty("missing").EnumerateArray().Select(x => x.GetString()).Order().ToArray();
        Assert.Equal(["inputs", "outputs", "side_effect", "system_owner"], missing);

        Assert.Equal(HttpStatusCode.OK, (await Upsert(a, "ts1", NewSpec())).StatusCode);
        var d = await Draft(f);
        Assert.Empty(d.GetProperty("missing").GetProperty("ts1").EnumerateArray());
        Assert.Equal("查工單", d.GetProperty("steps")[0].GetProperty("purpose").GetString());
    }

    [Fact]
    public async Task Reuse_needs_a_ref_but_no_system_owner()
    {
        using var f = Make(); await f.Seed();
        var r = await Upsert(Agent(f), "ts1", new
        {
            kind = "Reuse", purpose = "查料件", system = "MES", side_effect = "read", @ref = new { server = "gw", tool = "get_part" },
            inputs = new { type = "object", properties = new { id = Str } }, outputs = new { type = "object", properties = new { qty = new { type = "integer" } } },
        });
        Assert.Empty((await TicketFactory.Json(r)).GetProperty("missing").EnumerateArray());
    }

    [Theory]
    [InlineData("{\"kind\":\"Other\"}")]
    [InlineData("{\"side_effect\":\"delete\"}")]
    [InlineData("{\"system\":\"NOPE\"}")]
    [InlineData("{\"inputs\":{\"type\":\"date\"}}")]
    [InlineData("{\"inputs\":{\"type\":\"object\",\"properties\":{\"a\":{\"type\":\"array\"}}}}")]                       // array 缺 items
    [InlineData("{\"inputs\":{\"type\":\"object\",\"properties\":{\"a\":{\"type\":\"string\",\"format\":\"email\"}}}}")] // 只支援 date-time
    [InlineData("{\"outputs\":{\"type\":\"object\",\"additionalProperties\":true}}")]                                    // 子集以外的關鍵字
    [InlineData("{\"inputs\":{\"type\":\"object\",\"properties\":{\"a\":{\"type\":\"string\"}},\"required\":[\"b\"]}}")]
    public async Task Invalid_fields_are_rejected_with_400_and_nothing_is_written(string json)
    {
        using var f = Make(); await f.Seed();
        var body = JsonDocument.Parse(json).RootElement;
        var r = await Upsert(Agent(f), "ts1", body);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.False(string.IsNullOrEmpty((await TicketFactory.Json(r)).GetProperty("message").GetString()));
        Assert.Equal(0, (await Draft(f)).GetProperty("steps").GetArrayLength());
    }

    [Fact]
    public async Task Nested_schema_with_enum_and_date_time_is_accepted()
    {
        using var f = Make(); await f.Seed();
        var r = await Upsert(Agent(f), "ts1", new
        {
            inputs = new
            {
                type = "object", required = new[] { "lines" },
                properties = new
                {
                    lines = new { type = "array", items = new { type = "object", properties = new { at = new { type = "string", format = "date-time" }, kind = new { type = "string", @enum = new[] { "a", "b" } } } } },
                },
            },
        });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    [Fact]
    public async Task Reuse_and_Extend_keep_schema_snapshot_of_the_referenced_tool()
    {
        using var f = Make(); await f.Seed();
        var a = Agent(f);
        var ok = await Upsert(a, "ts1", new { kind = "Extend", @ref = new { server = "gw", tool = "get_part" } });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var rf = (await Draft(f)).GetProperty("steps")[0].GetProperty("ref");
        Assert.Equal("get_part", rf.GetProperty("tool").GetString());
        Assert.Equal("string", rf.GetProperty("schema_snapshot").GetProperty("input").GetProperty("properties").GetProperty("id").GetProperty("type").GetString());

        Assert.Equal(HttpStatusCode.BadRequest, (await Upsert(a, "ts2", new { @ref = new { server = "gw", tool = "nope" } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Upsert(a, "ts2", new { @ref = new { server = "zz", tool = "x" } })).StatusCode);
    }

    [Fact]
    public async Task Tool_api_needs_run_token_and_a_spec_belongs_only_to_its_ticket()
    {
        using var f = Make(); await f.Seed();
        Assert.Equal(HttpStatusCode.Unauthorized, (await Upsert(f.CreateClient(), "ts1", new { purpose = "x" })).StatusCode);
        (await f.Create("CIM2", "T-2", "別張")).EnsureSuccessStatusCode();
        (await Upsert(Agent(f, "T-2", "CIM2"), "ts1", new { purpose = "他單" })).EnsureSuccessStatusCode();
        Assert.Equal(0, (await Draft(f)).GetProperty("steps").GetArrayLength());
    }

    [Fact]
    public async Task Human_edit_is_protected_until_override_and_is_logged()
    {
        using var f = Make(); await f.Seed();
        var a = Agent(f);
        await Upsert(a, "ts1", NewSpec("A")); // v1
        Assert.Equal(HttpStatusCode.OK, (await Human(f, 1, new[] { Id(JsonSerializer.SerializeToElement(NewSpec("人工")), "ts1") })).StatusCode);
        var d = await Draft(f);
        Assert.Equal(["tool_specs.ts1.purpose"], d.GetProperty("humanEdits").EnumerateArray().Select(x => x.GetString()));

        var rej = await Upsert(a, "ts1", new { purpose = "agent 想改" });
        Assert.Equal(HttpStatusCode.Conflict, rej.StatusCode);
        var rr = (await TicketFactory.Json(rej)).GetProperty("rejected")[0];
        Assert.Equal(("tool_specs.ts1.purpose", "人工"), (rr.GetProperty("path").GetString(), rr.GetProperty("current").GetString()));

        Assert.Equal(HttpStatusCode.OK, (await Upsert(a, "ts1", new { system = "ERP" })).StatusCode); // 沒碰到人工欄位
        Assert.Equal(HttpStatusCode.OK, (await Upsert(a, "ts1", new { purpose = "覆寫", override_human_edit = true })).StatusCode);
        var ev = Assert.Single(f.Events());
        Assert.Equal(("OverrideHumanEdit", "tool_specs.ts1.purpose"), (ev.Type, ev.Reason));
    }

    static object Id(JsonElement e, string id)
    {
        var d = JsonSerializer.Deserialize<Dictionary<string, object>>(e.GetRawText())!; d["id"] = id; return d;
    }

    [Fact]
    public async Task Human_save_needs_version_owner_and_valid_schema()
    {
        using var f = Make(); await f.Seed();
        await Upsert(Agent(f), "ts1", NewSpec("A")); // v1
        var good = new[] { Id(JsonSerializer.SerializeToElement(NewSpec("B")), "ts1") };
        Assert.Equal(HttpStatusCode.Forbidden, (await Human(f, 1, good, "CIM2")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Human(f, 0, good)).StatusCode);
        var bad = new[] { Id(JsonSerializer.SerializeToElement(new { inputs = new { type = "date" } }), "ts1") };
        Assert.Equal(HttpStatusCode.BadRequest, (await Human(f, 1, bad)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Human(f, 1, good)).StatusCode);
        Assert.Equal(2, (await Draft(f, "CIM2")).GetProperty("version").GetInt32());
    }

    [Fact]
    public async Task Workflow_draft_and_tool_spec_draft_are_independent_and_both_reach_the_agent()
    {
        using var f = Make(); await f.Seed();
        await Upsert(Agent(f), "ts1", NewSpec("A"));
        await Sse.Read(await f.Send("CIM1", "一"));
        var req = f.Agent.Requests[^1];
        Assert.Equal(0, req.Draft!["steps"]!.AsArray().Count);
        Assert.Equal("A", req.Draft["tool_specs"]![0]!["purpose"]!.GetValue<string>());

        await Human(f, 1, new[] { Id(JsonSerializer.SerializeToElement(NewSpec("B")), "ts1") });
        await Sse.Read(await f.Send("CIM1", "二"));
        var diff = f.Agent.Requests[^1].DraftDiff!.AsArray().Single()!;
        Assert.Equal(("tool_specs.ts1.purpose", "A", "B"), (diff["path"]!.GetValue<string>(), diff["before"]!.GetValue<string>(), diff["after"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Delete_removes_spec_and_search_finds_other_tickets_in_progress_specs()
    {
        using var f = Make(); await f.Seed();
        (await f.Create("CIM2", "T-2", "別張")).EnsureSuccessStatusCode();
        var mine = Agent(f); var other = Agent(f, "T-2", "CIM2");
        await Upsert(mine, "ts1", NewSpec("查工單狀態"));
        await Upsert(other, "x1", NewSpec("查詢工單進度"));
        await Upsert(other, "x2", new { kind = "New", purpose = "發料", system = "ERP" });

        var hits = await TicketFactory.Json(await mine.GetAsync("/internal/tools/tool-specs?keyword=%E5%B7%A5%E5%96%AE")); // 工單
        var h = Assert.Single(hits.EnumerateArray());
        Assert.Equal(("T-2", "x1", "查詢工單進度"), (h.GetProperty("ticketId").GetString(), h.GetProperty("id").GetString(), h.GetProperty("purpose").GetString()));
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.CreateClient().GetAsync("/internal/tools/tool-specs?keyword=x")).StatusCode);

        await f.As("CIM2").PostAsJsonAsync("/api/tickets/T-2/cancel", new { reason = "不做了" }); // 已取消的單不算進行中
        Assert.Empty((await TicketFactory.Json(await mine.GetAsync("/internal/tools/tool-specs?keyword=%E5%B7%A5%E5%96%AE"))).EnumerateArray());

        Assert.Equal(HttpStatusCode.OK, (await mine.DeleteAsync("/internal/tools/tool-specs/ts1")).StatusCode);
        Assert.Equal(0, (await Draft(f)).GetProperty("steps").GetArrayLength());
    }
}
