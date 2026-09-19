using IssueAgent.Domain;

namespace IssueAgent.Providers;

/// <summary>Provider-neutral API surface used by IssueAgent workflows.</summary>
public interface IGitProvider
{
    string Name { get; }

    ValueTask<ProviderIdentity> GetCurrentIdentityAsync(CancellationToken cancellationToken);

    /// <summary>Returns the repository's default branch (spec §10: the effective target branch
    /// falls back to this when no repository-level override is configured).</summary>
    ValueTask<string> GetDefaultBranchAsync(RepositoryRef repository, CancellationToken cancellationToken);

    IAsyncEnumerable<IssueSummary> DiscoverAssignedOpenIssuesAsync(
        RepositoryRef repository,
        string identity,
        DateTimeOffset startDate,
        CancellationToken cancellationToken);

    /// <summary>Enumerates every issue already owned by IssueAgent, including closed issues, so
    /// restart reconciliation and terminal cleanup do not depend on the new-assignment query.</summary>
    IAsyncEnumerable<IssueSummary> DiscoverManagedIssuesAsync(
        RepositoryRef repository,
        CancellationToken cancellationToken);

    ValueTask<ProviderIssue> GetIssueAsync(
        RepositoryRef repository,
        long issueNumber,
        CancellationToken cancellationToken);

    IAsyncEnumerable<ProviderComment> GetIssueCommentsAsync(
        RepositoryRef repository,
        long issueNumber,
        CancellationToken cancellationToken);

    ValueTask<ProviderComment> CreateIssueCommentAsync(
        RepositoryRef repository,
        long issueNumber,
        string body,
        CancellationToken cancellationToken);

    ValueTask<ProviderComment> UpdateIssueCommentAsync(
        RepositoryRef repository,
        long issueNumber,
        long commentId,
        string body,
        CancellationToken cancellationToken);

    ValueTask<IReadOnlySet<string>> GetLabelsAsync(
        ProviderWorkItemReference workItem,
        CancellationToken cancellationToken);

    ValueTask AddLabelsAsync(
        ProviderWorkItemReference workItem,
        IReadOnlyCollection<string> labels,
        CancellationToken cancellationToken);

    ValueTask RemoveLabelAsync(
        ProviderWorkItemReference workItem,
        string label,
        CancellationToken cancellationToken);

    ValueTask EnsureLabelAsync(
        RepositoryRef repository,
        ProviderLabel label,
        CancellationToken cancellationToken);

    ValueTask<ProviderMergeRequest?> FindMergeRequestAsync(
        RepositoryRef repository,
        string sourceBranch,
        string targetBranch,
        CancellationToken cancellationToken);

    ValueTask<ProviderMergeRequest> CreateDraftMergeRequestAsync(
        CreateMergeRequestRequest request,
        CancellationToken cancellationToken);

    ValueTask<ProviderMergeRequest> GetMergeRequestAsync(
        RepositoryRef repository,
        long number,
        CancellationToken cancellationToken);

    IAsyncEnumerable<ProviderComment> GetMergeRequestCommentsAsync(
        RepositoryRef repository,
        long number,
        CancellationToken cancellationToken);

    IAsyncEnumerable<ProviderReviewThread> GetReviewThreadsAsync(
        RepositoryRef repository,
        long number,
        CancellationToken cancellationToken);

    IAsyncEnumerable<IssueRelationship> GetIssueRelationshipsAsync(
        RepositoryRef repository,
        long issueNumber,
        CancellationToken cancellationToken);

    /// <summary>True when <paramref name="url"/> is a provider-owned attachment endpoint that may
    /// receive this provider's credentials. External hosts never receive provider credentials.</summary>
    bool IsTrustedAttachmentHost(Uri url);

    ValueTask<DownloadedAttachment> DownloadAttachmentAsync(
        ProviderAttachment attachment,
        string destinationDirectory,
        long maxSizeBytes,
        CancellationToken cancellationToken);
}

public sealed record RepositoryRef(string Id, string OwnerOrNamespace, string Name);

public sealed record ProviderIdentity(string Login, string DisplayName);

public sealed record IssueSummary(long Number, string Title, DateTimeOffset CreatedAt, IReadOnlySet<string> Assignees);

public sealed record ProviderIssue(
    RepositoryRef Repository,
    long Number,
    string Title,
    string Description,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlySet<string> Labels,
    IReadOnlySet<string> Assignees,
    AttachmentSource DescriptionSource);

public enum ProviderWorkItemKind
{
    Issue,
    MergeRequest,
}

public sealed record ProviderWorkItemReference(RepositoryRef Repository, ProviderWorkItemKind Kind, long Number);

public sealed record ProviderComment(
    long Id,
    string AuthorLogin,
    string Body,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    AttachmentSource Source,
    bool IsBot);

public sealed record ProviderLabel(string Name, string Color, string Description);

public sealed record CreateMergeRequestRequest(
    RepositoryRef Repository,
    string SourceBranch,
    string TargetBranch,
    string Title,
    string Body,
    bool IsDraft,
    long IssueNumber);

public sealed record ProviderMergeRequest(
    RepositoryRef Repository,
    long Number,
    string SourceBranch,
    string TargetBranch,
    string Title,
    string Description,
    bool IsDraft,
    bool IsMerged,
    bool IsClosed,
    AttachmentSource DescriptionSource);

public sealed record ProviderReviewThread(
    string Id,
    bool IsResolved,
    IReadOnlyList<ProviderComment> Comments);

public sealed record IssueRelationship(
    string Relationship,
    RepositoryRef Repository,
    long IssueNumber);

public sealed record AttachmentSource(
    string Surface,
    string SourceId,
    string? ThreadId = null);

public sealed record ProviderAttachment(
    Uri Url,
    string SuggestedFileName,
    long? SizeBytes,
    AttachmentSource Source,
    bool IsProviderOwnedEndpoint,
    IReadOnlySet<System.Net.IPAddress>? ValidatedAddresses = null);

public sealed record DownloadedAttachment(
    string LocalPath,
    string SafeFileName,
    long SizeBytes);

/// <summary>Thrown when a downloaded attachment exceeds the caller-supplied size cap. The caller
/// omits the attachment rather than failing the workflow.</summary>
public sealed class AttachmentTooLargeException(string message) : Exception(message);
