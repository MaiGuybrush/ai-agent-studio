using System;
using System.Collections.Generic;
using System.Linq;
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
}
