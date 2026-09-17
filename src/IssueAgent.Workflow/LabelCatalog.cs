using IssueAgent.Domain;
using IssueAgent.Providers;

namespace IssueAgent.Workflow;

/// <summary>Canonical descriptions/colors for every label IssueAgent owns. Used to create missing
/// labels automatically (specification §19); never overwrites a user-customized existing label.</summary>
public static class LabelCatalog
{
    public static IReadOnlyList<ProviderLabel> All { get; } = BuildAll();

    private static List<ProviderLabel> BuildAll()
    {
        var labels = new List<ProviderLabel>();

        foreach (var phase in Enum.GetValues<WorkflowPhase>())
        {
            labels.Add(new ProviderLabel(WorkflowLabels.Phase(phase), PhaseColor(phase), $"IssueAgent workflow phase: {phase}"));
        }

        labels.Add(new ProviderLabel(WorkflowLabels.WorkingState, "1d76db", "IssueAgent is actively working"));
        labels.Add(new ProviderLabel(WorkflowLabels.WaitingState, "fbca04", "IssueAgent is waiting for human input"));

        labels.Add(new ProviderLabel(WorkflowCommandLabels.Replan, "5319e7", "Ask IssueAgent to revise the current plan"));
        labels.Add(new ProviderLabel(WorkflowCommandLabels.Implement, "5319e7", "Approve the current plan for implementation"));
        labels.Add(new ProviderLabel(WorkflowCommandLabels.Revise, "5319e7", "Ask IssueAgent to revise the implementation"));
        labels.Add(new ProviderLabel(WorkflowCommandLabels.Continue, "5319e7", "Tell IssueAgent to proceed past a blocker"));
        labels.Add(new ProviderLabel(WorkflowCommandLabels.Cancel, "b60205", "Cancel IssueAgent's work on this item"));

        return labels;
    }

    private static string PhaseColor(WorkflowPhase phase) => phase switch
    {
        WorkflowPhase.Planning => "c5def5",
        WorkflowPhase.Planned => "0e8a16",
        WorkflowPhase.Implementing => "c5def5",
        WorkflowPhase.Review => "0e8a16",
        WorkflowPhase.Revising => "c5def5",
        WorkflowPhase.Done => "0e8a16",
        WorkflowPhase.Failed => "b60205",
        WorkflowPhase.Cancelled => "ededed",
        _ => throw new ArgumentOutOfRangeException(nameof(phase), phase, null),
    };
}
