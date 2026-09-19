using System.Diagnostics;

namespace IssueAgent.Observability.Tests;

/// <summary>Proves the M16 fix: <see cref="IssueAgentActivitySource.StartDispatch"/> establishes a
/// real ambient <see cref="Activity"/> so <c>CorrelationId</c> is genuinely populated, and every
/// child span started later in the same call chain inherits <c>WorkflowId</c> once it is set on the
/// dispatch activity, without every caller needing to pass it explicitly.</summary>
public sealed class IssueAgentActivitySourceTests : IDisposable
{
    private readonly ActivityListener listener = new()
    {
        ShouldListenTo = source => source.Name == IssueAgentActivitySource.Name,
        Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
    };

    public IssueAgentActivitySourceTests() => ActivitySource.AddActivityListener(listener);

    [Fact]
    public void StartDispatchPopulatesCorrelationIdFromTheRealTraceId()
    {
        using var activity = IssueAgentActivitySource.StartDispatch("existing", "github", "octo/widgets", 7);

        Assert.NotNull(activity);
        var correlationId = activity!.GetTagItem(LogContextFields.CorrelationId) as string;
        Assert.False(string.IsNullOrEmpty(correlationId));
        Assert.Equal(activity.TraceId.ToString(), correlationId);
    }

    [Fact]
    public void ChildSpanInheritsWorkflowIdSetOnTheAmbientDispatchActivityAfterItStarted()
    {
        using var dispatch = IssueAgentActivitySource.StartDispatch("existing", "github", "octo/widgets", 7);
        Assert.NotNull(dispatch);

        // Mirrors WorkflowDispatcher: WorkflowId becomes known only after reconciliation resolves
        // state, strictly after the dispatch activity is already the ambient Activity.Current.
        dispatch!.SetTag(LogContextFields.WorkflowId, "11111111-1111-1111-1111-111111111111");

        using var gitSpan = IssueAgentActivitySource.StartGitOperation("fetch", "octo/widgets");
        using var ompSpan = IssueAgentActivitySource.StartOmpOperation("run", sessionId: "session-1");
        using var reconciliationSpan = IssueAgentActivitySource.StartReconciliation(workflowId: null);

        Assert.Equal("11111111-1111-1111-1111-111111111111", gitSpan?.GetTagItem(LogContextFields.WorkflowId));
        Assert.Equal("11111111-1111-1111-1111-111111111111", ompSpan?.GetTagItem(LogContextFields.WorkflowId));
        Assert.Equal("11111111-1111-1111-1111-111111111111", reconciliationSpan?.GetTagItem(LogContextFields.WorkflowId));
    }

    [Fact]
    public void ExplicitWorkflowIdOverridesTheInheritedAmbientValue()
    {
        using var dispatch = IssueAgentActivitySource.StartDispatch("existing", "github", "octo/widgets", 7);
        dispatch!.SetTag(LogContextFields.WorkflowId, "ambient-workflow");

        using var gitSpan = IssueAgentActivitySource.StartGitOperation("fetch", "octo/widgets", workflowId: "explicit-workflow");

        Assert.Equal("explicit-workflow", gitSpan?.GetTagItem(LogContextFields.WorkflowId));
    }

    [Fact]
    public void ChildSpanHasNoWorkflowIdTagWhenNoAmbientDispatchActivityExists()
    {
        using var gitSpan = IssueAgentActivitySource.StartGitOperation("fetch", "octo/widgets");

        Assert.Null(gitSpan?.GetTagItem(LogContextFields.WorkflowId));
    }

    public void Dispose() => listener.Dispose();
}
