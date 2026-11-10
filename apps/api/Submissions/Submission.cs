using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using AiAgentStudio.Api.Tickets;

namespace AiAgentStudio.Api.Submissions;

/// <summary>Tool Spec 的評估／實作狀態（spec 5.1）。#26／#28 會產生 Feasible／Infeasible／Conditional／ConditionAccepted／Implementing，推導規則已涵蓋。</summary>
public static class ToolSpecState
{
    public const string Pending = "Pending";                     // 待評估
    public const string Feasible = "Feasible";                   // 可行
    public const string Infeasible = "Infeasible";               // 不可行
    public const string Conditional = "Conditional";             // 附條件（尚未接受）
    public const string ConditionAccepted = "ConditionAccepted"; // 附條件，已接受（視同可行）
    public const string Implementing = "Implementing";           // 實作中
    public const string Implemented = "Implemented";             // 已實作
    public static readonly string[] All = [Pending, Feasible, Infeasible, Conditional, ConditionAccepted, Implementing, Implemented];
}

/// <summary>需求單狀態推導（spec 5.2）。只有訪談中與已取消存 DB，其餘在查詢時由 Tool Spec 狀態算出。</summary>
public static class TicketStatusDeriver
{
    public static string Derive(string storedStatus, IEnumerable<string> specStates)
    {
        if (storedStatus is TicketStatus.Interviewing or TicketStatus.Cancelled) return storedStatus;
        var s = specStates.ToList();
        if (s.Find(x => !ToolSpecState.All.Contains(x)) is { } bad) throw new ArgumentException($"未知的 Tool Spec 狀態 '{bad}'。");
        if (s.Any(x => x is ToolSpecState.Pending or ToolSpecState.Infeasible or ToolSpecState.Conditional)) return TicketStatus.Evaluating;
        return s.All(x => x == ToolSpecState.Implemented) ? TicketStatus.Completed : TicketStatus.Implementing;
    }
}

/// <summary>
/// 已實作的 Tool Spec 不重檢（spec 7.3）：狀態為已實作、且草稿內容與凍結的 revision 相同者，以 interface_snapshot 為準。
/// 重開 Interview 後被改動的已實作 spec 不在此列（送出時會變成新 revision，要重新檢查）。
/// </summary>
public static class FrozenSpecs
{
    static JsonObject Norm(JsonNode n)
    {
        var c = (JsonObject)n.DeepClone(); c.Remove("id"); c.Remove("naming_check"); c.Remove("system_owner_name");
        return c;
    }

    public static async Task<HashSet<string>> IdsAsync(StudioDb db, string ticketId, IEnumerable<JsonObject> draftSpecs, CancellationToken ct)
    {
        var recs = await db.ToolSpecRecords.AsNoTracking().Where(r => r.TicketId == ticketId && r.State == ToolSpecState.Implemented).ToListAsync(ct);
        var res = new HashSet<string>();
        if (recs.Count == 0) return res;
        var revs = await db.ToolSpecRevisions.AsNoTracking().Where(r => r.TicketId == ticketId).ToListAsync(ct);
        foreach (var s in draftSpecs)
        {
            var id = s["id"]!.GetValue<string>();
            var rec = recs.FirstOrDefault(r => r.SpecId == id);
            var rev = rec is null ? null : revs.FirstOrDefault(v => v.SpecId == id && v.Revision == rec.CurrentRevision);
            if (rev is not null && JsonNode.DeepEquals(Norm(s), Norm(JsonNode.Parse(rev.ContentJson)!))) res.Add(id);
        }
        return res;
    }
}

/// <summary>submission（spec 13）：每次送出一筆，含 Workflow Definition 快照；Number 為該單內的送出編號，Tool Spec Revision 共用。</summary>
public class Submission
{
    public long Id { get; set; }
    public string TicketId { get; set; } = "";
    public int Number { get; set; }
    public string WorkflowJson { get; set; } = "[]";
    public string SubmittedByEmpNo { get; set; } = "";
    public DateTimeOffset At { get; set; }
}

/// <summary>tool_spec_revision（spec 6.5）：不可變的 Tool Spec 內容。重開後內容沒改的 spec 不產生新 revision。</summary>
public class ToolSpecRevision
{
    public long Id { get; set; }
    public string TicketId { get; set; } = "";
    public string SpecId { get; set; } = "";
    public int Revision { get; set; }
    public int SubmissionNumber { get; set; }
    public string ContentJson { get; set; } = "{}";
}

/// <summary>tool_spec（spec 13）：每份 Tool Spec 的評估／實作狀態與目前的 revision。草稿本身在 workflow_draft。</summary>
public class ToolSpecRecord
{
    public string TicketId { get; set; } = "";
    public string SpecId { get; set; } = "";
    public string Kind { get; set; } = "";
    public string State { get; set; } = ToolSpecState.Pending;
    public int CurrentRevision { get; set; }
    /// <summary>CIM Engineer 已處理評估結果（接受條件、修改或刪除；#28 負責寫入）。為 true 後 System Owner 不能再修改回覆（spec 9）。</summary>
    public bool Handled { get; set; }
    /// <summary>CIM Engineer 接受的條件（spec 6.2 選填「已接受的條件」）；新 revision 時清除。</summary>
    public string? AcceptedCondition { get; set; }
}

/// <summary>interface_snapshot（spec 6.4、13）：Interface Compliance Check 通過時的實際介面。已實作的 Tool Spec 以此為準，不再重檢 Naming Check。</summary>
public class InterfaceSnapshot
{
    public long Id { get; set; }
    public string TicketId { get; set; } = "";
    public string SpecId { get; set; } = "";
    /// <summary>通過檢查時比對的 Tool Spec revision。</summary>
    public int Revision { get; set; }
    public string ServerKey { get; set; } = "";
    public string ToolName { get; set; } = "";
    public string InputSchemaJson { get; set; } = "{}";
    public string? OutputSchemaJson { get; set; }
    public string CheckedByEmpNo { get; set; } = "";
    public DateTimeOffset At { get; set; }
}

/// <summary>feasibility_review（spec 13）：綁定 revision 的回覆；每次回覆或修改新增一筆，保留歷史。</summary>
public class FeasibilityReview
{
    public long Id { get; set; }
    public string TicketId { get; set; } = "";
    public string SpecId { get; set; } = "";
    public int Revision { get; set; }
    /// <summary>ToolSpecState.Feasible／Infeasible／Conditional。</summary>
    public string Result { get; set; } = "";
    public string? Reason { get; set; }
    public string? Condition { get; set; }
    /// <summary>預估工時（人日）；不可行時為 null。</summary>
    public decimal? EstimatedDays { get; set; }
    public string ReviewerEmpNo { get; set; } = "";
    public DateTimeOffset At { get; set; }
}
