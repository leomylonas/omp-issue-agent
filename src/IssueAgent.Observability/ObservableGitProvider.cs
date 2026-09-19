using System.Diagnostics;
using System.Runtime.CompilerServices;
using IssueAgent.Providers;
using Microsoft.Extensions.Logging;

namespace IssueAgent.Observability;

/// <summary>Wraps an <see cref="IGitProvider"/> with tracing, metrics, and structured logging
/// (specification §30: provider request/error/duration metrics), without changing the wrapped
/// implementation. Attachment-trust checks pass straight through: they are local predicates, not
/// provider API calls.</summary>
public sealed class ObservableGitProvider(IGitProvider inner, IssueAgentMetrics metrics, ILogger<ObservableGitProvider> logger) : IGitProvider
{
    public string Name => inner.Name;

    public bool IsTrustedAttachmentHost(Uri url) => inner.IsTrustedAttachmentHost(url);

    public async ValueTask<ProviderIdentity> GetCurrentIdentityAsync(CancellationToken cancellationToken) =>
        await RunAsync("get-current-identity", () => inner.GetCurrentIdentityAsync(cancellationToken).AsTask(), cancellationToken).ConfigureAwait(false);

    public async ValueTask<string> GetDefaultBranchAsync(RepositoryRef repository, CancellationToken cancellationToken) =>
        await RunAsync("get-default-branch", () => inner.GetDefaultBranchAsync(repository, cancellationToken).AsTask(), cancellationToken).ConfigureAwait(false);

    public async IAsyncEnumerable<IssueSummary> DiscoverAssignedOpenIssuesAsync(
        RepositoryRef repository,
        string identity,
        DateTimeOffset startDate,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var issue in RunEnumerableAsync(
            "discover-assigned-open-issues",
            inner.DiscoverAssignedOpenIssuesAsync(repository, identity, startDate, cancellationToken),
            cancellationToken).ConfigureAwait(false))
        {
            yield return issue;
        }
    }

    public async IAsyncEnumerable<IssueSummary> DiscoverManagedIssuesAsync(
        RepositoryRef repository,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var issue in RunEnumerableAsync(
            "discover-managed-issues",
            inner.DiscoverManagedIssuesAsync(repository, cancellationToken),
            cancellationToken).ConfigureAwait(false))
        {
            yield return issue;
        }
    }

    public async ValueTask<ProviderIssue> GetIssueAsync(RepositoryRef repository, long issueNumber, CancellationToken cancellationToken) =>
        await RunAsync("get-issue", () => inner.GetIssueAsync(repository, issueNumber, cancellationToken).AsTask(), cancellationToken).ConfigureAwait(false);

    public async IAsyncEnumerable<ProviderComment> GetIssueCommentsAsync(
        RepositoryRef repository,
        long issueNumber,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var comment in RunEnumerableAsync(
            "get-issue-comments",
            inner.GetIssueCommentsAsync(repository, issueNumber, cancellationToken),
            cancellationToken).ConfigureAwait(false))
        {
            yield return comment;
        }
    }

    public async ValueTask<ProviderComment> CreateIssueCommentAsync(RepositoryRef repository, long issueNumber, string body, CancellationToken cancellationToken) =>
        await RunAsync("create-issue-comment", () => inner.CreateIssueCommentAsync(repository, issueNumber, body, cancellationToken).AsTask(), cancellationToken).ConfigureAwait(false);

    public async ValueTask<ProviderComment> UpdateIssueCommentAsync(RepositoryRef repository, long issueNumber, long commentId, string body, CancellationToken cancellationToken) =>
        await RunAsync("update-issue-comment", () => inner.UpdateIssueCommentAsync(repository, issueNumber, commentId, body, cancellationToken).AsTask(), cancellationToken).ConfigureAwait(false);

    public async ValueTask<IReadOnlySet<string>> GetLabelsAsync(ProviderWorkItemReference workItem, CancellationToken cancellationToken) =>
        await RunAsync("get-labels", () => inner.GetLabelsAsync(workItem, cancellationToken).AsTask(), cancellationToken).ConfigureAwait(false);

    public async ValueTask AddLabelsAsync(ProviderWorkItemReference workItem, IReadOnlyCollection<string> labels, CancellationToken cancellationToken) =>
        await RunAsync("add-labels", () => inner.AddLabelsAsync(workItem, labels, cancellationToken).AsTask(), cancellationToken).ConfigureAwait(false);

    public async ValueTask RemoveLabelAsync(ProviderWorkItemReference workItem, string label, CancellationToken cancellationToken) =>
        await RunAsync("remove-label", () => inner.RemoveLabelAsync(workItem, label, cancellationToken).AsTask(), cancellationToken).ConfigureAwait(false);

    public async ValueTask EnsureLabelAsync(RepositoryRef repository, ProviderLabel label, CancellationToken cancellationToken) =>
        await RunAsync("ensure-label", () => inner.EnsureLabelAsync(repository, label, cancellationToken).AsTask(), cancellationToken).ConfigureAwait(false);

    public async ValueTask<ProviderMergeRequest?> FindMergeRequestAsync(RepositoryRef repository, string sourceBranch, string targetBranch, CancellationToken cancellationToken) =>
        await RunAsync("find-merge-request", () => inner.FindMergeRequestAsync(repository, sourceBranch, targetBranch, cancellationToken).AsTask(), cancellationToken).ConfigureAwait(false);

    public async ValueTask<ProviderMergeRequest> CreateDraftMergeRequestAsync(CreateMergeRequestRequest request, CancellationToken cancellationToken) =>
        await RunAsync("create-draft-merge-request", () => inner.CreateDraftMergeRequestAsync(request, cancellationToken).AsTask(), cancellationToken).ConfigureAwait(false);

    public async ValueTask<ProviderMergeRequest> GetMergeRequestAsync(RepositoryRef repository, long number, CancellationToken cancellationToken) =>
        await RunAsync("get-merge-request", () => inner.GetMergeRequestAsync(repository, number, cancellationToken).AsTask(), cancellationToken).ConfigureAwait(false);

    public async IAsyncEnumerable<ProviderComment> GetMergeRequestCommentsAsync(
        RepositoryRef repository,
        long number,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var comment in RunEnumerableAsync(
            "get-merge-request-comments",
            inner.GetMergeRequestCommentsAsync(repository, number, cancellationToken),
            cancellationToken).ConfigureAwait(false))
        {
            yield return comment;
        }
    }

    public async IAsyncEnumerable<ProviderReviewThread> GetReviewThreadsAsync(
        RepositoryRef repository,
        long number,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var thread in RunEnumerableAsync(
            "get-review-threads",
            inner.GetReviewThreadsAsync(repository, number, cancellationToken),
            cancellationToken).ConfigureAwait(false))
        {
            yield return thread;
        }
    }

    public async IAsyncEnumerable<IssueRelationship> GetIssueRelationshipsAsync(
        RepositoryRef repository,
        long issueNumber,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var relationship in RunEnumerableAsync(
            "get-issue-relationships",
            inner.GetIssueRelationshipsAsync(repository, issueNumber, cancellationToken),
            cancellationToken).ConfigureAwait(false))
        {
            yield return relationship;
        }
    }

    public async ValueTask<DownloadedAttachment> DownloadAttachmentAsync(ProviderAttachment attachment, string destinationDirectory, long maxSizeBytes, CancellationToken cancellationToken) =>
        await RunAsync("download-attachment", () => inner.DownloadAttachmentAsync(attachment, destinationDirectory, maxSizeBytes, cancellationToken).AsTask(), cancellationToken).ConfigureAwait(false);

    private async Task<T> RunAsync<T>(string operation, Func<Task<T>> action, CancellationToken cancellationToken)
    {
        using var activity = IssueAgentActivitySource.Source.StartActivity($"provider.{operation}", ActivityKind.Client);
        activity?.SetTag(LogContextFields.Provider, inner.Name);
        var tags = new KeyValuePair<string, object?>[] { new(LogContextFields.Operation, operation), new(LogContextFields.Provider, inner.Name) };
        var stopwatch = Stopwatch.StartNew();
        metrics.ProviderRequests.Add(1, tags);
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Graceful shutdown/agent-cancel is normal operation (specification §27), not a
            // provider failure; must never inflate issueagent_provider_errors_total.
            throw;
        }
        catch (Exception ex)
        {
            metrics.ProviderErrors.Add(1, tags);
            activity?.SetStatus(ActivityStatusCode.Error);
            ProviderLogMessages.ProviderOperationFailed(logger, ex.GetType().Name, operation, inner.Name);
            throw;
        }
        finally
        {
            metrics.ProviderDuration.Record(stopwatch.Elapsed.TotalSeconds, tags);
        }
    }

    private async Task RunAsync(string operation, Func<Task> action, CancellationToken cancellationToken)
    {
        using var activity = IssueAgentActivitySource.Source.StartActivity($"provider.{operation}", ActivityKind.Client);
        activity?.SetTag(LogContextFields.Provider, inner.Name);
        var tags = new KeyValuePair<string, object?>[] { new(LogContextFields.Operation, operation), new(LogContextFields.Provider, inner.Name) };
        var stopwatch = Stopwatch.StartNew();
        metrics.ProviderRequests.Add(1, tags);
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            metrics.ProviderErrors.Add(1, tags);
            activity?.SetStatus(ActivityStatusCode.Error);
            ProviderLogMessages.ProviderOperationFailed(logger, ex.GetType().Name, operation, inner.Name);
            throw;
        }
        finally
        {
            metrics.ProviderDuration.Record(stopwatch.Elapsed.TotalSeconds, tags);
        }
    }

    /// <summary>Wraps a paginated provider call at its public request boundary. The provider
    /// implementations buffer each page before yielding items; measuring every yielded item
    /// counts buffered records as HTTP requests and inflates latency. This decorator therefore
    /// records one request and duration for the provider operation itself.</summary>
    private async IAsyncEnumerable<T> RunEnumerableAsync<T>(
        string operation,
        IAsyncEnumerable<T> source,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var activity = IssueAgentActivitySource.Source.StartActivity($"provider.{operation}", ActivityKind.Client);
        activity?.SetTag(LogContextFields.Provider, inner.Name);
        var tags = new KeyValuePair<string, object?>[] { new(LogContextFields.Operation, operation), new(LogContextFields.Provider, inner.Name) };
        var stopwatch = Stopwatch.StartNew();
        metrics.ProviderRequests.Add(1, tags);
        var enumerator = source.GetAsyncEnumerator(cancellationToken);
        try
        {
            while (true)
            {
                bool hasNext;
                try
                {
                    hasNext = await enumerator.MoveNextAsync().ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    metrics.ProviderErrors.Add(1, tags);
                    activity?.SetStatus(ActivityStatusCode.Error);
                    ProviderLogMessages.ProviderOperationFailed(logger, ex.GetType().Name, operation, inner.Name);
                    throw;
                }

                if (!hasNext)
                {
                    break;
                }

                yield return enumerator.Current;
            }
        }
        finally
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
            metrics.ProviderDuration.Record(stopwatch.Elapsed.TotalSeconds, tags);
        }
    }
}
