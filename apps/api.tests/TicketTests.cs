using System.Net;
using System.Net.Http.Json;

namespace AiAgentStudio.Api.Tests;

public class TicketCreateTests : IClassFixture<TicketFactory>
{
    readonly TicketFactory f;
    public TicketCreateTests(TicketFactory f) => this.f = f;

    [Fact]
    public async Task Cim_engineer_creates_ticket_with_business_user_name_snapshot()
    {
        var r = await f.Create("CIM1", "T-100");
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var j = await TicketFactory.Json(await f.As("SSO1").GetAsync("/api/tickets/T-100"));
        Assert.Equal("業務一", j.GetProperty("businessUserName").GetString());
        Assert.Equal("BIZ1", j.GetProperty("businessUserEmpNo").GetString());
        Assert.Equal("CIM1", j.GetProperty("cimEngineerEmpNo").GetString());
        Assert.Equal("工程師一", j.GetProperty("cimEngineerName").GetString());
        Assert.Equal("訪談中", j.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Unknown_business_user_is_rejected()
    {
        var r = await f.Create("CIM1", "T-101", bu: "NOBODY");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await f.As("CIM1").GetAsync("/api/tickets/T-101")).StatusCode);
    }

    [Fact]
    public async Task Non_cim_user_cannot_create() =>
        Assert.Equal(HttpStatusCode.Forbidden, (await f.Create("SSO1", "T-102")).StatusCode);

    [Fact]
    public async Task Duplicate_id_is_conflict()
    {
        await f.Create("CIM1", "T-103");
        Assert.Equal(HttpStatusCode.Conflict, (await f.Create("CIM2", "T-103")).StatusCode);
    }

    [Fact]
    public async Task Everyone_can_list_and_anonymous_cannot()
    {
        await f.Create("CIM1", "T-104");
        var j = await TicketFactory.Json(await f.As("SSO1").GetAsync("/api/tickets"));
        Assert.Contains(j.EnumerateArray(), t => t.GetProperty("id").GetString() == "T-104");
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.CreateClient().GetAsync("/api/tickets")).StatusCode);
    }

    [Fact]
    public async Task Detail_hides_conversation_from_ordinary_sso_and_business_user_but_not_cim()
    {
        await f.Create("CIM1", "T-105");
        async Task<bool> Can(string who) => (await TicketFactory.Json(await f.As(who).GetAsync("/api/tickets/T-105"))).GetProperty("permissions").GetProperty("canViewConversation").GetBoolean();
        Assert.True(await Can("CIM2"));
        Assert.True(await Can("CIM1"));
        Assert.False(await Can("SSO1"));
        Assert.False(await Can("BIZ1"));
    }
}
