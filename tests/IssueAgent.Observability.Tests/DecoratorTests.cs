using IssueAgent.Git;
using IssueAgent.Omp;
using IssueAgent.Providers;
using IssueAgent.Workflow;
using Microsoft.Extensions.Logging.Abstractions;

namespace IssueAgent.Observability.Tests;

public sealed class ObservableGitRepositoryManagerTests
{
    [Fact]
    public async Task FetchAsyncRecordsSuccessMetricsAndDoesNotThrow()
    {
        using var capture = new MetricCapture();
        using var metrics = new IssueAgentMetrics();
        var decorated = new ObservableGitRepositoryManager(new FakeGitRepositoryManager(), metrics, NullLogger<ObservableGitRepositoryManager>.Instance);

        await decorated.FetchAsync("repo-1", GitAuthentication.Anonymous(TlsTrust.System), CancellationToken.None);

        Assert.Equal(1, capture.CountFor("issueagent.git.operations"));
        Assert.Equal(0, capture.CountFor("issueagent.git.errors"));
    }

    [Fact]
    public async Task FetchAsyncRecordsErrorMetricsAndRethrowsOnFailure()
    {
        using var capture = new MetricCapture();
        using var metrics = new IssueAgentMetrics();
        var inner = new FakeGitRepositoryManager { ThrowOnFetch = true };
        var decorated = new ObservableGitRepositoryManager(inner, metrics, NullLogger<ObservableGitRepositoryManager>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            decorated.FetchAsync("repo-1", GitAuthentication.Anonymous(TlsTrust.System), CancellationToken.None).AsTask());

        Assert.Equal(1, capture.CountFor("issueagent.git.operations"));
        Assert.Equal(1, capture.CountFor("issueagent.git.errors"));
    }

    [Fact]
    public async Task FetchAsyncDoesNotWriteCredentialsFromExceptionToLogs()
    {
        const string secret = "provider-token-super-secret";
        using var metrics = new IssueAgentMetrics();
        var logger = new RecordingLogger<ObservableGitRepositoryManager>();
        var inner = new FakeGitRepositoryManager
        {
            ThrowOnFetch = true,
            FailureMessage = $"Authorization: Bearer {secret}",
        };
        var decorated = new ObservableGitRepositoryManager(inner, metrics, logger);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            decorated.FetchAsync("repo-1", GitAuthentication.Anonymous(TlsTrust.System), CancellationToken.None).AsTask());

        Assert.NotEmpty(logger.Messages);
        Assert.DoesNotContain(secret, string.Join(Environment.NewLine, logger.Messages), StringComparison.Ordinal);
        Assert.Contains(nameof(InvalidOperationException), logger.Messages[^1], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://oauth2:secret-token@git.example/octo/widgets.git", "https://git.example/")]
    [InlineData("https://api.github.com/repos/octo/widgets/issues?access_token=secret-token", "https://api.github.com/")]
    [InlineData("https://user:pw@gitlab.example:8443/api/v4/projects/7", "https://gitlab.example:8443/")]
    public void HttpSpanRedactorStripsCredentialsPathAndQuery(string rawUri, string expectedRedacted)
    {
        var redacted = HttpSpanRedactor.RedactUrl(new Uri(rawUri));

        Assert.Equal(expectedRedacted, redacted);
        Assert.DoesNotContain("secret-token", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("pw", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UploadLfsObjectsAsyncRecordsLfsErrorMetricsAndRethrows()
    {
        using var capture = new MetricCapture();
        using var metrics = new IssueAgentMetrics();
        var decorated = new ObservableGitRepositoryManager(new FakeGitRepositoryManager(), metrics, NullLogger<ObservableGitRepositoryManager>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            decorated.UploadLfsObjectsAsync("repo-1", "/tmp/wt", "agent/issue-1", GitAuthentication.Anonymous(TlsTrust.System), CancellationToken.None).AsTask());

        Assert.Equal(1, capture.CountFor("issueagent.lfs.operations"));
        Assert.Equal(1, capture.CountFor("issueagent.lfs.errors"));
    }

    [Fact]
    public async Task ResolveBranchCommitAsyncPassesThroughResult()
    {
        using var metrics = new IssueAgentMetrics();
        var decorated = new ObservableGitRepositoryManager(new FakeGitRepositoryManager(), metrics, NullLogger<ObservableGitRepositoryManager>.Instance);

        var result = await decorated.ResolveBranchCommitAsync("repo-1", "main", CancellationToken.None);

        Assert.Equal("abc123", result);
    }

    [Fact]
    public async Task FetchAsyncDoesNotRecordAnErrorWhenCancelledByItsOwnToken()
    {
        using var capture = new MetricCapture();
        using var metrics = new IssueAgentMetrics();
        var inner = new CancelingGitRepositoryManager();
        var decorated = new ObservableGitRepositoryManager(inner, metrics, NullLogger<ObservableGitRepositoryManager>.Instance);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            decorated.FetchAsync("repo-1", GitAuthentication.Anonymous(TlsTrust.System), cts.Token).AsTask());

        Assert.Equal(0, capture.CountFor("issueagent.git.errors"));
    }

    [Fact]
    public async Task ResetWorktreeAsyncTagsTheGitSpanWithTheRealRepositoryIdNotUnknown()
    {
        // Regression: worktree-scoped Git spans (as opposed to bare-repository spans, which always
        // had a repositoryId) previously carried the literal "unknown" repository id.
        using var activityListener = new System.Diagnostics.ActivityListener
        {
            ShouldListenTo = source => source.Name == IssueAgentActivitySource.Name,
            Sample = (ref System.Diagnostics.ActivityCreationOptions<System.Diagnostics.ActivityContext> _) => System.Diagnostics.ActivitySamplingResult.AllData,
        };
        System.Diagnostics.Activity.Current = null;
        System.Diagnostics.ActivitySource.AddActivityListener(activityListener);
        System.Diagnostics.Activity? captured = null;
        activityListener.ActivityStopped = activity =>
        {
            if (activity.OperationName == "git.reset-worktree")
            {
                captured = activity;
            }
        };

        using var metrics = new IssueAgentMetrics();
        var decorated = new ObservableGitRepositoryManager(new FakeGitRepositoryManager(), metrics, NullLogger<ObservableGitRepositoryManager>.Instance);

        await decorated.ResetWorktreeAsync("repo-42", "/tmp/wt", "abc123", CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Equal("repo-42", captured!.GetTagItem(LogContextFields.Repository));
    }

    private sealed class CancelingGitRepositoryManager : IGitRepositoryManager
    {
        public ValueTask EnsureBareRepositoryAsync(string repositoryId, string cloneUrl, GitAuthentication authentication, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask FetchAsync(string repositoryId, GitAuthentication authentication, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("Unreachable: token must already be canceled in this test.");
        }
        public ValueTask<string> ResolveBranchCommitAsync(string repositoryId, string branchName, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<string?> TryResolveRemoteBranchCommitAsync(string repositoryId, string branchName, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<bool> IsAncestorAsync(string repositoryId, string ancestorCommit, string descendantCommit, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask CreateWorktreeAsync(string repositoryId, string worktreeId, string worktreePath, string branchName, string baseCommit, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask ResetWorktreeAsync(string repositoryId, string worktreePath, string commit, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<bool> HasUncommittedChangesAsync(string repositoryId, string worktreePath, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<string> GetHeadCommitAsync(string repositoryId, string worktreePath, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask UpdateSubmodulesAsync(string repositoryId, string worktreePath, Func<string, GitAuthentication?> authenticationResolver, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<bool> TryRebaseOntoAsync(string repositoryId, string worktreePath, string ontoCommit, GitIdentity identity, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<bool> TryMergeAsync(string repositoryId, string worktreePath, string commit, GitIdentity identity, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask PushAsync(string repositoryId, string worktreePath, string branchName, GitAuthentication authentication, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask RemoveWorktreeAsync(string repositoryId, string worktreeId, string worktreePath, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask RemoveLocalBranchAsync(string repositoryId, string branchName, CancellationToken cancellationToken) => throw new NotSupportedException();
        public bool WorktreeRequiresLfs(string worktreePath) => throw new NotSupportedException();
        public ValueTask MaterializeLfsContentAsync(string repositoryId, string worktreePath, GitAuthentication authentication, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask UploadLfsObjectsAsync(string repositoryId, string worktreePath, string branchName, GitAuthentication authentication, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}

public sealed class ObservableOmpClientTests
{
    [Fact]
    public async Task RunAsyncPassesThroughAllEventsInOrder()
    {
        using var metrics = new IssueAgentMetrics();
        var decorated = new ObservableOmpClient(new FakeOmpClient(), metrics, NullLogger<ObservableOmpClient>.Instance);
        var request = new OmpRunRequest("session-1", "/tmp", "prompt", new Dictionary<string, string>());

        var events = new List<OmpEvent>();
        await foreach (var e in decorated.RunAsync(request, CancellationToken.None))
        {
            events.Add(e);
        }

        Assert.Collection(events, e => Assert.IsType<OmpMessageEvent>(e), e => Assert.IsType<OmpCompletedEvent>(e));
    }

    [Fact]
    public async Task RunAsyncRecordsErrorMetricsAndRethrowsOnFailure()
    {
        using var capture = new MetricCapture();
        using var metrics = new IssueAgentMetrics();
        var decorated = new ObservableOmpClient(new FakeOmpClient { ThrowDuringRun = true }, metrics, NullLogger<ObservableOmpClient>.Instance);
        var request = new OmpRunRequest("session-1", "/tmp", "prompt", new Dictionary<string, string>());

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in decorated.RunAsync(request, CancellationToken.None))
            {
            }
        });

        Assert.Equal(1, capture.CountFor("issueagent.omp.errors"));
    }

    [Fact]
    public async Task CreateSessionAsyncPassesThroughSession()
    {
        using var metrics = new IssueAgentMetrics();
        var decorated = new ObservableOmpClient(new FakeOmpClient(), metrics, NullLogger<ObservableOmpClient>.Instance);

        var session = await decorated.CreateSessionAsync("plan", CancellationToken.None);

        Assert.Equal("session-1", session.SessionId);
        Assert.Equal("plan", session.Role);
    }

    [Fact]
    public async Task CreateSessionAsyncDoesNotRecordAnErrorWhenCancelledByItsOwnToken()
    {
        using var capture = new MetricCapture();
        using var metrics = new IssueAgentMetrics();
        var decorated = new ObservableOmpClient(new CancelingOmpClient(), metrics, NullLogger<ObservableOmpClient>.Instance);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => decorated.CreateSessionAsync("plan", cts.Token).AsTask());

        Assert.Equal(0, capture.CountFor("issueagent.omp.errors"));
    }

    private sealed class CancelingOmpClient : IOmpClient
    {
        public ValueTask<OmpSession> CreateSessionAsync(string role, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("Unreachable: token must already be canceled in this test.");
        }

        public ValueTask<OmpSession> ResumeSessionAsync(string sessionId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public IAsyncEnumerable<OmpEvent> RunAsync(OmpRunRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask CancelAsync(string sessionId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

public sealed class ObservableWorkflowNotifierTests
{
    [Fact]
    public async Task NotifyAsyncDoesNotRecordFailureOnSuccess()
    {
        using var capture = new MetricCapture();
        using var metrics = new IssueAgentMetrics();
        var decorated = new ObservableWorkflowNotifier(new FakeWorkflowNotifier(), metrics, NullLogger<ObservableWorkflowNotifier>.Instance);

        await decorated.NotifyAsync(new WorkflowNotification(WorkflowNotificationKind.PlanReady, "repo-1", 1, "workflow-1", "ready"), CancellationToken.None);

        Assert.Equal(0, capture.CountFor("issueagent.notifications.failures"));
    }

    [Fact]
    public async Task NotifyAsyncRecordsFailureMetricAndRethrowsOnUnexpectedException()
    {
        using var capture = new MetricCapture();
        using var metrics = new IssueAgentMetrics();
        var decorated = new ObservableWorkflowNotifier(new FakeWorkflowNotifier { ThrowOnNotify = true }, metrics, NullLogger<ObservableWorkflowNotifier>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            decorated.NotifyAsync(new WorkflowNotification(WorkflowNotificationKind.PlanFailed, "repo-1", 1, "workflow-1", "failed"), CancellationToken.None));

        Assert.Equal(1, capture.CountFor("issueagent.notifications.failures"));
    }
}

public sealed class ObservableGitProviderTests
{
    private static readonly RepositoryRef Repository = new("github/octo/widgets", "octo", "widgets");

    [Fact]
    public async Task DiscoverAssignedOpenIssuesAsyncRecordsOneRequestMeasurementAtProviderBoundary()
    {
        // Buffered issue records are not HTTP request boundaries; counting each yielded item
        // inflates request volume and makes metrics depend on consumer iteration.
        using var capture = new MetricCapture();
        using var metrics = new IssueAgentMetrics();
        var inner = new FakeGitProvider
        {
            Issues =
            [
                new IssueSummary(1, "a", DateTimeOffset.UtcNow, new HashSet<string>()),
                new IssueSummary(2, "b", DateTimeOffset.UtcNow, new HashSet<string>()),
                new IssueSummary(3, "c", DateTimeOffset.UtcNow, new HashSet<string>()),
            ],
        };
        var decorated = new ObservableGitProvider(inner, metrics, NullLogger<ObservableGitProvider>.Instance);

        var results = new List<IssueSummary>();
        await foreach (var issue in decorated.DiscoverAssignedOpenIssuesAsync(Repository, "bot", DateTimeOffset.MinValue, CancellationToken.None))
        {
            results.Add(issue);
        }

        Assert.Equal(3, results.Count);
        Assert.Equal(1, capture.CountFor("issueagent.provider.requests"));
    }


    [Fact]
    public async Task DiscoverAssignedOpenIssuesAsyncDoesNotRecordAnErrorWhenCancelledByItsOwnToken()
    {
        using var capture = new MetricCapture();
        using var metrics = new IssueAgentMetrics();
        var inner = new FakeGitProvider { Issues = [new IssueSummary(1, "a", DateTimeOffset.UtcNow, new HashSet<string>()), new IssueSummary(2, "b", DateTimeOffset.UtcNow, new HashSet<string>())] };
        var decorated = new ObservableGitProvider(inner, metrics, NullLogger<ObservableGitProvider>.Instance);
        using var cts = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in decorated.DiscoverAssignedOpenIssuesAsync(Repository, "bot", DateTimeOffset.MinValue, cts.Token))
            {
                await cts.CancelAsync();
            }
        });

        Assert.Equal(0, capture.CountFor("issueagent.provider.errors"));
    }

    [Fact]
    public async Task DiscoverAssignedOpenIssuesAsyncRecordsAnErrorForAGenuineProviderFailure()
    {
        using var capture = new MetricCapture();
        using var metrics = new IssueAgentMetrics();
        var inner = new FakeGitProvider
        {
            Issues = [new IssueSummary(1, "a", DateTimeOffset.UtcNow, new HashSet<string>())],
            ThrowOnEnumeration = true,
        };
        var decorated = new ObservableGitProvider(inner, metrics, NullLogger<ObservableGitProvider>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in decorated.DiscoverAssignedOpenIssuesAsync(Repository, "bot", DateTimeOffset.MinValue, CancellationToken.None))
            {
            }
        });

        Assert.Equal(1, capture.CountFor("issueagent.provider.errors"));
    }

    [Fact]
    public async Task GetCurrentIdentityAsyncDoesNotRecordAnErrorWhenCancelledByItsOwnToken()
    {
        using var capture = new MetricCapture();
        using var metrics = new IssueAgentMetrics();
        var inner = new CancelingGitProvider();
        var decorated = new ObservableGitProvider(inner, metrics, NullLogger<ObservableGitProvider>.Instance);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => decorated.GetCurrentIdentityAsync(cts.Token).AsTask());

        Assert.Equal(0, capture.CountFor("issueagent.provider.errors"));
    }

    private sealed class CancelingGitProvider : IGitProvider
    {
        public string Name => "fake";

        public ValueTask<ProviderIdentity> GetCurrentIdentityAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("Unreachable: token must already be canceled in this test.");
        }

        public ValueTask<string> GetDefaultBranchAsync(RepositoryRef repository, CancellationToken cancellationToken) => throw new NotSupportedException();
        public IAsyncEnumerable<IssueSummary> DiscoverAssignedOpenIssuesAsync(RepositoryRef repository, string identity, DateTimeOffset startDate, CancellationToken cancellationToken) => throw new NotSupportedException();
        public IAsyncEnumerable<IssueSummary> DiscoverManagedIssuesAsync(RepositoryRef repository, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<ProviderIssue> GetIssueAsync(RepositoryRef repository, long issueNumber, CancellationToken cancellationToken) => throw new NotSupportedException();
        public IAsyncEnumerable<ProviderComment> GetIssueCommentsAsync(RepositoryRef repository, long issueNumber, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<ProviderComment> CreateIssueCommentAsync(RepositoryRef repository, long issueNumber, string body, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<ProviderComment> UpdateIssueCommentAsync(RepositoryRef repository, long issueNumber, long commentId, string body, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<IReadOnlySet<string>> GetLabelsAsync(ProviderWorkItemReference workItem, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask AddLabelsAsync(ProviderWorkItemReference workItem, IReadOnlyCollection<string> labels, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask RemoveLabelAsync(ProviderWorkItemReference workItem, string label, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask EnsureLabelAsync(RepositoryRef repository, ProviderLabel label, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<ProviderMergeRequest?> FindMergeRequestAsync(RepositoryRef repository, string sourceBranch, string targetBranch, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<ProviderMergeRequest> CreateDraftMergeRequestAsync(CreateMergeRequestRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<ProviderMergeRequest> GetMergeRequestAsync(RepositoryRef repository, long number, CancellationToken cancellationToken) => throw new NotSupportedException();
        public IAsyncEnumerable<ProviderComment> GetMergeRequestCommentsAsync(RepositoryRef repository, long number, CancellationToken cancellationToken) => throw new NotSupportedException();
        public IAsyncEnumerable<ProviderReviewThread> GetReviewThreadsAsync(RepositoryRef repository, long number, CancellationToken cancellationToken) => throw new NotSupportedException();
        public IAsyncEnumerable<IssueRelationship> GetIssueRelationshipsAsync(RepositoryRef repository, long issueNumber, CancellationToken cancellationToken) => throw new NotSupportedException();
        public bool IsTrustedAttachmentHost(Uri url) => throw new NotSupportedException();
        public ValueTask<DownloadedAttachment> DownloadAttachmentAsync(ProviderAttachment attachment, string destinationDirectory, long maxSizeBytes, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
