using System.Net;
using System.Net.Http.Json;

namespace AiAgentStudio.Api.Tests;

public class TicketReassignTests : IClassFixture<TicketFactory>
{
    readonly TicketFactory f;
    public TicketReassignTests(TicketFactory f) => this.f = f;

    Task<HttpResponseMessage> Reassign(string who, string id, string to, string? reason = "原負責人請假") =>
        f.As(who).PostAsJsonAsync($"/api/tickets/{id}/reassign", new { newCimEngineerEmpNo = to, reason });
    async Task<System.Text.Json.JsonElement> Detail(string who, string id) =>
        await TicketFactory.Json(await f.As(who).GetAsync($"/api/tickets/{id}"));

    [Fact]
    public async Task Any_cim_engineer_can_reassign_and_name_snapshot_comes_from_basic_info()
    {
        await f.Create("CIM1", "R-1");
        Assert.Equal(HttpStatusCode.OK, (await Reassign("CIM2", "R-1", "CIM2")).StatusCode); // 非負責人也可改派（含改派給自己）
        var d = await Detail("CIM2", "R-1");
        Assert.Equal("CIM2", d.GetProperty("cimEngineerEmpNo").GetString());
        Assert.Equal("工程師二", d.GetProperty("cimEngineerName").GetString());
        Assert.True(d.GetProperty("permissions").GetProperty("canCancel").GetBoolean());
        Assert.False((await Detail("CIM1", "R-1")).GetProperty("permissions").GetProperty("canCancel").GetBoolean());
    }

    [Fact]
    public async Task Reassign_requires_reason_and_known_target_and_cim_caller()
    {
        await f.Create("CIM1", "R-2");
        Assert.Equal(HttpStatusCode.BadRequest, (await Reassign("CIM1", "R-2", "CIM2", " ")).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Reassign("CIM1", "R-2", "NOBODY")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Reassign("SSO1", "R-2", "CIM2")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Reassign("BIZ1", "R-2", "CIM2")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Reassign("CIM1", "NOPE", "CIM2")).StatusCode);
        Assert.Equal("CIM1", (await Detail("CIM1", "R-2")).GetProperty("cimEngineerEmpNo").GetString());
    }

    [Fact]
    public async Task Target_department_is_not_checked_and_non_cim_assignee_is_read_only()
    {
        await f.Create("CIM1", "R-3");
        Assert.Equal(HttpStatusCode.OK, (await Reassign("CIM1", "R-3", "SSO1")).StatusCode); // 部門等對方登入才判斷
        var d = await Detail("SSO1", "R-3"); // SSO1 現在掛名負責，但不是 CIM Engineer
        Assert.Equal("SSO1", d.GetProperty("cimEngineerEmpNo").GetString());
        var p = d.GetProperty("permissions");
        Assert.False(p.GetProperty("canCancel").GetBoolean());
        Assert.False(p.GetProperty("canReassign").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await f.As("SSO1").PostAsJsonAsync("/api/tickets/R-3/cancel", new { reason = "x" })).StatusCode);
        // 其他 CIM Engineer 可再改派
        Assert.Equal(HttpStatusCode.OK, (await Reassign("CIM2", "R-3", "CIM2")).StatusCode);
    }

    [Fact]
    public async Task Reassign_is_recorded_in_events_and_sends_no_notification()
    {
        await f.Create("CIM1", "R-4");
        await Reassign("CIM2", "R-4", "CIM2", "交接");
        var e = Assert.Single(f.Events(), x => x.TicketId == "R-4");
        Assert.Equal("Reassign", e.Type);
        Assert.Equal("CIM2", e.ActorEmpNo);
        Assert.Equal(("CIM1", "CIM2", "交接"), (e.FromValue, e.ToValue, e.Reason));
    }
}
