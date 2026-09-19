using IssueAgent.Domain;
using IssueAgent.Providers;

namespace IssueAgent.Workflow;

/// <summary>Why a label snapshot cannot be interpreted unambiguously (specification §19). The
/// workflow must not guess when one of these is set; it stays/goes waiting and notifies.</summary>
public enum LabelAmbiguity
{
    None,
    MissingPhaseLabel,
    MultiplePhaseLabels,
    MissingStateLabel,
    MultipleStateLabels,
    MultipleCommandLabels,
}

/// <summary>The parsed three label dimensions for one managed issue or PR/MR at a point in time.</summary>
public sealed record LabelSnapshot(
    WorkflowPhase? Phase,
    WorkflowOperationalState? OperationalState,
    IReadOnlyList<WorkflowCommand> Commands,
    LabelAmbiguity Ambiguity)
{
    public bool IsAmbiguous => Ambiguity != LabelAmbiguity.None;

    /// <summary>Whether only the phase/state dimensions are ambiguous. Human command labels are
    /// parsed independently so a correctly placed command can still be admitted when a request
    /// carries no phase labels.</summary>
    public bool IsPhaseStateAmbiguous =>
        Ambiguity is LabelAmbiguity.MissingPhaseLabel or LabelAmbiguity.MultiplePhaseLabels
            or LabelAmbiguity.MissingStateLabel or LabelAmbiguity.MultipleStateLabels;

    public WorkflowCommand? SingleCommand =>
        Commands.Count == 1 ? Commands[0] : null;
}

/// <summary>Parses and computes deltas for the three independent label dimensions IssueAgent
/// manages: phase, state, and transient human commands. Phase/state labels are owned by
/// IssueAgent; humans interact via command labels and comments.</summary>
public static class LabelProtocol
{
    public static LabelSnapshot Analyze(IReadOnlyCollection<string> labels)
    {
        ArgumentNullException.ThrowIfNull(labels);

        var phases = new List<WorkflowPhase>();
        var states = new List<WorkflowOperationalState>();
        var commands = new List<WorkflowCommand>();

        foreach (var label in labels)
        {
            if (TryParsePhase(label, out var phase))
            {
                phases.Add(phase);
            }
            else if (TryParseState(label, out var state))
            {
                states.Add(state);
            }
            else if (WorkflowCommandLabels.TryParse(label, out var command))
            {
                commands.Add(command);
            }
        }

        var ambiguity = LabelAmbiguity.None;
        if (phases.Count == 0)
        {
            ambiguity = LabelAmbiguity.MissingPhaseLabel;
        }
        else if (phases.Count > 1)
        {
            ambiguity = LabelAmbiguity.MultiplePhaseLabels;
        }
        else if (states.Count == 0)
        {
            ambiguity = LabelAmbiguity.MissingStateLabel;
        }
        else if (states.Count > 1)
        {
            ambiguity = LabelAmbiguity.MultipleStateLabels;
        }
        else if (commands.Count > 1)
        {
            ambiguity = LabelAmbiguity.MultipleCommandLabels;
        }

        return new LabelSnapshot(
            phases.Count == 1 ? phases[0] : null,
            states.Count == 1 ? states[0] : null,
            commands,
            ambiguity);
    }

    /// <summary>Computes the label add/remove set to transition phase/state labels to the target
    /// values and consume the given accepted command labels, leaving any other labels untouched.</summary>
    public static (IReadOnlyList<string> ToAdd, IReadOnlyList<string> ToRemove) ComputeTransition(
        IReadOnlyCollection<string> currentLabels,
        WorkflowPhase targetPhase,
        WorkflowOperationalState targetOperationalState,
        IReadOnlyCollection<WorkflowCommand> commandsToConsume)
    {
        ArgumentNullException.ThrowIfNull(currentLabels);
        ArgumentNullException.ThrowIfNull(commandsToConsume);

        var toAdd = new List<string>();
        var toRemove = new List<string>();

        var targetPhaseLabel = WorkflowLabels.Phase(targetPhase);
        foreach (var label in currentLabels)
        {
            if (TryParsePhase(label, out _) && label != targetPhaseLabel)
            {
                toRemove.Add(label);
            }
        }

        if (!currentLabels.Contains(targetPhaseLabel))
        {
            toAdd.Add(targetPhaseLabel);
        }

        var targetStateLabel = WorkflowLabels.State(targetOperationalState);
        foreach (var label in currentLabels)
        {
            if (TryParseState(label, out _) && label != targetStateLabel)
            {
                toRemove.Add(label);
            }
        }

        if (!currentLabels.Contains(targetStateLabel))
        {
            toAdd.Add(targetStateLabel);
        }

        foreach (var command in commandsToConsume)
        {
            var commandLabel = CommandLabel(command);
            if (currentLabels.Contains(commandLabel))
            {
                toRemove.Add(commandLabel);
            }
        }

        return (toAdd, toRemove);
    }

    private static string CommandLabel(WorkflowCommand command) => command switch
    {
        WorkflowCommand.Replan => WorkflowCommandLabels.Replan,
        WorkflowCommand.Implement => WorkflowCommandLabels.Implement,
        WorkflowCommand.Revise => WorkflowCommandLabels.Revise,
        WorkflowCommand.Continue => WorkflowCommandLabels.Continue,
        WorkflowCommand.Cancel => WorkflowCommandLabels.Cancel,
        _ => throw new ArgumentOutOfRangeException(nameof(command), command, null),
    };

    private static bool TryParsePhase(string label, out WorkflowPhase phase)
    {
        foreach (var candidate in Enum.GetValues<WorkflowPhase>())
        {
            if (label == WorkflowLabels.Phase(candidate))
            {
                phase = candidate;
                return true;
            }
        }

        phase = default;
        return false;
    }

    private static bool TryParseState(string label, out WorkflowOperationalState state)
    {
        foreach (var candidate in Enum.GetValues<WorkflowOperationalState>())
        {
            if (label == WorkflowLabels.State(candidate))
            {
                state = candidate;
                return true;
            }
        }

        state = default;
        return false;
    }
}
