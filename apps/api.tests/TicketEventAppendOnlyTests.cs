using System.Net.Http.Json;
using AiAgentStudio.Api.Tickets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;

namespace AiAgentStudio.Api.Tests;

public class TicketEventAppendOnlyTests : IClassFixture<TicketFactory>
{
    readonly TicketFactory f;
    public TicketEventAppendOnlyTests(TicketFactory f) => this.f = f;

    [Fact]
    public async Task Existing_events_cannot_be_modified_or_deleted()
    {
        await f.Create("CIM1", "E-1");
        await f.As("CIM1").PostAsync("/api/tickets/E-1/cancel", JsonContent.Create(new { reason = "x" }));
        using var s = f.Services.CreateScope();
        var db = s.ServiceProvider.GetRequiredService<StudioDb>();
        var e = db.TicketEvents.First(x => x.TicketId == "E-1");
        e.Reason = "改寫";
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();
        db.TicketEvents.Remove(db.TicketEvents.First(x => x.TicketId == "E-1"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }
}
