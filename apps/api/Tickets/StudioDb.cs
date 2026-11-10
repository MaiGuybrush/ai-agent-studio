using System;
using System.Linq;
using Microsoft.EntityFrameworkCore;

namespace AiAgentStudio.Api.Tickets;

public static class TicketStatus
{
    public const string Interviewing = "Interviewing"; // 訪談中
    public const string Cancelled = "Cancelled";       // 已取消
    public static string Label(string s) => s == Cancelled ? "已取消" : "訪談中";
}

/// <summary>需求單（spec 13）。只存人觸發的狀態；其餘狀態之後由 Tool Spec 推導。</summary>
public class RequirementTicket
{
    public string Id { get; set; } = "";               // 單號（主鍵）
    public string Description { get; set; } = "";
    public string CimEngineerEmpNo { get; set; } = ""; // 負責的 CIM Engineer（工號＋姓名快照）
    public string CimEngineerName { get; set; } = "";
    public string BusinessUserEmpNo { get; set; } = "";
    public string BusinessUserName { get; set; } = "";
    public string Status { get; set; } = TicketStatus.Interviewing;
    public string? CancelReason { get; set; }
    public string? StatusBeforeCancel { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>人工狀態變更紀錄，只能新增。</summary>
public class TicketEvent
{
    public long Id { get; set; }
    public string TicketId { get; set; } = "";
    public string Type { get; set; } = ""; // Cancel / Reopen / Reassign
    public string ActorEmpNo { get; set; } = "";
    public DateTimeOffset At { get; set; }
    public string? FromValue { get; set; }
    public string? ToValue { get; set; }
    public string? Reason { get; set; }
}

public class StudioDb(DbContextOptions<StudioDb> options) : DbContext(options)
{
    public DbSet<RequirementTicket> Tickets => Set<RequirementTicket>();
    public DbSet<TicketEvent> TicketEvents => Set<TicketEvent>();

    public DbSet<AiAgentStudio.Api.Interviews.Interview> Interviews => Set<AiAgentStudio.Api.Interviews.Interview>();
    public DbSet<AiAgentStudio.Api.Interviews.AgentRun> AgentRuns => Set<AiAgentStudio.Api.Interviews.AgentRun>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<AiAgentStudio.Api.Interviews.Interview>(e =>
        {
            e.ToTable("interview");
            e.HasIndex(x => x.TicketId);
            e.Property(x => x.ConversationJson).HasColumnType("jsonb");
        });
        b.Entity<AiAgentStudio.Api.Interviews.AgentRun>(e =>
        {
            e.ToTable("agent_run");
            e.HasIndex(x => x.TicketId);
        });
        b.Entity<RequirementTicket>(e =>
        {
            e.ToTable("requirement_ticket");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(64);
        });
        b.Entity<TicketEvent>(e =>
        {
            e.ToTable("ticket_event");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.TicketId);
        });
    }

    // ticket_event 只能新增：已存在的事件不得修改或刪除（DB 層級的保護可再加 trigger）。
    void GuardAppendOnly()
    {
        if (ChangeTracker.Entries<TicketEvent>().Any(x => x.State is EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("ticket_event is append-only.");
    }
    public override int SaveChanges(bool acceptAllChangesOnSuccess) { GuardAppendOnly(); return base.SaveChanges(acceptAllChangesOnSuccess); }
    public override System.Threading.Tasks.Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, System.Threading.CancellationToken ct = default)
    { GuardAppendOnly(); return base.SaveChangesAsync(acceptAllChangesOnSuccess, ct); }
}
