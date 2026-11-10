using AiAgentStudio.Api.Tickets;
using Microsoft.EntityFrameworkCore;

namespace AiAgentStudio.Api.Tests;

/// <summary>
/// 對真的 PostgreSQL 驗證 migrations 與 append-only 持久化。需要環境變數 AAS_PG（連線字串，見 README），沒設定時直接略過。
/// 例：AAS_PG="Host=localhost;Port=5432;Database=studio;Username=studio;Password=studio"
/// </summary>
public class TicketPostgresTests
{
    static readonly string? Cs = Environment.GetEnvironmentVariable("AAS_PG");

    [Fact]
    public async Task Migrations_apply_and_ticket_roundtrips()
    {
        if (Cs is null) return;
        var opt = new DbContextOptionsBuilder<StudioDb>().UseNpgsql(Cs).Options;
        await using var db = new StudioDb(opt);
        await db.Database.MigrateAsync();
        var id = "PG-" + Guid.NewGuid().ToString("N")[..8];
        db.Tickets.Add(new RequirementTicket { Id = id, Description = "d", CimEngineerEmpNo = "1", CimEngineerName = "a", BusinessUserEmpNo = "2", BusinessUserName = "b", CreatedAt = DateTimeOffset.UtcNow });
        db.TicketEvents.Add(new TicketEvent { TicketId = id, Type = "Cancel", ActorEmpNo = "1", At = DateTimeOffset.UtcNow });
        db.PendingTerms.Add(new AiAgentStudio.Api.Vocabulary.PendingTerm { Term = "t-" + id, Explanation = "e", AliasesJson = "[\"a\"]", TicketId = id, GiteaIssueNumber = 1, GiteaIssueUrl = "u", ConfirmedByEmpNo = "1", CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        await using var db2 = new StudioDb(opt);
        Assert.Equal("Interviewing", (await db2.Tickets.SingleAsync(t => t.Id == id)).Status);
        Assert.Single(db2.TicketEvents.Where(e => e.TicketId == id));
        Assert.Equal("Pending", (await db2.PendingTerms.SingleAsync(x => x.TicketId == id)).Status);
    }
}
