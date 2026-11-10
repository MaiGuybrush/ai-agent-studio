using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AiAgentStudio.Api.Identity;
using AiAgentStudio.Api.Services;
using AiAgentStudio.Api.Tickets;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AiAgentStudio.Api.Vocabulary;

public sealed record VocabularyProposal(string Term, string Explanation, IReadOnlyList<string> Aliases, string TicketId);
public sealed record ProposalIssue(int Number, string Url);

/// <summary>在 McpGateway.Core 的 Gitea 開 vocabulary-proposal issue（服務帳號；不開 PR）。介面存在是為了測試能換成假的。</summary>
public interface IVocabularyProposalClient
{
    /// <summary>失敗時丟例外（呼叫端不會留下 Pending Term）。</summary>
    Task<ProposalIssue> CreateAsync(VocabularyProposal proposal, CancellationToken ct);
}

public static class PendingTermStatus
{
    public const string Pending = "Pending";   // 已提案，尚未收錄
    public const string Promoted = "Promoted"; // 已出現在 Tier1Vocabulary，自動移出
}

/// <summary>pending_term（spec 13）：新名詞、說明、Gitea issue、確認者、狀態。</summary>
public class PendingTerm
{
    public long Id { get; set; }
    public string Term { get; set; } = "";
    public string Explanation { get; set; } = "";
    public string AliasesJson { get; set; } = "[]";
    public string TicketId { get; set; } = "";
    public int GiteaIssueNumber { get; set; }
    public string GiteaIssueUrl { get; set; } = "";
    public string ConfirmedByEmpNo { get; set; } = "";
    public string Status { get; set; } = PendingTermStatus.Pending;
    public DateTimeOffset CreatedAt { get; set; }
}

[ApiController]
[Authorize]
public class PendingTermController(ICurrentUserService user, IdentityService identity, StudioDb db, IVocabularyProposalClient proposals, TimeProvider time) : ControllerBase
{
    public record ConfirmRequest(string? Term, string? Explanation, string[]? Aliases);

    static object View(PendingTerm t) => new
    {
        term = t.Term, explanation = t.Explanation, aliases = JsonSerializer.Deserialize<string[]>(t.AliasesJson), ticketId = t.TicketId,
        giteaIssueNumber = t.GiteaIssueNumber, giteaIssueUrl = t.GiteaIssueUrl, confirmedBy = t.ConfirmedByEmpNo, status = t.Status,
    };

    /// <summary>只有該單負責的 CIM Engineer 能確認；agent 沒有對應工具（spec 8.3）。</summary>
    [HttpPost("api/tickets/{ticketId}/pending-terms")]
    public async Task<IActionResult> Confirm(string ticketId, ConfirmRequest req, CancellationToken ct)
    {
        if (user.EmpNo is not { } empNo) return Forbid();
        var me = await identity.GetAsync(empNo, Request.Headers.Authorization.ToString()["Bearer ".Length..].Trim(), ct);
        var t = await db.Tickets.FirstOrDefaultAsync(x => x.Id == ticketId, ct);
        if (t is null) return NotFound();
        if (!me.IsCimEngineer || t.CimEngineerEmpNo != me.EmpNo) return Forbid();

        if (string.IsNullOrWhiteSpace(req.Term) || string.IsNullOrWhiteSpace(req.Explanation))
            return Problem("確認新名詞必須填寫名詞與說明。", statusCode: 400);
        var term = req.Term.Trim();
        var explanation = req.Explanation.Trim();
        var aliases = (req.Aliases ?? []).Select(a => a.Trim()).Where(a => a.Length > 0).ToArray();
        var issue = await proposals.CreateAsync(new(term, explanation, aliases, ticketId), ct);
        var row = new PendingTerm
        {
            Term = term, Explanation = explanation, AliasesJson = JsonSerializer.Serialize(aliases), TicketId = ticketId,
            GiteaIssueNumber = issue.Number, GiteaIssueUrl = issue.Url, ConfirmedByEmpNo = me.EmpNo, CreatedAt = time.GetUtcNow(),
        };
        db.PendingTerms.Add(row);
        await db.SaveChangesAsync(ct);
        return StatusCode(201, View(row));
    }

    [HttpGet("api/pending-terms")]
    public async Task<IActionResult> List(CancellationToken ct) =>
        Ok((await db.PendingTerms.Where(x => x.Status == PendingTermStatus.Pending).OrderBy(x => x.Id).ToListAsync(ct)).Select(View));
}
