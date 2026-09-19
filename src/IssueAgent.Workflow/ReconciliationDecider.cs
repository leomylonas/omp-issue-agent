using IssueAgent.Context;
using IssueAgent.Domain;

namespace IssueAgent.Workflow;

public enum ReconciliationAction
{
    ResumeAutomatically,
    WaitForHuman,
}

public sealed record ReconciliationDecision(ReconciliationAction Action, WaitingReason? Reason, string Explanation);

/// <summary>Snapshot of durable remote and observed local state used to decide whether a workflow
/// may resume automatically after a restart (specification §26).</summary>
public sealed record ReconciliationInput(
    LabelSnapshot RemoteLabels,
    CanonicalCommentContent? RemoteCanonicalComment,
    bool LocalWorktreeExists,
    string? LocalWorktreeHeadCommit,
    string? RemoteBranchHeadCommit,
    bool LocalWorktreeDirty = false);

/// <summary>
/// Decides whether restart reconciliation may resume a workflow automatically or must wait for a
/// human. Resumption requires the remote labels and canonical comment to agree unambiguously, and
/// requires that a persisted "working" state is never blindly resumed, since a process restart
/// while actively working means the prior attempt was interrupted mid-operation and its safety
/// cannot be assumed. Pure decision logic; callers perform the actual reconciliation I/O.
/// </summary>
public static class ReconciliationDecider
{
    public static ReconciliationDecision Decide(ReconciliationInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (input.RemoteLabels.IsAmbiguous)
        {
            return Wait(WaitingReason.AmbiguousCommand, DescribeLabelAmbiguity(input.RemoteLabels.Ambiguity));
        }

        if (input.RemoteCanonicalComment is null)
        {
            return Wait(WaitingReason.CorruptState, "No parseable canonical comment was found for this workflow; state cannot be safely reconstructed.");
        }

        var document = input.RemoteCanonicalComment.State;
        if (!string.Equals(document.Phase, KebabCase(input.RemoteLabels.Phase!.Value.ToString()), StringComparison.Ordinal))
        {
            return Wait(WaitingReason.CorruptState, "The phase label and the canonical comment's recorded phase disagree.");
        }

        if (!string.Equals(document.State, KebabCase(input.RemoteLabels.OperationalState!.Value.ToString()), StringComparison.Ordinal))
        {
            return Wait(WaitingReason.CorruptState, "The state label and the canonical comment's recorded operational state disagree.");
        }

        if (string.Equals(document.State, "working", StringComparison.Ordinal))
        {
            return Wait(WaitingReason.ManualIntervention, "The workflow was actively working when the process stopped; its in-flight operation cannot be safely resumed automatically.");
        }

        if (!input.LocalWorktreeExists)
        {
            return Wait(WaitingReason.ManualIntervention, "The retained workflow worktree is missing; IssueAgent will not recreate or discard state without human acknowledgement.");
        }

        if (input.LocalWorktreeDirty)
        {
            return Wait(WaitingReason.ManualIntervention, "The retained worktree contains uncommitted changes from an interrupted attempt; they were preserved for human review.");
        }

        if (input.RemoteLabels.Phase!.Value is WorkflowPhase.Review or WorkflowPhase.Revising && input.RemoteBranchHeadCommit is null)
        {
            return Wait(WaitingReason.RemoteHistoryRewrite, "The workflow is in a published phase (review/revising) but its remote branch no longer exists; publication may have been reverted or deleted externally.");
        }

        if (input.LocalWorktreeExists && input.RemoteBranchHeadCommit is not null &&
            !string.Equals(input.LocalWorktreeHeadCommit, input.RemoteBranchHeadCommit, StringComparison.Ordinal))
        {
            return Wait(WaitingReason.RemoteHistoryRewrite, "The retained worktree's history no longer matches the remote branch; it may have been force-pushed or reset externally.");
        }

        return new ReconciliationDecision(ReconciliationAction.ResumeAutomatically, null, "Remote and local state are consistent and unambiguous.");
    }

    private static ReconciliationDecision Wait(WaitingReason reason, string explanation) => new(ReconciliationAction.WaitForHuman, reason, explanation);

    private static string DescribeLabelAmbiguity(LabelAmbiguity ambiguity) => ambiguity switch
    {
        LabelAmbiguity.MissingPhaseLabel => "No phase label is present.",
        LabelAmbiguity.MultiplePhaseLabels => "Multiple conflicting phase labels are present.",
        LabelAmbiguity.MissingStateLabel => "No state label is present.",
        LabelAmbiguity.MultipleStateLabels => "Multiple conflicting state labels are present.",
        LabelAmbiguity.MultipleCommandLabels => "Multiple conflicting command labels are present.",
        _ => "Label state is ambiguous.",
    };

    private static string KebabCase(string pascalCase)
    {
        if (pascalCase.Length == 0)
        {
            return pascalCase;
        }

        var builder = new System.Text.StringBuilder();
        for (var i = 0; i < pascalCase.Length; i++)
        {
            var c = pascalCase[i];
            if (i > 0 && char.IsUpper(c))
            {
                builder.Append('-');
            }

            builder.Append(char.ToLowerInvariant(c));
        }

        return builder.ToString();
    }
}
