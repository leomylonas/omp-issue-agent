using IssueAgent.Context;
using IssueAgent.Domain;
using IssueAgent.Workflow;

namespace IssueAgent.Host;

[Flags]
public enum WorkflowCommandSource
{
    None = 0,
    Issue = 1,
    MergeRequest = 2,
}

public sealed record WorkflowCommandResolution(
    WorkflowCommand? Command,
    WorkflowCommandSource Sources,
    bool IsAmbiguous);

/// <summary>Combines command labels from an issue and its linked merge request without guessing
/// when either surface contains conflicting intent.</summary>
public static class WorkflowCommandRouting
{
    public static WorkflowCommandResolution Resolve(LabelSnapshot issue, LabelSnapshot? mergeRequest)
    {
        ArgumentNullException.ThrowIfNull(issue);

        if (issue.HasConflictingCommands || mergeRequest?.HasConflictingCommands == true)
        {
            return new WorkflowCommandResolution(null, WorkflowCommandSource.None, IsAmbiguous: true);
        }

        var issueCommand = issue.SingleCommand;
        var mergeRequestCommand = mergeRequest?.SingleCommand;
        if (mergeRequestCommand is not (WorkflowCommand.Continue or WorkflowCommand.Revise or WorkflowCommand.Cancel))
        {
            mergeRequestCommand = null;
        }
        if (issueCommand is not null && mergeRequestCommand is not null && issueCommand != mergeRequestCommand)
        {
            return new WorkflowCommandResolution(null, WorkflowCommandSource.None, IsAmbiguous: true);
        }

        var command = issueCommand ?? mergeRequestCommand;
        var sources = (issueCommand is null ? WorkflowCommandSource.None : WorkflowCommandSource.Issue) |
            (mergeRequestCommand is null ? WorkflowCommandSource.None : WorkflowCommandSource.MergeRequest);
        return new WorkflowCommandResolution(command, sources, IsAmbiguous: false);
    }

    public static WorkflowCommand? ContinueRoute(WorkflowState state, WorkflowState durableState)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(durableState);

        return state.Phase switch
        {
            WorkflowPhase.Review => null,
            WorkflowPhase.Planning when durableState.Phase == WorkflowPhase.Planning &&
                durableState.OperationalState == WorkflowOperationalState.Working => WorkflowCommand.Replan,
            WorkflowPhase.Planning => null,
            WorkflowPhase.Revising when durableState.Phase == WorkflowPhase.Revising &&
                durableState.OperationalState == WorkflowOperationalState.Working => WorkflowCommand.Revise,
            WorkflowPhase.Revising => null,
            _ => WorkflowCommand.Implement,
        };
    }
}
