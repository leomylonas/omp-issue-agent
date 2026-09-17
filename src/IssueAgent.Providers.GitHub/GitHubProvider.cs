using System.Runtime.CompilerServices;
using IssueAgent.Providers;
using Octokit;

namespace IssueAgent.Providers.GitHub;

/// <summary>
/// GitHub implementation of <see cref="IGitProvider"/>. REST operations use Octokit.NET; review
/// thread resolution and cross-issue relationship discovery use the raw HTTP clients in this
/// assembly because Octokit does not expose GraphQL review-thread resolution or issue timeline
/// events with strongly typed models.
/// </summary>
public sealed class GitHubProvider(
    IGitHubClient client,
    GitHubGraphQlClient graphQlClient,
    GitHubTimelineClient timelineClient,
    HttpClient authenticatedAttachmentClient,
    HttpClient anonymousAttachmentClient,
    IReadOnlyList<string> trustedAttachmentHostSuffixes,
    string name) : IGitProvider
{
    public string Name { get; } = name;

    public async ValueTask<ProviderIdentity> GetCurrentIdentityAsync(CancellationToken cancellationToken)
    {
        var user = await client.User.Current().ConfigureAwait(false);
        return new ProviderIdentity(user.Login, user.Name ?? user.Login);
    }

    public async IAsyncEnumerable<IssueSummary> DiscoverAssignedOpenIssuesAsync(
        RepositoryRef repository,
        string identity,
        DateTimeOffset startDate,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var request = new RepositoryIssueRequest
        {
            Assignee = identity,
            State = ItemStateFilter.Open,
            SortProperty = IssueSort.Created,
            SortDirection = SortDirection.Ascending,
        };

        var issues = await client.Issue
            .GetAllForRepository(repository.OwnerOrNamespace, repository.Name, request)
            .ConfigureAwait(false);

        foreach (var issue in issues)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (issue.PullRequest is not null)
            {
                continue;
            }

            if (issue.CreatedAt < startDate)
            {
                continue;
            }

            yield return new IssueSummary(
                issue.Number,
                issue.Title,
                issue.CreatedAt,
                issue.Assignees.Select(a => a.Login).ToHashSet(StringComparer.Ordinal));
        }
    }

    public async ValueTask<ProviderIssue> GetIssueAsync(
        RepositoryRef repository,
        long issueNumber,
        CancellationToken cancellationToken)
    {
        var issue = await client.Issue
            .Get(repository.OwnerOrNamespace, repository.Name, checked((int)issueNumber))
            .ConfigureAwait(false);

        return new ProviderIssue(
            repository,
            issue.Number,
            issue.Title,
            issue.Body ?? string.Empty,
            issue.CreatedAt,
            issue.UpdatedAt ?? issue.CreatedAt,
            issue.Labels.Select(l => l.Name).ToHashSet(StringComparer.Ordinal),
            issue.Assignees.Select(a => a.Login).ToHashSet(StringComparer.Ordinal),
            new AttachmentSource("issue-description", issue.Number.ToString(System.Globalization.CultureInfo.InvariantCulture)));
    }

    public async IAsyncEnumerable<ProviderComment> GetIssueCommentsAsync(
        RepositoryRef repository,
        long issueNumber,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var comments = await client.Issue.Comment
            .GetAllForIssue(repository.OwnerOrNamespace, repository.Name, checked((int)issueNumber))
            .ConfigureAwait(false);

        foreach (var comment in comments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return ToProviderComment(comment, issueNumber);
        }
    }

    public async ValueTask<ProviderComment> CreateIssueCommentAsync(
        RepositoryRef repository,
        long issueNumber,
        string body,
        CancellationToken cancellationToken)
    {
        var comment = await client.Issue.Comment
            .Create(repository.OwnerOrNamespace, repository.Name, checked((int)issueNumber), body)
            .ConfigureAwait(false);
        return ToProviderComment(comment, issueNumber);
    }

    public async ValueTask<ProviderComment> UpdateIssueCommentAsync(
        RepositoryRef repository,
        long issueNumber,
        long commentId,
        string body,
        CancellationToken cancellationToken)
    {
        var comment = await client.Issue.Comment
            .Update(repository.OwnerOrNamespace, repository.Name, checked((int)commentId), body)
            .ConfigureAwait(false);
        return ToProviderComment(comment, issueNumber);
    }

    public async ValueTask<IReadOnlySet<string>> GetLabelsAsync(
        ProviderWorkItemReference workItem,
        CancellationToken cancellationToken)
    {
        var labels = await client.Issue.Labels
            .GetAllForIssue(workItem.Repository.OwnerOrNamespace, workItem.Repository.Name, checked((int)workItem.Number))
            .ConfigureAwait(false);
        return labels.Select(l => l.Name).ToHashSet(StringComparer.Ordinal);
    }

    public async ValueTask AddLabelsAsync(
        ProviderWorkItemReference workItem,
        IReadOnlyCollection<string> labels,
        CancellationToken cancellationToken)
    {
        await client.Issue.Labels
            .AddToIssue(workItem.Repository.OwnerOrNamespace, workItem.Repository.Name, checked((int)workItem.Number), [.. labels])
            .ConfigureAwait(false);
    }

    public async ValueTask RemoveLabelAsync(
        ProviderWorkItemReference workItem,
        string label,
        CancellationToken cancellationToken)
    {
        await client.Issue.Labels
            .RemoveFromIssue(workItem.Repository.OwnerOrNamespace, workItem.Repository.Name, checked((int)workItem.Number), label)
            .ConfigureAwait(false);
    }

    public async ValueTask EnsureLabelAsync(
        RepositoryRef repository,
        ProviderLabel label,
        CancellationToken cancellationToken)
    {
        try
        {
            await client.Issue.Labels
                .Get(repository.OwnerOrNamespace, repository.Name, label.Name)
                .ConfigureAwait(false);
        }
        catch (NotFoundException)
        {
            await client.Issue.Labels
                .Create(repository.OwnerOrNamespace, repository.Name, new NewLabel(label.Name, label.Color) { Description = label.Description })
                .ConfigureAwait(false);
        }
    }

    public async ValueTask<ProviderMergeRequest?> FindMergeRequestAsync(
        RepositoryRef repository,
        string sourceBranch,
        string targetBranch,
        CancellationToken cancellationToken)
    {
        var request = new PullRequestRequest
        {
            Head = $"{repository.OwnerOrNamespace}:{sourceBranch}",
            Base = targetBranch,
            State = ItemStateFilter.All,
        };

        var pullRequests = await client.PullRequest
            .GetAllForRepository(repository.OwnerOrNamespace, repository.Name, request)
            .ConfigureAwait(false);

        var match = pullRequests.Count > 0 ? pullRequests[0] : null;
        return match is null ? null : ToProviderMergeRequest(repository, match);
    }

    public async ValueTask<ProviderMergeRequest> CreateDraftMergeRequestAsync(
        CreateMergeRequestRequest request,
        CancellationToken cancellationToken)
    {
        var newPullRequest = new NewPullRequest(request.Title, request.SourceBranch, request.TargetBranch)
        {
            Body = request.Body,
            Draft = true,
        };

        var created = await client.PullRequest
            .Create(request.Repository.OwnerOrNamespace, request.Repository.Name, newPullRequest)
            .ConfigureAwait(false);

        return ToProviderMergeRequest(request.Repository, created);
    }

    public async ValueTask<ProviderMergeRequest> GetMergeRequestAsync(
        RepositoryRef repository,
        long number,
        CancellationToken cancellationToken)
    {
        var pullRequest = await client.PullRequest
            .Get(repository.OwnerOrNamespace, repository.Name, checked((int)number))
            .ConfigureAwait(false);
        return ToProviderMergeRequest(repository, pullRequest);
    }

    public async IAsyncEnumerable<ProviderComment> GetMergeRequestCommentsAsync(
        RepositoryRef repository,
        long number,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var comment in GetIssueCommentsAsync(repository, number, cancellationToken).ConfigureAwait(false))
        {
            yield return comment;
        }
    }

    public async IAsyncEnumerable<ProviderReviewThread> GetReviewThreadsAsync(
        RepositoryRef repository,
        long number,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var thread in graphQlClient
            .GetReviewThreadsAsync(repository.OwnerOrNamespace, repository.Name, checked((int)number), cancellationToken)
            .ConfigureAwait(false))
        {
            yield return thread;
        }
    }

    public async IAsyncEnumerable<IssueRelationship> GetIssueRelationshipsAsync(
        RepositoryRef repository,
        long issueNumber,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var reference in timelineClient
            .GetCrossReferencedIssuesAsync(repository.OwnerOrNamespace, repository.Name, issueNumber, cancellationToken)
            .ConfigureAwait(false))
        {
            var relatedRepository = string.Equals(reference.Owner, repository.OwnerOrNamespace, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(reference.Name, repository.Name, StringComparison.OrdinalIgnoreCase)
                ? repository
                : new RepositoryRef($"github/{reference.Owner}/{reference.Name}", reference.Owner, reference.Name);

            yield return new IssueRelationship("related", relatedRepository, reference.Number);
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
        using var response = await httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        if (response.Content.Headers.ContentLength is { } declaredLength && declaredLength > maxSizeBytes)
        {
            throw new AttachmentTooLargeException(
                $"Attachment '{attachment.SuggestedFileName}' declares {declaredLength} bytes, exceeding the {maxSizeBytes}-byte limit.");
        }

        var destinationPath = AttachmentFileNames.ResolveSafeDestination(destinationDirectory, attachment.SuggestedFileName);
        Directory.CreateDirectory(destinationDirectory);

        await using var sourceStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var destinationStream = new FileStream(destinationPath, System.IO.FileMode.Create, System.IO.FileAccess.Write, System.IO.FileShare.None);

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

    private static ProviderComment ToProviderComment(IssueComment comment, long issueNumber) => new(
        comment.Id,
        comment.User.Login,
        comment.Body,
        comment.CreatedAt,
        comment.UpdatedAt ?? comment.CreatedAt,
        new AttachmentSource("issue-comment", issueNumber.ToString(System.Globalization.CultureInfo.InvariantCulture), comment.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        comment.User.Login.EndsWith("[bot]", StringComparison.Ordinal));

    private static ProviderMergeRequest ToProviderMergeRequest(RepositoryRef repository, PullRequest pullRequest) => new(
        repository,
        pullRequest.Number,
        pullRequest.Head.Ref,
        pullRequest.Base.Ref,
        pullRequest.Title,
        pullRequest.Body ?? string.Empty,
        pullRequest.Draft,
        pullRequest.Merged,
        pullRequest.State.Value == ItemState.Closed && !pullRequest.Merged,
        new AttachmentSource("merge-request-description", pullRequest.Number.ToString(System.Globalization.CultureInfo.InvariantCulture)));
}
