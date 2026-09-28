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

        var mergeRequestCommands = mergeRequest?.Commands
            .Where(command => command is WorkflowCommand.Continue or WorkflowCommand.Revise or WorkflowCommand.Cancel)
            .ToArray() ?? [];
        WorkflowCommand? mergeRequestCommand = mergeRequestCommands.Length == 1 ? mergeRequestCommands[0] : null;

        if (issue.HasConflictingCommands || mergeRequest?.HasConflictingCommands == true)
        {
            return new WorkflowCommandResolution(null, WorkflowCommandSource.None, IsAmbiguous: true);
        }
        var issueCommand = issue.SingleCommand;
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
            WorkflowPhase.Failed => state.InterruptedPhase switch
            {
                WorkflowPhase.Planning => WorkflowCommand.Replan,
                WorkflowPhase.Revising => WorkflowCommand.Revise,
                WorkflowPhase.Implementing => WorkflowCommand.Implement,
                _ => null,
            },
            WorkflowPhase.Planning when state.InterruptedPhase == WorkflowPhase.Planning => WorkflowCommand.Replan,
            WorkflowPhase.Planning when durableState.Phase == WorkflowPhase.Planning &&
                durableState.OperationalState == WorkflowOperationalState.Working => WorkflowCommand.Replan,
            WorkflowPhase.Planning => null,
            WorkflowPhase.Revising when state.WaitingReason is WaitingReason.MaterialPlanDeviation or
                WaitingReason.NewFeedbackDuringRevision or WaitingReason.RemoteHistoryRewrite or
                WaitingReason.MissingRemoteRevisionBranch => WorkflowCommand.Revise,
            WorkflowPhase.Revising when state.InterruptedPhase == WorkflowPhase.Revising => WorkflowCommand.Revise,
            WorkflowPhase.Revising when durableState.Phase == WorkflowPhase.Revising &&
                durableState.OperationalState == WorkflowOperationalState.Working => WorkflowCommand.Revise,
            WorkflowPhase.Revising => null,
            WorkflowPhase.Implementing when state.WaitingReason is WaitingReason.NewInputDuringImplementation or WaitingReason.MaterialPlanDeviation => WorkflowCommand.Implement,
            WorkflowPhase.Implementing when state.InterruptedPhase == WorkflowPhase.Implementing => WorkflowCommand.Implement,
            WorkflowPhase.Implementing when durableState.Phase == WorkflowPhase.Implementing &&
                durableState.OperationalState == WorkflowOperationalState.Working => WorkflowCommand.Implement,
            WorkflowPhase.Implementing => null,
            _ => null,
        };
    }

    /// <summary>Commands are accepted only at the workflow milestone they control. This prevents
    /// a stale label from restarting an unrelated phase.</summary>
    public static bool IsPhaseCompatible(WorkflowCommand command, WorkflowState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        return command switch
        {
            WorkflowCommand.Replan => state.Phase is WorkflowPhase.Planned or WorkflowPhase.Review,
            WorkflowCommand.Implement => state.Phase == WorkflowPhase.Planned,
            WorkflowCommand.Revise => state.Phase == WorkflowPhase.Review,
            WorkflowCommand.Continue => ContinueRoute(state, state) is not null,
            WorkflowCommand.Cancel => state.Phase is WorkflowPhase.Planning or WorkflowPhase.Planned or
                WorkflowPhase.Implementing or WorkflowPhase.Review or WorkflowPhase.Revising,
            _ => false,
        };
    }
}
