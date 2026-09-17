using IssueAgent.Providers;

namespace IssueAgent.Workflow.Tests;

/// <summary>Minimal in-memory <see cref="IGitProvider"/> for workflow orchestration tests.</summary>
public sealed class FakeGitProvider : IGitProvider
{
    public Dictionary<(string RepositoryId, long Number), ProviderIssue> Issues { get; } = [];

    public Dictionary<(string RepositoryId, long Number), List<ProviderComment>> IssueComments { get; } = [];

    public Dictionary<(string RepositoryId, ProviderWorkItemKind Kind, long Number), HashSet<string>> Labels { get; } = [];

    public HashSet<string> CreatedLabels { get; } = [];

    public List<(long IssueNumber, string Body)> CreatedComments { get; } = [];

    public List<(long IssueNumber, long CommentId, string Body)> UpdatedComments { get; } = [];

    public string Name => "fake";

    public void AddIssue(RepositoryRef repository, long number, string title, string description, IReadOnlySet<string>? labels = null)
    {
        Issues[(repository.Id, number)] = new ProviderIssue(
            repository, number, title, description, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            labels ?? new HashSet<string>(), new HashSet<string>(),
            new AttachmentSource("issue-description", number.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        Labels[(repository.Id, ProviderWorkItemKind.Issue, number)] = labels is null ? [] : [.. labels];
    }

    public void AddComment(RepositoryRef repository, long number, string author, string body, DateTimeOffset createdAt, bool isBot = false)
    {
        var key = (repository.Id, number);
        if (!IssueComments.TryGetValue(key, out var list))
        {
            IssueComments[key] = list = [];
        }

        var id = list.Count + 1;
        list.Add(new ProviderComment(
            id, author, body, createdAt, createdAt,
            new AttachmentSource("issue-comment", number.ToString(System.Globalization.CultureInfo.InvariantCulture), id.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            isBot));
    }

    public ValueTask<ProviderIdentity> GetCurrentIdentityAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

    public IAsyncEnumerable<IssueSummary> DiscoverAssignedOpenIssuesAsync(RepositoryRef repository, string identity, DateTimeOffset startDate, CancellationToken cancellationToken) => throw new NotSupportedException();

    public ValueTask<ProviderIssue> GetIssueAsync(RepositoryRef repository, long issueNumber, CancellationToken cancellationToken) =>
        ValueTask.FromResult(Issues[(repository.Id, issueNumber)]);

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

    public ValueTask<ProviderComment> CreateIssueCommentAsync(RepositoryRef repository, long issueNumber, string body, CancellationToken cancellationToken)
    {
        CreatedComments.Add((issueNumber, body));
        var comment = new ProviderComment(999, "issue-agent-bot", body, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, new AttachmentSource("issue-comment", issueNumber.ToString(System.Globalization.CultureInfo.InvariantCulture)), false);
        var key = (repository.Id, issueNumber);
        if (!IssueComments.TryGetValue(key, out var list))
        {
            IssueComments[key] = list = [];
        }

        list.Add(comment);
        return ValueTask.FromResult(comment);
    }

    public ValueTask<ProviderComment> UpdateIssueCommentAsync(RepositoryRef repository, long issueNumber, long commentId, string body, CancellationToken cancellationToken)
    {
        UpdatedComments.Add((issueNumber, commentId, body));
        var key = (repository.Id, issueNumber);
        var list = IssueComments[key];
        var index = list.FindIndex(c => c.Id == commentId);
        var updated = list[index] with { Body = body, UpdatedAt = DateTimeOffset.UtcNow };
        list[index] = updated;
        return ValueTask.FromResult(updated);
    }

    public ValueTask<IReadOnlySet<string>> GetLabelsAsync(ProviderWorkItemReference workItem, CancellationToken cancellationToken) =>
        ValueTask.FromResult<IReadOnlySet<string>>(Labels.TryGetValue((workItem.Repository.Id, workItem.Kind, workItem.Number), out var labels) ? labels : new HashSet<string>());

    public ValueTask AddLabelsAsync(ProviderWorkItemReference workItem, IReadOnlyCollection<string> labels, CancellationToken cancellationToken)
    {
        var key = (workItem.Repository.Id, workItem.Kind, workItem.Number);
        if (!Labels.TryGetValue(key, out var set))
        {
            Labels[key] = set = [];
        }

        foreach (var label in labels)
        {
            set.Add(label);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask RemoveLabelAsync(ProviderWorkItemReference workItem, string label, CancellationToken cancellationToken)
    {
        Labels[(workItem.Repository.Id, workItem.Kind, workItem.Number)].Remove(label);
        return ValueTask.CompletedTask;
    }

    public ValueTask EnsureLabelAsync(RepositoryRef repository, ProviderLabel label, CancellationToken cancellationToken)
    {
        CreatedLabels.Add(label.Name);
        return ValueTask.CompletedTask;
    }

    public ValueTask<ProviderMergeRequest?> FindMergeRequestAsync(RepositoryRef repository, string sourceBranch, string targetBranch, CancellationToken cancellationToken) => throw new NotSupportedException();

    public ValueTask<ProviderMergeRequest> CreateDraftMergeRequestAsync(CreateMergeRequestRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

    public ValueTask<ProviderMergeRequest> GetMergeRequestAsync(RepositoryRef repository, long number, CancellationToken cancellationToken) => throw new NotSupportedException();

    public IAsyncEnumerable<ProviderComment> GetMergeRequestCommentsAsync(RepositoryRef repository, long number, CancellationToken cancellationToken) => throw new NotSupportedException();

    public IAsyncEnumerable<ProviderReviewThread> GetReviewThreadsAsync(RepositoryRef repository, long number, CancellationToken cancellationToken) => throw new NotSupportedException();

    public async IAsyncEnumerable<IssueRelationship> GetIssueRelationshipsAsync(RepositoryRef repository, long issueNumber, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        yield break;
    }

    public bool IsTrustedAttachmentHost(Uri url) => false;

    public ValueTask<DownloadedAttachment> DownloadAttachmentAsync(ProviderAttachment attachment, string destinationDirectory, long maxSizeBytes, CancellationToken cancellationToken) => throw new NotSupportedException();
}
