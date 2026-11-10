using System;
using System.Linq;
using Microsoft.EntityFrameworkCore;

namespace AiAgentStudio.Api.Tickets;

public static class TicketStatus
{
    public const string Interviewing = "Interviewing"; // 訪談中
    public const string Cancelled = "Cancelled";       // 已取消
    /// <summary>已送出：只是「不在訪談中」的標記，顯示用的狀態由 Tool Spec 推導（<c>TicketStatusDeriver</c>）。</summary>
    public const string Submitted = "Submitted";
    public const string Evaluating = "Evaluating";     // 評估中（推導）
    public const string Implementing = "Implementing"; // 實作中（推導）
    public const string Completed = "Completed";       // 已完成（推導）
    public static string Label(string s) => s switch
    {
        Cancelled => "已取消", Evaluating => "評估中", Implementing => "實作中", Completed => "已完成", _ => "訪談中",
    };
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

    public DbSet<AiAgentStudio.Api.Workflow.WorkflowDraft> WorkflowDrafts => Set<AiAgentStudio.Api.Workflow.WorkflowDraft>();
    public DbSet<AiAgentStudio.Api.Interviews.Interview> Interviews => Set<AiAgentStudio.Api.Interviews.Interview>();
    public DbSet<AiAgentStudio.Api.Vocabulary.PendingTerm> PendingTerms => Set<AiAgentStudio.Api.Vocabulary.PendingTerm>();
    public DbSet<AiAgentStudio.Api.Interviews.AgentRun> AgentRuns => Set<AiAgentStudio.Api.Interviews.AgentRun>();

    public DbSet<AiAgentStudio.Api.Submissions.Submission> Submissions => Set<AiAgentStudio.Api.Submissions.Submission>();
    public DbSet<AiAgentStudio.Api.Submissions.ToolSpecRevision> ToolSpecRevisions => Set<AiAgentStudio.Api.Submissions.ToolSpecRevision>();
    public DbSet<AiAgentStudio.Api.Submissions.ToolSpecRecord> ToolSpecRecords => Set<AiAgentStudio.Api.Submissions.ToolSpecRecord>();

    public DbSet<AiAgentStudio.Api.Submissions.FeasibilityReview> FeasibilityReviews => Set<AiAgentStudio.Api.Submissions.FeasibilityReview>();

    public DbSet<AiAgentStudio.Api.Notifications.EmailOutbox> EmailOutbox => Set<AiAgentStudio.Api.Notifications.EmailOutbox>();

    /// <summary>啟動時清除遺留的「執行中」標記（spec 2.4）；回傳清除筆數。</summary>
    public int ClearStaleRunning()
    {
        var stale = Interviews.Where(x => x.IsRunning).ToList();
        foreach (var iv in stale) iv.IsRunning = false;
        SaveChanges();
        return stale.Count;
    }

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
        b.Entity<AiAgentStudio.Api.Workflow.WorkflowDraft>(e =>
        {
            e.ToTable("workflow_draft");
            e.HasKey(x => new { x.TicketId, x.Doc });
            e.Property(x => x.TicketId).HasMaxLength(64);
            e.Property(x => x.Doc).HasMaxLength(32).HasDefaultValue(AiAgentStudio.Api.Workflow.WorkflowDraft.StepsDoc);
            e.Property(x => x.Version).IsConcurrencyToken();
            e.Property(x => x.DraftJson).HasColumnType("jsonb");
            e.Property(x => x.SeenJson).HasColumnType("jsonb");
            e.Property(x => x.HumanEditsJson).HasColumnType("jsonb");
        });
        b.Entity<AiAgentStudio.Api.Vocabulary.PendingTerm>(e =>
        {
            e.ToTable("pending_term");
            e.HasIndex(x => x.Term);
            e.Property(x => x.AliasesJson).HasColumnType("jsonb");
        });
        b.Entity<AiAgentStudio.Api.Submissions.Submission>(e =>
        {
            e.ToTable("submission");
            e.HasIndex(x => new { x.TicketId, x.Number }).IsUnique();
            e.Property(x => x.WorkflowJson).HasColumnType("jsonb");
        });
        b.Entity<AiAgentStudio.Api.Submissions.ToolSpecRevision>(e =>
        {
            e.ToTable("tool_spec_revision");
            e.HasIndex(x => new { x.TicketId, x.SpecId, x.Revision }).IsUnique();
            e.Property(x => x.ContentJson).HasColumnType("jsonb");
        });
        b.Entity<AiAgentStudio.Api.Submissions.ToolSpecRecord>(e =>
        {
            e.ToTable("tool_spec");
            e.HasKey(x => new { x.TicketId, x.SpecId });
            e.Property(x => x.TicketId).HasMaxLength(64);
        });
        b.Entity<AiAgentStudio.Api.Submissions.FeasibilityReview>(e =>
        {
            e.ToTable("feasibility_review");
            e.HasIndex(x => new { x.TicketId, x.SpecId, x.Revision });
            e.Property(x => x.TicketId).HasMaxLength(64);
        });
        b.Entity<AiAgentStudio.Api.Notifications.EmailOutbox>(e =>
        {
            e.ToTable("email_outbox");
            e.HasIndex(x => new { x.Status, x.NextAttemptAt });
            e.Property(x => x.PayloadJson).HasColumnType("jsonb");
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
