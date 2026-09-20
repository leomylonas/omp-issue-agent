using IssueAgent.Providers;
using Microsoft.Extensions.Logging.Abstractions;

namespace IssueAgent.Observability.Tests;

public sealed class ProviderIdentityCacheTests
{
    [Fact]
    public async Task GetCurrentIdentityAsyncCachesSuccessfulLookupPerProvider()
    {
        var inner = new IdentityCountingProvider();
        using var metrics = new IssueAgentMetrics();
        var provider = new ObservableGitProvider(inner, metrics, NullLogger<ObservableGitProvider>.Instance);

        var first = await provider.GetCurrentIdentityAsync(CancellationToken.None);
        var second = await provider.GetCurrentIdentityAsync(CancellationToken.None);

        Assert.Equal(first, second);
        Assert.Equal(1, inner.IdentityLookups);
    }

    private sealed class IdentityCountingProvider : IGitProvider
    {
        public int IdentityLookups { get; private set; }
        public string Name => "provider";
        public ValueTask<ProviderIdentity> GetCurrentIdentityAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(new ProviderIdentity($"bot-{++IdentityLookups}", "Bot"));
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
