using System.Globalization;
using System.Runtime.CompilerServices;
using IssueAgent.Providers;

namespace IssueAgent.Providers.GitLab;

/// <summary>
/// GitLab implementation of <see cref="IGitProvider"/> over the hand-rolled <see cref="GitLabApiClient"/>.
///
/// Known scope limits versus GitHub: GitLab's Issue Links API (used for relationships) exposes
/// <c>relates_to</c>, <c>blocks</c>, and <c>is_blocked_by</c> in Community Edition; parent/child and
/// duplicate relationships require Epics (a GitLab Premium/Ultimate feature) and are not implemented.
/// Draft merge requests use GitLab's documented <c>Draft:</c> title-prefix convention because the
/// stable REST API has no dedicated boolean create parameter.
/// </summary>
public sealed class GitLabProvider(
    GitLabApiClient client,
    HttpClient authenticatedAttachmentClient,
    HttpClient anonymousAttachmentClient,
    IReadOnlyList<string> trustedAttachmentHostSuffixes,
    string name) : IGitProvider
{
    private const string DraftTitlePrefix = "Draft: ";

    public string Name { get; } = name;

    public async ValueTask<ProviderIdentity> GetCurrentIdentityAsync(CancellationToken cancellationToken)
    {
        var user = await client.GetCurrentUserAsync(cancellationToken).ConfigureAwait(false);
        return new ProviderIdentity(user.Username, user.Name);
    }

    public async IAsyncEnumerable<IssueSummary> DiscoverAssignedOpenIssuesAsync(
        RepositoryRef repository,
        string identity,
        DateTimeOffset startDate,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var issues = await client.GetIssuesAsync(repository.Id, identity, "opened", cancellationToken).ConfigureAwait(false);
        foreach (var issue in issues)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (issue.CreatedAt < startDate)
            {
                continue;
            }

            yield return new IssueSummary(
                issue.Iid,
                issue.Title,
                issue.CreatedAt,
                issue.Assignees.Select(a => a.Username).ToHashSet(StringComparer.Ordinal));
        }
    }

    public async ValueTask<ProviderIssue> GetIssueAsync(RepositoryRef repository, long issueNumber, CancellationToken cancellationToken)
    {
        var issue = await client.GetIssueAsync(repository.Id, issueNumber, cancellationToken).ConfigureAwait(false);
        return ToProviderIssue(repository, issue);
    }

    public async IAsyncEnumerable<ProviderComment> GetIssueCommentsAsync(
        RepositoryRef repository,
        long issueNumber,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var notes = await client.GetIssueNotesAsync(repository.Id, issueNumber, cancellationToken).ConfigureAwait(false);
        foreach (var note in notes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (note.System)
            {
                continue;
            }

            yield return ToProviderComment(note, "issue-comment", issueNumber);
        }
    }

    public async ValueTask<ProviderComment> CreateIssueCommentAsync(RepositoryRef repository, long issueNumber, string body, CancellationToken cancellationToken)
    {
        var note = await client.CreateIssueNoteAsync(repository.Id, issueNumber, body, cancellationToken).ConfigureAwait(false);
        return ToProviderComment(note, "issue-comment", issueNumber);
    }

    public async ValueTask<ProviderComment> UpdateIssueCommentAsync(RepositoryRef repository, long issueNumber, long commentId, string body, CancellationToken cancellationToken)
    {
        var note = await client.UpdateIssueNoteAsync(repository.Id, issueNumber, commentId, body, cancellationToken).ConfigureAwait(false);
        return ToProviderComment(note, "issue-comment", issueNumber);
    }

    public async ValueTask<IReadOnlySet<string>> GetLabelsAsync(ProviderWorkItemReference workItem, CancellationToken cancellationToken)
    {
        if (workItem.Kind == ProviderWorkItemKind.Issue)
        {
            var issue = await client.GetIssueAsync(workItem.Repository.Id, workItem.Number, cancellationToken).ConfigureAwait(false);
            return issue.Labels.ToHashSet(StringComparer.Ordinal);
        }

        var mergeRequest = await client.GetMergeRequestAsync(workItem.Repository.Id, workItem.Number, cancellationToken).ConfigureAwait(false);
        return mergeRequest.Labels.ToHashSet(StringComparer.Ordinal);
    }

    public async ValueTask AddLabelsAsync(ProviderWorkItemReference workItem, IReadOnlyCollection<string> labels, CancellationToken cancellationToken)
    {
        if (workItem.Kind == ProviderWorkItemKind.Issue)
        {
            await client.UpdateIssueLabelsAsync(workItem.Repository.Id, workItem.Number, labels, null, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await client.UpdateMergeRequestLabelsAsync(workItem.Repository.Id, workItem.Number, labels, null, cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask RemoveLabelAsync(ProviderWorkItemReference workItem, string label, CancellationToken cancellationToken)
    {
        if (workItem.Kind == ProviderWorkItemKind.Issue)
        {
            await client.UpdateIssueLabelsAsync(workItem.Repository.Id, workItem.Number, null, [label], cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await client.UpdateMergeRequestLabelsAsync(workItem.Repository.Id, workItem.Number, null, [label], cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask EnsureLabelAsync(RepositoryRef repository, ProviderLabel label, CancellationToken cancellationToken)
    {
        var existing = await client.FindLabelAsync(repository.Id, label.Name, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            await client.CreateLabelAsync(repository.Id, label.Name, label.Color, label.Description, cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask<ProviderMergeRequest?> FindMergeRequestAsync(RepositoryRef repository, string sourceBranch, string targetBranch, CancellationToken cancellationToken)
    {
        var mergeRequests = await client.FindMergeRequestsAsync(repository.Id, sourceBranch, targetBranch, cancellationToken).ConfigureAwait(false);
        var match = mergeRequests.Count > 0 ? mergeRequests[0] : null;
        return match is null ? null : ToProviderMergeRequest(repository, match);
    }

    public async ValueTask<ProviderMergeRequest> CreateDraftMergeRequestAsync(CreateMergeRequestRequest request, CancellationToken cancellationToken)
    {
        var title = request.Title.StartsWith(DraftTitlePrefix, StringComparison.OrdinalIgnoreCase) ? request.Title : DraftTitlePrefix + request.Title;
        var created = await client.CreateMergeRequestAsync(request.Repository.Id, request.SourceBranch, request.TargetBranch, title, request.Body, cancellationToken)
            .ConfigureAwait(false);
        return ToProviderMergeRequest(request.Repository, created);
    }

    public async ValueTask<ProviderMergeRequest> GetMergeRequestAsync(RepositoryRef repository, long number, CancellationToken cancellationToken)
    {
        var mergeRequest = await client.GetMergeRequestAsync(repository.Id, number, cancellationToken).ConfigureAwait(false);
        return ToProviderMergeRequest(repository, mergeRequest);
    }

    public async IAsyncEnumerable<ProviderComment> GetMergeRequestCommentsAsync(
        RepositoryRef repository,
        long number,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var notes = await client.GetMergeRequestNotesAsync(repository.Id, number, cancellationToken).ConfigureAwait(false);
        foreach (var note in notes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (note.System)
            {
                continue;
            }

            yield return ToProviderComment(note, "merge-request-comment", number);
        }
    }

    public async IAsyncEnumerable<ProviderReviewThread> GetReviewThreadsAsync(
        RepositoryRef repository,
        long number,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var discussions = await client.GetMergeRequestDiscussionsAsync(repository.Id, number, cancellationToken).ConfigureAwait(false);
        foreach (var discussion in discussions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (discussion.IndividualNote)
            {
                continue;
            }

            var resolvableNotes = discussion.Notes.Where(n => n.Resolvable).ToList();
            var isResolved = resolvableNotes.Count > 0 && resolvableNotes.All(n => n.Resolved);

            yield return new ProviderReviewThread(
                discussion.Id,
                isResolved,
                discussion.Notes.Select(n => ToProviderComment(n, "merge-request-review-comment", number, discussion.Id)).ToList());
        }
    }

    public async IAsyncEnumerable<IssueRelationship> GetIssueRelationshipsAsync(
        RepositoryRef repository,
        long issueNumber,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var links = await client.GetIssueLinksAsync(repository.Id, issueNumber, cancellationToken).ConfigureAwait(false);
        foreach (var link in links)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relationship = link.LinkType switch
            {
                "relates_to" => "related",
                "blocks" => "blocks",
                "is_blocked_by" => "blocked-by",
                _ => link.LinkType,
            };

            var linkedRepository = link.ProjectId.ToString(CultureInfo.InvariantCulture) == repository.Id
                ? repository
                : new RepositoryRef($"gitlab/{link.ProjectId}", repository.OwnerOrNamespace, repository.Name);

            yield return new IssueRelationship(relationship, linkedRepository, link.Iid);
        }
    }

    public bool IsTrustedAttachmentHost(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        return trustedAttachmentHostSuffixes.Any(suffix =>
            url.Host.Equals(suffix, StringComparison.OrdinalIgnoreCase) ||
            url.Host.EndsWith("." + suffix, StringComparison.OrdinalIgnoreCase));
    }

    public async ValueTask<DownloadedAttachment> DownloadAttachmentAsync(
        ProviderAttachment attachment,
        string destinationDirectory,
        long maxSizeBytes,
        CancellationToken cancellationToken)
    {
        var httpClient = IsTrustedAttachmentHost(attachment.Url) ? authenticatedAttachmentClient : anonymousAttachmentClient;

        using var request = new HttpRequestMessage(HttpMethod.Get, attachment.Url);
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        if (response.Content.Headers.ContentLength is { } declaredLength && declaredLength > maxSizeBytes)
        {
            throw new AttachmentTooLargeException(
                $"Attachment '{attachment.SuggestedFileName}' declares {declaredLength} bytes, exceeding the {maxSizeBytes}-byte limit.");
        }

        var destinationPath = AttachmentFileNames.ResolveSafeDestination(destinationDirectory, attachment.SuggestedFileName);
        Directory.CreateDirectory(destinationDirectory);

        await using var sourceStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var destinationStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);

        var buffer = new byte[81920];
        long totalRead = 0;
        int read;
        while ((read = await sourceStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            totalRead += read;
            if (totalRead > maxSizeBytes)
            {
                destinationStream.Close();
                File.Delete(destinationPath);
                throw new AttachmentTooLargeException(
                    $"Attachment '{attachment.SuggestedFileName}' exceeded the {maxSizeBytes}-byte limit while streaming.");
            }

            await destinationStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        return new DownloadedAttachment(destinationPath, Path.GetFileName(destinationPath), totalRead);
    }

    private static ProviderIssue ToProviderIssue(RepositoryRef repository, GitLabIssue issue) => new(
        repository,
        issue.Iid,
        issue.Title,
        issue.Description ?? string.Empty,
        issue.CreatedAt,
        issue.UpdatedAt,
        issue.Labels.ToHashSet(StringComparer.Ordinal),
        issue.Assignees.Select(a => a.Username).ToHashSet(StringComparer.Ordinal),
        new AttachmentSource("issue-description", issue.Iid.ToString(CultureInfo.InvariantCulture)));

    private static ProviderComment ToProviderComment(GitLabNote note, string surface, long parentNumber, string? threadId = null) => new(
        note.Id,
        note.Author.Username,
        note.Body,
        note.CreatedAt,
        note.UpdatedAt,
        new AttachmentSource(surface, parentNumber.ToString(CultureInfo.InvariantCulture), threadId ?? note.Id.ToString(CultureInfo.InvariantCulture)),
        note.Author.Username.Contains("bot", StringComparison.OrdinalIgnoreCase));

    private static ProviderMergeRequest ToProviderMergeRequest(RepositoryRef repository, GitLabMergeRequest mergeRequest) => new(
        repository,
        mergeRequest.Iid,
        mergeRequest.SourceBranch,
        mergeRequest.TargetBranch,
        mergeRequest.Title,
        mergeRequest.Description ?? string.Empty,
        mergeRequest.Draft || mergeRequest.Title.StartsWith(DraftTitlePrefix, StringComparison.OrdinalIgnoreCase),
        mergeRequest.State == "merged",
        mergeRequest.State == "closed",
        new AttachmentSource("merge-request-description", mergeRequest.Iid.ToString(CultureInfo.InvariantCulture)));
}
