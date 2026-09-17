namespace IssueAgent.Domain;

/// <summary>Stable identifier for an issue workflow across its full lifecycle.</summary>
public readonly record struct WorkflowId(Guid Value)
{
    public static WorkflowId New() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString("D");
}

/// <summary>Durable milestone represented by the canonical IssueAgent comment and labels.</summary>
public enum WorkflowPhase
{
    Planning,
    Planned,
    Implementing,
    Review,
    Revising,
    Done,
    Failed,
    Cancelled,
}

/// <summary>Operational status of an actively managed workflow.</summary>
public enum WorkflowOperationalState
{
    Working,
    Waiting,
}

/// <summary>Human action or safety condition that requires an explicit decision.</summary>
public enum WaitingReason
{
    PlanApproval,
    ReplanRequired,
    MaterialPlanDeviation,
    NewInputDuringImplementation,
    AmbiguousCommand,
    CorruptState,
    Conflict,
    MissingCredentials,
    RemoteHistoryRewrite,
    ProtectedBranch,
    ManualIntervention,
}

/// <summary>Canonical labels owned by IssueAgent.</summary>
public static class WorkflowLabels
{
    public const string PlanningPhase = "agent:phase:planning";
    public const string PlannedPhase = "agent:phase:planned";
    public const string ImplementingPhase = "agent:phase:implementing";
    public const string ReviewPhase = "agent:phase:review";
    public const string RevisingPhase = "agent:phase:revising";
    public const string DonePhase = "agent:phase:done";
    public const string FailedPhase = "agent:phase:failed";
    public const string CancelledPhase = "agent:phase:cancelled";
    public const string WorkingState = "agent:state:working";
    public const string WaitingState = "agent:state:waiting";

    public static string Phase(WorkflowPhase phase) => phase switch
    {
        WorkflowPhase.Planning => PlanningPhase,
        WorkflowPhase.Planned => PlannedPhase,
        WorkflowPhase.Implementing => ImplementingPhase,
        WorkflowPhase.Review => ReviewPhase,
        WorkflowPhase.Revising => RevisingPhase,
        WorkflowPhase.Done => DonePhase,
        WorkflowPhase.Failed => FailedPhase,
        WorkflowPhase.Cancelled => CancelledPhase,
        _ => throw new ArgumentOutOfRangeException(nameof(phase), phase, null),
    };

    public static string State(WorkflowOperationalState state) => state switch
    {
        WorkflowOperationalState.Working => WorkingState,
        WorkflowOperationalState.Waiting => WaitingState,
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, null),
    };
}

/// <summary>Minimal durable workflow state, independent of any provider transport type.</summary>
public sealed record WorkflowState(
    WorkflowId WorkflowId,
    WorkflowPhase Phase,
    WorkflowOperationalState OperationalState,
    WaitingReason? WaitingReason,
    int PlanRevision,
    int? ApprovedPlanRevision,
    string OmpSessionId,
    string Branch,
    string TargetBranch,
    string BaseCommit,
    DateTimeOffset UpdatedAt)
{
    public void EnsureValid()
    {
        if (PlanRevision < 0)
        {
            throw new InvalidOperationException("Plan revision cannot be negative.");
        }

        if (ApprovedPlanRevision.HasValue && ApprovedPlanRevision.Value > PlanRevision)
        {
            throw new InvalidOperationException("Approved plan revision cannot exceed the current plan revision.");
        }

        if (OperationalState == WorkflowOperationalState.Waiting && WaitingReason is null)
        {
            throw new InvalidOperationException("Waiting workflows must record a waiting reason.");
        }

        if (OperationalState == WorkflowOperationalState.Working && WaitingReason is not null)
        {
            throw new InvalidOperationException("Working workflows cannot record a waiting reason.");
        }
    }
}
