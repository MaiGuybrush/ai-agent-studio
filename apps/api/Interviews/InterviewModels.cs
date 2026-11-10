using System;

namespace AiAgentStudio.Api.Interviews;

/// <summary>一次訪談（spec 13）。重開 Interview 時會再開一筆新的（#17 只建第一筆）。</summary>
public class Interview
{
    public long Id { get; set; }
    public string TicketId { get; set; } = "";
    public string? ReopenReason { get; set; }
    public string OpeningContext { get; set; } = "";
    /// <summary>pydantic-ai 的 ModelMessage 陣列（JSONB）；C# 不解讀內容，只負責存取與顯示。</summary>
    public string ConversationJson { get; set; } = "[]";
    public bool IsRunning { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>每個 run 一筆用量紀錄（spec 11）。沒收到最後事件時用量為 null（未知）。</summary>
public class AgentRun
{
    public long Id { get; set; }
    public string RunId { get; set; } = "";
    public string TicketId { get; set; } = "";
    public string EmpNo { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public string Result { get; set; } = "";   // 完成／取消／錯誤
    public string? Model { get; set; }
    public long? InputTokens { get; set; }
    public long? OutputTokens { get; set; }
    public int? Requests { get; set; }
}
