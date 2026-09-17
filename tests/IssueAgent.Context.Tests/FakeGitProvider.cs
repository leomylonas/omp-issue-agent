using IssueAgent.Providers;

namespace IssueAgent.Context.Tests;

/// <summary>Minimal in-memory <see cref="IGitProvider"/> for context-builder tests. Only the
/// members <see cref="AgentContextBuilder"/> and <see cref="AttachmentPipeline"/> actually call are
/// implemented meaningfully; everything else throws to catch accidental new dependencies.</summary>
public sealed class FakeGitProvider : IGitProvider
{
    public Dictionary<(string RepositoryId, long Number), ProviderIssue> Issues { get; } = [];

    public Dictionary<(string RepositoryId, long Number), List<ProviderComment>> IssueComments { get; } = [];

    public Dictionary<(string RepositoryId, long Number), List<IssueRelationship>> Relationships { get; } = [];

    public Dictionary<(string RepositoryId, long Number), List<ProviderComment>> MergeRequestComments { get; } = [];

    public Dictionary<(string RepositoryId, long Number), List<ProviderReviewThread>> ReviewThreads { get; } = [];

    public Dictionary<string, byte[]> DownloadableContent { get; } = [];

    public HashSet<string> TrustedHosts { get; } = [];

    public string Name => "fake";

    public void AddIssue(RepositoryRef repository, long number, string title, string description, IReadOnlySet<string>? labels = null, IReadOnlySet<string>? assignees = null) =>
        Issues[(repository.Id, number)] = new ProviderIssue(
            repository, number, title, description, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            labels ?? new HashSet<string>(), assignees ?? new HashSet<string>(),
            new AttachmentSource("issue-description", number.ToString(System.Globalization.CultureInfo.InvariantCulture)));

    public void AddComment(RepositoryRef repository, long number, string author, string body, bool isBot = false)
    {
        var key = (repository.Id, number);
        if (!IssueComments.TryGetValue(key, out var list))
        {
            IssueComments[key] = list = [];
        }

        list.Add(new ProviderComment(
            list.Count + 1, author, body, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            new AttachmentSource("issue-comment", number.ToString(System.Globalization.CultureInfo.InvariantCulture), (list.Count + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)),
            isBot));
    }

    public void AddRelationship(RepositoryRef repository, long number, string relationship, RepositoryRef targetRepository, long targetNumber)
    {
        var key = (repository.Id, number);
        if (!Relationships.TryGetValue(key, out var list))
        {
            Relationships[key] = list = [];
        }

        list.Add(new IssueRelationship(relationship, targetRepository, targetNumber));
    }

    public ValueTask<ProviderIdentity> GetCurrentIdentityAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

    public IAsyncEnumerable<IssueSummary> DiscoverAssignedOpenIssuesAsync(RepositoryRef repository, string identity, DateTimeOffset startDate, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public ValueTask<ProviderIssue> GetIssueAsync(RepositoryRef repository, long issueNumber, CancellationToken cancellationToken) =>
        Issues.TryGetValue((repository.Id, issueNumber), out var issue)
            ? ValueTask.FromResult(issue)
            : throw new KeyNotFoundException($"No fake issue registered for {repository.Id}#{issueNumber}.");

    public async IAsyncEnumerable<ProviderComment> GetIssueCommentsAsync(RepositoryRef repository, long issueNumber, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        if (IssueComments.TryGetValue((repository.Id, issueNumber), out var comments))
        {
            foreach (var comment in comments)
            {
                yield return comment;
            }
        }
    }

    public ValueTask<ProviderComment> CreateIssueCommentAsync(RepositoryRef repository, long issueNumber, string body, CancellationToken cancellationToken) => throw new NotSupportedException();

    public ValueTask<ProviderComment> UpdateIssueCommentAsync(RepositoryRef repository, long issueNumber, long commentId, string body, CancellationToken cancellationToken) => throw new NotSupportedException();

    public ValueTask<IReadOnlySet<string>> GetLabelsAsync(ProviderWorkItemReference workItem, CancellationToken cancellationToken) => throw new NotSupportedException();

    public ValueTask AddLabelsAsync(ProviderWorkItemReference workItem, IReadOnlyCollection<string> labels, CancellationToken cancellationToken) => throw new NotSupportedException();

    public ValueTask RemoveLabelAsync(ProviderWorkItemReference workItem, string label, CancellationToken cancellationToken) => throw new NotSupportedException();

    public ValueTask EnsureLabelAsync(RepositoryRef repository, ProviderLabel label, CancellationToken cancellationToken) => throw new NotSupportedException();

    public ValueTask<ProviderMergeRequest?> FindMergeRequestAsync(RepositoryRef repository, string sourceBranch, string targetBranch, CancellationToken cancellationToken) => throw new NotSupportedException();

    public ValueTask<ProviderMergeRequest> CreateDraftMergeRequestAsync(CreateMergeRequestRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

    public ValueTask<ProviderMergeRequest> GetMergeRequestAsync(RepositoryRef repository, long number, CancellationToken cancellationToken) => throw new NotSupportedException();

    public async IAsyncEnumerable<ProviderComment> GetMergeRequestCommentsAsync(RepositoryRef repository, long number, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        if (MergeRequestComments.TryGetValue((repository.Id, number), out var comments))
        {
            foreach (var comment in comments)
            {
                yield return comment;
            }
        }
    }

    public async IAsyncEnumerable<ProviderReviewThread> GetReviewThreadsAsync(RepositoryRef repository, long number, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        if (ReviewThreads.TryGetValue((repository.Id, number), out var threads))
        {
            foreach (var thread in threads)
            {
                yield return thread;
            }
        }
    }

    public async IAsyncEnumerable<IssueRelationship> GetIssueRelationshipsAsync(RepositoryRef repository, long issueNumber, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        if (Relationships.TryGetValue((repository.Id, issueNumber), out var relationships))
        {
            foreach (var relationship in relationships)
            {
                yield return relationship;
            }
        }
    }

    public bool IsTrustedAttachmentHost(Uri url) => TrustedHosts.Contains(url.Host);

    public ValueTask<DownloadedAttachment> DownloadAttachmentAsync(ProviderAttachment attachment, string destinationDirectory, long maxSizeBytes, CancellationToken cancellationToken)
    {
        if (!DownloadableContent.TryGetValue(attachment.Url.ToString(), out var content))
        {
            throw new KeyNotFoundException($"No fake content registered for {attachment.Url}.");
        }

        if (content.Length > maxSizeBytes)
        {
            throw new AttachmentTooLargeException($"Attachment '{attachment.SuggestedFileName}' exceeds the {maxSizeBytes}-byte limit.");
        }

        Directory.CreateDirectory(destinationDirectory);
        var destinationPath = AttachmentFileNames.ResolveSafeDestination(destinationDirectory, attachment.SuggestedFileName);
        File.WriteAllBytes(destinationPath, content);
        return ValueTask.FromResult(new DownloadedAttachment(destinationPath, Path.GetFileName(destinationPath), content.Length));
    }
}
