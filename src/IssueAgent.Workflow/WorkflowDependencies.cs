using IssueAgent.Context;
using IssueAgent.Git;
using IssueAgent.Omp;
using IssueAgent.Providers;

namespace IssueAgent.Workflow;

/// <summary>Abstracts <see cref="DateTimeOffset.UtcNow"/> so workflow timestamps are deterministic
/// in tests.</summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

/// <summary>Workflow notification events (specification §28). Notifications are fan-out and
/// independent: a failure in one sink never blocks a workflow transition or another sink.</summary>
public enum WorkflowNotificationKind
{
    PlanReady,
    ImplementationReady,
    HumanActionRequired,
    PlanFailed,
    ImplementationFailed,
    RevisionFailed,
    Cancelled,
}

public sealed record WorkflowNotification(
    WorkflowNotificationKind Kind,
    string RepositoryId,
    long IssueNumber,
    string WorkflowId,
    string Message);

/// <summary>Generic fan-out notification sink consumed by the workflow engine. Concrete sinks
/// (Telegram, Slack) are implemented by the Notifications component.</summary>
public interface IWorkflowNotifier
{
    Task NotifyAsync(WorkflowNotification notification, CancellationToken cancellationToken);
}

/// <summary>No-op notifier for callers that have not configured any notification sink.</summary>
public sealed class NullWorkflowNotifier : IWorkflowNotifier
{
    public Task NotifyAsync(WorkflowNotification notification, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>Collaborators a workflow needs for one repository, excluding the OMP client: OMP owns
/// process lifecycle per session, so callers start/resume an <see cref="IOmpClient"/> bound to the
/// workflow's persisted session id and pass it explicitly into each workflow operation.</summary>
public sealed record WorkflowDependencies(
    IGitProvider Provider,
    IGitRepositoryManager Git,
    AgentContextBuilder ContextBuilder,
    IWorkflowNotifier Notifier,
    IClock Clock);

/// <summary>Per-repository/workspace configuration a workflow needs beyond its collaborators.</summary>
public sealed record WorkflowRepositoryConfig(
    RepositoryRef Repository,
    string RepositoryStoragePath,
    string WorkflowsStoragePath,
    string TargetBranchOverride,
    GitAuthentication GitAuthentication,
    GitIdentity GitIdentity,
    string OmpExecutablePath,
    IReadOnlyList<string> OmpArguments,
    IReadOnlyDictionary<string, string> OmpAllowedEnvironment,
    string PlanningRole,
    string ImplementationRole);
