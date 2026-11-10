using System.Net;
using AiAgentStudio.Api.Submissions;
using AiAgentStudio.Api.Tickets;
using Microsoft.Extensions.DependencyInjection;

namespace AiAgentStudio.Api.Tests;

/// <summary>#31：複製為 Markdown（spec 6.6）。測試走 GET /api/tickets/{id}/markdown，資料直接種進 DB。</summary>
public class CopyMarkdownTests
{
    const string Workflow = """
        [
          {"id":"s1","description":"查工單","tool_spec":"ts1","inputs":[{"name":"eqptId","from_user":true}],"outputs":["status"],"needs_approval":false},
          {"id":"s2","description":"寫備註","tool_spec":"ts2","inputs":[{"name":"note","from_step":"s1","from_field":"status"}],"outputs":["ok"],"needs_approval":true},
          {"id":"s3","description":"主管簽核","needs_approval":true}
        ]
        """;

    static async Task<InterviewFactory> Make(string state2 = ToolSpecState.Implemented)
    {
        var f = new InterviewFactory(); await f.Seed();
        using var s = f.Services.CreateScope();
        var db = s.ServiceProvider.GetRequiredService<StudioDb>();
        (await db.Tickets.FindAsync("T-1"))!.Status = TicketStatus.Submitted;
        db.Submissions.Add(new Submission { TicketId = "T-1", Number = 1, WorkflowJson = "[]" });
        db.Submissions.Add(new Submission { TicketId = "T-1", Number = 2, WorkflowJson = Workflow });
        db.ToolSpecRecords.Add(new ToolSpecRecord { TicketId = "T-1", SpecId = "ts1", Kind = "Reuse", State = ToolSpecState.Implemented, CurrentRevision = 1 });
        db.ToolSpecRecords.Add(new ToolSpecRecord { TicketId = "T-1", SpecId = "ts2", Kind = "New", State = state2, CurrentRevision = 1 });
        db.ToolSpecRevisions.Add(new ToolSpecRevision { TicketId = "T-1", SpecId = "ts1", Revision = 1, SubmissionNumber = 1, ContentJson = """{"kind":"Reuse","ref":{"server":"gw","tool":"get_workorder"}}""" });
        db.ToolSpecRevisions.Add(new ToolSpecRevision { TicketId = "T-1", SpecId = "ts2", Revision = 1, SubmissionNumber = 2, ContentJson = """{"kind":"New","expected_tool_name":"add_note","system":"MES"}""" });
        await db.SaveChangesAsync();
        return f;
    }

    [Fact]
    public async Task Completed_ticket_markdown_has_each_step_with_server_key_tool_name_and_manual_marker()
    {
        using var f = await Make();
        var r = await f.As("SSO1").GetAsync("/api/tickets/T-1/markdown"); // 任何可檢視者
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var md = await r.Content.ReadAsStringAsync();
        Assert.Contains("`gw`", md);
        Assert.Contains("`get_workorder`", md);
        Assert.Contains("`add_note`", md);
        Assert.Contains("查工單", md);
        Assert.Contains("使用者輸入", md);
        Assert.Contains("s1.status", md);       // 輸入來源：前面某一步的 output 欄位
        Assert.Contains("需要人工核准：是", md);
        Assert.Contains("人工步驟", md);        // s3 無 Tool Spec
    }

    [Fact]
    public async Task Ticket_not_completed_returns_409()
    {
        using var f = await Make(ToolSpecState.Pending);
        Assert.Equal(HttpStatusCode.Conflict, (await f.As("CIM1").GetAsync("/api/tickets/T-1/markdown")).StatusCode);
    }
}
