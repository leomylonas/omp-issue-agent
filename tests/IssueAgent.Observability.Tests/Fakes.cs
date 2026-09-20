using IssueAgent.Git;
using IssueAgent.Omp;
using IssueAgent.Providers;
using IssueAgent.Workflow;
using Microsoft.Extensions.Logging;

namespace IssueAgent.Observability.Tests;

internal sealed class FakeGitRepositoryManager : IGitRepositoryManager
{
    public bool ThrowOnFetch { get; set; }

    public string FailureMessage { get; set; } = "Simulated fetch failure.";

    public ValueTask EnsureBareRepositoryAsync(string repositoryId, string cloneUrl, GitAuthentication authentication, CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public ValueTask FetchAsync(string repositoryId, GitAuthentication authentication, CancellationToken cancellationToken) =>
        ThrowOnFetch ? throw new InvalidOperationException(FailureMessage) : ValueTask.CompletedTask;

    public ValueTask<string> ResolveBranchCommitAsync(string repositoryId, string branchName, CancellationToken cancellationToken) => ValueTask.FromResult("abc123");

    public ValueTask<string?> TryResolveRemoteBranchCommitAsync(string repositoryId, string branchName, CancellationToken cancellationToken) =>
        ValueTask.FromResult<string?>("abc123");

    public ValueTask<bool> IsAncestorAsync(string repositoryId, string ancestorCommit, string descendantCommit, CancellationToken cancellationToken) =>
        ValueTask.FromResult(true);

    public ValueTask CreateWorktreeAsync(string repositoryId, string worktreeId, string worktreePath, string branchName, string baseCommit, CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public ValueTask ResetWorktreeAsync(string repositoryId, string worktreePath, string commit, CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public ValueTask<bool> HasUncommittedChangesAsync(string repositoryId, string worktreePath, CancellationToken cancellationToken) => ValueTask.FromResult(false);

    public ValueTask<string> GetHeadCommitAsync(string repositoryId, string worktreePath, CancellationToken cancellationToken) => ValueTask.FromResult("abc123");

    public ValueTask UpdateSubmodulesAsync(string repositoryId, string worktreePath, Func<string, GitAuthentication?> authenticationResolver, CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public ValueTask<bool> TryRebaseOntoAsync(string repositoryId, string worktreePath, string ontoCommit, GitIdentity identity, CancellationToken cancellationToken) => ValueTask.FromResult(true);

    public ValueTask<bool> TryMergeAsync(string repositoryId, string worktreePath, string commit, GitIdentity identity, CancellationToken cancellationToken) => ValueTask.FromResult(true);

    public ValueTask PushAsync(string repositoryId, string worktreePath, string branchName, GitAuthentication authentication, CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public ValueTask RemoveWorktreeAsync(string repositoryId, string worktreeId, string worktreePath, CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public ValueTask RemoveLocalBranchAsync(string repositoryId, string branchName, CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public bool WorktreeRequiresLfs(string worktreePath) => false;

    public ValueTask MaterializeLfsContentAsync(string repositoryId, string worktreePath, GitAuthentication authentication, CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public ValueTask UploadLfsObjectsAsync(string repositoryId, string worktreePath, string branchName, GitAuthentication authentication, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Simulated LFS upload failure.");
}

internal sealed class FakeOmpClient : IOmpClient
{
    public bool ThrowDuringRun { get; set; }

    public string? ToolResult { get; set; }
    public ValueTask<OmpSession> CreateSessionAsync(string role, CancellationToken cancellationToken) => ValueTask.FromResult(new OmpSession("session-1", role));

    public ValueTask<OmpSession> ResumeSessionAsync(string sessionId, string sessionFile, CancellationToken cancellationToken) => ValueTask.FromResult(new OmpSession(sessionId, "task", sessionFile));

    public async IAsyncEnumerable<OmpEvent> RunAsync(OmpRunRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        yield return new OmpMessageEvent(request.SessionId, DateTimeOffset.UtcNow, "working");
        if (ToolResult is not null)
        {
            yield return new OmpToolResultEvent(request.SessionId, DateTimeOffset.UtcNow, "call-1", false, ToolResult);
        }
        if (ThrowDuringRun)
        {
            throw new InvalidOperationException("Simulated run failure.");
        }

        yield return new OmpCompletedEvent(request.SessionId, DateTimeOffset.UtcNow, "{}");
    }

    public ValueTask CancelAsync(string sessionId, CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class FakeWorkflowNotifier : IWorkflowNotifier
{
    public bool ThrowOnNotify { get; set; }

    public Task NotifyAsync(WorkflowNotification notification, CancellationToken cancellationToken) =>
        ThrowOnNotify ? throw new InvalidOperationException("Simulated notify failure.") : Task.CompletedTask;
}

internal sealed class RecordingLogger<T> : ILogger<T>
{
    public List<string> Messages { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter) =>
        Messages.Add(formatter(state, exception) + (exception is null ? string.Empty : exception.ToString()));
}

/// <summary>Minimal in-memory <see cref="IGitProvider"/> for <see cref="ObservableGitProvider"/>
/// decorator tests. Only the members exercised by those tests are implemented meaningfully.</summary>
internal sealed class FakeGitProvider : IGitProvider
{
    public string Name => "fake";

    public IReadOnlyList<IssueSummary> Issues { get; set; } = [];

    public bool ThrowOnEnumeration { get; set; }

    public ValueTask<ProviderIdentity> GetCurrentIdentityAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

    public ValueTask<string> GetDefaultBranchAsync(RepositoryRef repository, CancellationToken cancellationToken) => throw new NotSupportedException();

    public async IAsyncEnumerable<IssueSummary> DiscoverAssignedOpenIssuesAsync(
        RepositoryRef repository,
        string identity,
        DateTimeOffset startDate,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var issue in Issues)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            if (ThrowOnEnumeration)
            {
                throw new InvalidOperationException("Simulated discovery failure.");
            }

            yield return issue;
        }
    }

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
