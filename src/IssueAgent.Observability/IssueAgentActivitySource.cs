using System.Diagnostics;

namespace IssueAgent.Observability;

/// <summary>Each discrete operation gets its own trace; <c>WorkflowId</c> correlates traces across
/// days of human review (specification §30). Explicit spans cover Git fetch/worktree/submodules/push,
/// LFS, OMP plan/implement/revise, and reconciliation.</summary>
public static class IssueAgentActivitySource
{
    public const string Name = "IssueAgent";

    public static readonly ActivitySource Source = new(Name, "1.0.0");

    public static Activity? StartGitOperation(string operation, string repositoryId, string? workflowId = null) =>
        Start($"git.{operation}", repositoryId, workflowId);

    public static Activity? StartLfsOperation(string operation, string repositoryId, string? workflowId = null) =>
        Start($"lfs.{operation}", repositoryId, workflowId);

    public static Activity? StartOmpOperation(string operation, string? sessionId, string? workflowId = null)
    {
        var effectiveWorkflowId = workflowId ?? Activity.Current?.GetTagItem(LogContextFields.WorkflowId) as string;
        var activity = Source.StartActivity($"omp.{operation}", ActivityKind.Client);
        if (activity is null)
        {
            return null;
        }

        if (sessionId is not null)
        {
            activity.SetTag(LogContextFields.OmpSessionId, sessionId);
        }

        if (effectiveWorkflowId is not null)
        {
            activity.SetTag(LogContextFields.WorkflowId, effectiveWorkflowId);
        }

        return activity;
    }

    /// <summary>Starts the top-level ambient span for one dispatched unit of work (specification
    /// §29, §30). Every span started later in the same async call chain — Git, LFS, OMP,
    /// reconciliation — becomes a child of this one via <see cref="Activity.Current"/> and inherits
    /// its <c>WorkflowId</c> tag once <see cref="Activity.SetTag"/> adds it, so a single dispatch's
    /// full trace and every structured log line emitted during it share one <c>CorrelationId</c>.</summary>
    public static Activity? StartDispatch(string kind, string provider, string repository, long issueNumber)
    {
        var activity = Source.StartActivity($"dispatch.{kind}", ActivityKind.Internal);
        if (activity is null)
        {
            return null;
        }

        activity.SetTag(LogContextFields.CorrelationId, activity.TraceId.ToString());
        activity.SetTag(LogContextFields.Provider, provider);
        activity.SetTag(LogContextFields.Repository, repository);
        activity.SetTag(LogContextFields.IssueNumber, issueNumber);
        return activity;
    }

    public static Activity? StartReconciliation(string? workflowId) =>
        Start("reconciliation", repositoryId: null, workflowId);

    private static Activity? Start(string operationName, string? repositoryId, string? workflowId)
    {
        var effectiveWorkflowId = workflowId ?? Activity.Current?.GetTagItem(LogContextFields.WorkflowId) as string;
        var activity = Source.StartActivity(operationName, ActivityKind.Internal);
        if (activity is null)
        {
            return null;
        }

        if (repositoryId is not null)
        {
            activity.SetTag(LogContextFields.Repository, repositoryId);
        }

        if (effectiveWorkflowId is not null)
        {
            activity.SetTag(LogContextFields.WorkflowId, effectiveWorkflowId);
        }

        return activity;
    }
}
