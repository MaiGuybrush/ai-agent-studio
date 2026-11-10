using AiAgentStudio.Api.Submissions;
using AiAgentStudio.Api.Tickets;

namespace AiAgentStudio.Api.Tests;

/// <summary>需求單狀態推導（spec 5.2）：純函式，直接測。#26／#28 加入可行／不可行時只需餵新的 Tool Spec 狀態。</summary>
public class TicketStatusDerivationTests
{
    const string S = TicketStatus.Submitted;
    static string D(string stored, params string[] specs) => TicketStatusDeriver.Derive(stored, specs);

    [Fact] public void Interviewing_is_stored_and_wins_over_spec_states() => Assert.Equal(TicketStatus.Interviewing, D(TicketStatus.Interviewing, ToolSpecState.Implemented));
    [Fact] public void Cancelled_is_stored_and_wins_over_spec_states() => Assert.Equal(TicketStatus.Cancelled, D(TicketStatus.Cancelled, ToolSpecState.Implemented));

    [Theory]
    [InlineData(ToolSpecState.Pending)]
    [InlineData(ToolSpecState.Infeasible)]
    [InlineData(ToolSpecState.Conditional)]
    public void Any_pending_infeasible_or_unaccepted_conditional_spec_means_evaluating(string blocking)
    {
        Assert.Equal(TicketStatus.Evaluating, D(S, blocking));
        Assert.Equal(TicketStatus.Evaluating, D(S, ToolSpecState.Implemented, ToolSpecState.Feasible, blocking));
    }

    [Theory]
    [InlineData(ToolSpecState.Feasible)]
    [InlineData(ToolSpecState.ConditionAccepted)]
    [InlineData(ToolSpecState.Implementing)]
    public void All_feasible_but_not_all_implemented_means_implementing(string state)
    {
        Assert.Equal(TicketStatus.Implementing, D(S, state));
        Assert.Equal(TicketStatus.Implementing, D(S, ToolSpecState.Implemented, state, ToolSpecState.Feasible));
    }

    [Fact] public void All_implemented_means_completed() => Assert.Equal(TicketStatus.Completed, D(S, ToolSpecState.Implemented, ToolSpecState.Implemented));
    [Fact] public void Reuse_only_ticket_is_completed_right_after_submit() => Assert.Equal(TicketStatus.Completed, D(S, ToolSpecState.Implemented));
    [Fact] public void Unknown_state_is_rejected() => Assert.Throws<ArgumentException>(() => D(S, "Bogus"));

    [Fact]
    public void Labels_are_chinese_status_names() =>
        Assert.Equal(["訪談中", "已取消", "評估中", "實作中", "已完成"],
            new[] { TicketStatus.Interviewing, TicketStatus.Cancelled, TicketStatus.Evaluating, TicketStatus.Implementing, TicketStatus.Completed }.Select(TicketStatus.Label));
}
