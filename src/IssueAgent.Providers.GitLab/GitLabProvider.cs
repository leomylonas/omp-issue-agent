using System.Net;

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using IssueAgent.Providers;
using IssueAgent.Domain;


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
public sealed partial class GitLabProvider(
    GitLabApiClient client,
    HttpClient authenticatedAttachmentClient,
    HttpClient anonymousAttachmentClient,
    IReadOnlyList<string> trustedAttachmentAuthorities,
    string attachmentPathPrefix,
    string name,
    RetryPolicy? configuredRetryPolicy = null,
    Uri? webBaseUri = null) : IGitProvider
{
    private const string DraftTitlePrefix = "Draft: ";

    public string Name { get; } = name;
    private readonly RetryPolicy retryPolicy = configuredRetryPolicy ?? RetryPolicy.Default;

    public async ValueTask<ProviderIdentity> GetCurrentIdentityAsync(CancellationToken cancellationToken)
    {
        var user = await client.GetCurrentUserAsync(cancellationToken).ConfigureAwait(false);
        return new ProviderIdentity(user.Username, user.Name, user.CommitEmail ?? user.Email);
    }

    public async ValueTask<string> GetDefaultBranchAsync(RepositoryRef repository, CancellationToken cancellationToken)
    {
        var project = await client.GetProjectAsync(ProjectAddress(repository), cancellationToken).ConfigureAwait(false);
        return project.DefaultBranch ?? throw new InvalidOperationException($"GitLab project '{ProjectAddress(repository)}' did not report a default branch.");
    }

    public async IAsyncEnumerable<IssueSummary> DiscoverAssignedOpenIssuesAsync(
        RepositoryRef repository,
        string identity,
        DateTimeOffset startDate,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var issues = await client.GetIssuesAsync(ProjectAddress(repository), identity, "opened", cancellationToken).ConfigureAwait(false);
        foreach (var issue in issues)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (issue.CreatedAt < startDate ||
                !issue.Assignees.Any(assignee => string.Equals(assignee.Username, identity, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            yield return new IssueSummary(
                issue.Iid,
                issue.Title,
                issue.CreatedAt,
                issue.Assignees.Select(a => a.Username).ToHashSet(StringComparer.OrdinalIgnoreCase));
        }
    }

    public async IAsyncEnumerable<IssueSummary> DiscoverManagedIssuesAsync(
        RepositoryRef repository,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var issues = await client.GetIssuesAsync(ProjectAddress(repository), assigneeUsername: null, "all", cancellationToken).ConfigureAwait(false);
        foreach (var issue in issues)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!issue.Labels.Any(label => label.StartsWith("agent:phase:", StringComparison.Ordinal))) continue;
            yield return new IssueSummary(
                issue.Iid,
                issue.Title,
                issue.CreatedAt,
                issue.Assignees.Select(assignee => assignee.Username).ToHashSet(StringComparer.OrdinalIgnoreCase));
        }
    }

    public async ValueTask<ProviderIssue> GetIssueAsync(RepositoryRef repository, long issueNumber, CancellationToken cancellationToken)
    {
        var issue = await client.GetIssueAsync(ProjectAddress(repository), issueNumber, cancellationToken).ConfigureAwait(false);
        return ToProviderIssue(repository, issue);
    }

    public async IAsyncEnumerable<ProviderComment> GetIssueCommentsAsync(
        RepositoryRef repository,
        long issueNumber,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var notes = await client.GetIssueNotesAsync(ProjectAddress(repository), issueNumber, cancellationToken).ConfigureAwait(false);
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
        var note = await client.CreateIssueNoteAsync(ProjectAddress(repository), issueNumber, body, cancellationToken).ConfigureAwait(false);
        return ToProviderComment(note, "issue-comment", issueNumber);
    }

    public async ValueTask<ProviderComment> UpdateIssueCommentAsync(RepositoryRef repository, long issueNumber, long commentId, string body, CancellationToken cancellationToken)
    {
        var note = await client.UpdateIssueNoteAsync(ProjectAddress(repository), issueNumber, commentId, body, cancellationToken).ConfigureAwait(false);
        return ToProviderComment(note, "issue-comment", issueNumber);
    }

    public async ValueTask<IReadOnlySet<string>> GetLabelsAsync(ProviderWorkItemReference workItem, CancellationToken cancellationToken)
    {
        if (workItem.Kind == ProviderWorkItemKind.Issue)
        {
            var issue = await client.GetIssueAsync(ProjectAddress(workItem.Repository), workItem.Number, cancellationToken).ConfigureAwait(false);
            return issue.Labels.ToHashSet(StringComparer.Ordinal);
        }

        var mergeRequest = await client.GetMergeRequestAsync(ProjectAddress(workItem.Repository), workItem.Number, cancellationToken).ConfigureAwait(false);
        return mergeRequest.Labels.ToHashSet(StringComparer.Ordinal);
    }

    public async ValueTask AddLabelsAsync(ProviderWorkItemReference workItem, IReadOnlyCollection<string> labels, CancellationToken cancellationToken)
    {
        if (workItem.Kind == ProviderWorkItemKind.Issue)
        {
            await client.UpdateIssueLabelsAsync(ProjectAddress(workItem.Repository), workItem.Number, labels, null, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await client.UpdateMergeRequestLabelsAsync(ProjectAddress(workItem.Repository), workItem.Number, labels, null, cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask RemoveLabelAsync(ProviderWorkItemReference workItem, string label, CancellationToken cancellationToken)
    {
        if (workItem.Kind == ProviderWorkItemKind.Issue)
        {
            await client.UpdateIssueLabelsAsync(ProjectAddress(workItem.Repository), workItem.Number, null, [label], cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await client.UpdateMergeRequestLabelsAsync(ProjectAddress(workItem.Repository), workItem.Number, null, [label], cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask EnsureLabelAsync(RepositoryRef repository, ProviderLabel label, CancellationToken cancellationToken)
    {
        if (await client.FindLabelAsync(ProjectAddress(repository), label.Name, cancellationToken).ConfigureAwait(false) is not null)
        {
            return;
        }

        try
        {
            await client.CreateLabelAsync(ProjectAddress(repository), label.Name, label.Color, label.Description, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception) when (exception.StatusCode == HttpStatusCode.Conflict)
        {
            if (await client.FindLabelAsync(ProjectAddress(repository), label.Name, cancellationToken).ConfigureAwait(false) is null)
            {
                throw;
            }
        }
    }

    public async ValueTask<ProviderMergeRequest?> FindMergeRequestAsync(RepositoryRef repository, string sourceBranch, string targetBranch, CancellationToken cancellationToken)
    {
        var mergeRequests = await client.FindMergeRequestsAsync(ProjectAddress(repository), sourceBranch, targetBranch, cancellationToken).ConfigureAwait(false);
        var match = mergeRequests.Count > 0 ? mergeRequests[0] : null;
        return match is null ? null : ToProviderMergeRequest(repository, match);
    }

    public async ValueTask<ProviderMergeRequest> CreateDraftMergeRequestAsync(CreateMergeRequestRequest request, CancellationToken cancellationToken)
    {
        var title = request.Title.StartsWith(DraftTitlePrefix, StringComparison.OrdinalIgnoreCase) ? request.Title : DraftTitlePrefix + request.Title;
        var created = await client.CreateMergeRequestAsync(ProjectAddress(request.Repository), request.SourceBranch, request.TargetBranch, title, request.Body, cancellationToken)
            .ConfigureAwait(false);
        return ToProviderMergeRequest(request.Repository, created);
    }

    public async ValueTask<ProviderMergeRequest> GetMergeRequestAsync(RepositoryRef repository, long number, CancellationToken cancellationToken)
    {
        var mergeRequest = await client.GetMergeRequestAsync(ProjectAddress(repository), number, cancellationToken).ConfigureAwait(false);
        return ToProviderMergeRequest(repository, mergeRequest);
    }

    public async IAsyncEnumerable<ProviderComment> GetMergeRequestCommentsAsync(
        RepositoryRef repository,
        long number,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var notes = await client.GetMergeRequestNotesAsync(ProjectAddress(repository), number, cancellationToken).ConfigureAwait(false);
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
        var discussions = await client.GetMergeRequestDiscussionsAsync(ProjectAddress(repository), number, cancellationToken).ConfigureAwait(false);
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
        var links = await client.GetIssueLinksAsync(ProjectAddress(repository), issueNumber, cancellationToken).ConfigureAwait(false);
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

            var linkedRepository = ToLinkedRepository(link, repository);

            yield return new IssueRelationship(relationship, linkedRepository, link.Iid);
        }
    }

    public Uri? ResolveAttachmentUrl(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        if (url.IsAbsoluteUri)
        {
            return url.Scheme is "http" or "https" ? url : null;
        }

        var relativePath = url.OriginalString;
        if (webBaseUri is null ||
            relativePath.Length == 0 ||
            relativePath[0] != '/' ||
            relativePath.StartsWith("//", StringComparison.Ordinal))
        {
            return null;
        }

        var resolved = new Uri(webBaseUri, relativePath[1..]);
        return IsDocumentedAttachmentPath(resolved.AbsolutePath) ? resolved : null;
    }

    public bool IsTrustedAttachmentHost(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        return url.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
            trustedAttachmentAuthorities.Contains(url.Authority, StringComparer.OrdinalIgnoreCase) &&
            IsDocumentedAttachmentPath(url.AbsolutePath);
    }

    private bool IsDocumentedAttachmentPath(string absolutePath)
    {
        if (attachmentPathPrefix.Length == 0)
        {
            return DocumentedAttachmentPathRegex().IsMatch(absolutePath);
        }

        return absolutePath.Length > attachmentPathPrefix.Length &&
            absolutePath.AsSpan().StartsWith(attachmentPathPrefix, StringComparison.Ordinal) &&
            absolutePath[attachmentPathPrefix.Length] == '/' &&
            DocumentedAttachmentPathRegex().IsMatch(absolutePath.AsSpan(attachmentPathPrefix.Length));
    }

    [GeneratedRegex(@"^(?:/uploads|/-/(?:project|group)/[0-9]+/uploads)/[0-9A-Fa-f]{32}/[^/]+$", RegexOptions.CultureInvariant)]
    private static partial Regex DocumentedAttachmentPathRegex();

    public async ValueTask<DownloadedAttachment> DownloadAttachmentAsync(
        ProviderAttachment attachment,
        string destinationDirectory,
        long maxSizeBytes,
        CancellationToken cancellationToken)
    {
        var httpClient = IsTrustedAttachmentHost(attachment.Url) ? authenticatedAttachmentClient : anonymousAttachmentClient;

        var destinationPath = AttachmentFileNames.ResolveSafeDestination(destinationDirectory, attachment.SuggestedFileName);
        var totalRead = await ProviderRetryPolicy.SendAndMaterializeAsync(
            token => SendAttachmentRequestAsync(attachment, httpClient, token),
            async (response, token) =>
            {
                if (attachment.RequiresAttachmentContentDisposition &&
                    !string.Equals(response.Content.Headers.ContentDisposition?.DispositionType, "attachment", StringComparison.OrdinalIgnoreCase))
                {
                    throw new AttachmentNotClassifiedException(
                        $"Extensionless attachment candidate '{attachment.Url}' did not return Content-Disposition: attachment.");
                }

                if (response.Content.Headers.ContentLength is { } declaredLength && declaredLength > maxSizeBytes)
                {
                    throw new AttachmentTooLargeException(
                        $"Attachment '{attachment.SuggestedFileName}' declares {declaredLength} bytes, exceeding the {maxSizeBytes}-byte limit.");
                }

                return await AttachmentDownloadWriter.WriteAsync(
                    response.Content,
                    destinationPath,
                    maxSizeBytes,
                    attachment.SuggestedFileName,
                    token).ConfigureAwait(false);
            },
            cancellationToken,
            retryPolicy).ConfigureAwait(false);
        return new DownloadedAttachment(destinationPath, Path.GetFileName(destinationPath), totalRead);
    }

    /// <summary>Authentication follows only documented provider attachment redirects. Redirects to
    /// arbitrary paths or hosts are rejected before a second credential-bearing request is sent.</summary>
    private async Task<HttpResponseMessage> SendAttachmentRequestAsync(
        ProviderAttachment attachment,
        HttpClient initialClient,
        CancellationToken cancellationToken)
    {
        var url = attachment.Url;
        var client = initialClient;
        for (var redirects = 0; ; redirects++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (client == anonymousAttachmentClient)
            {
                request.Options.Set(IssueAgent.Git.TlsHttpHandlerFactory.ValidatedAddressesOptionKey, attachment.ValidatedAddresses!);
            }

            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if ((int)response.StatusCode is < 300 or >= 400)
            {
                return response;
            }

            var location = response.Headers.Location;
            if (client != authenticatedAttachmentClient ||
                location is null ||
                redirects == 4 ||
                !IsTrustedAttachmentHost(url = new Uri(url, location)))
            {
                response.Dispose();
                throw new AttachmentRedirectRejectedException(
                    $"Attachment redirect from '{attachment.Url}' was not a trusted provider attachment URL.");
            }

            response.Dispose();
        }
    }

    private static ProviderIssue ToProviderIssue(RepositoryRef repository, GitLabIssue issue) => new(
        repository,
        issue.Iid,
        issue.Title,
        issue.Description ?? string.Empty,
        issue.CreatedAt,
        issue.UpdatedAt,
        issue.Labels.ToHashSet(StringComparer.Ordinal),
        issue.Assignees.Select(a => a.Username).ToHashSet(StringComparer.OrdinalIgnoreCase),
        new AttachmentSource("issue-description", issue.Iid.ToString(CultureInfo.InvariantCulture)));

    private static ProviderComment ToProviderComment(GitLabNote note, string surface, long parentNumber, string? threadId = null) => new(
        note.Id,
        note.Author.Username,
        note.Body,
        note.CreatedAt,
        note.UpdatedAt,
        new AttachmentSource(surface, parentNumber.ToString(CultureInfo.InvariantCulture), threadId ?? note.Id.ToString(CultureInfo.InvariantCulture)),
        note.Author.Bot || string.Equals(note.Author.UserType, "bot", StringComparison.OrdinalIgnoreCase) ||
        note.Author.Username.EndsWith("-bot", StringComparison.OrdinalIgnoreCase) ||
        note.Author.Username.EndsWith("[bot]", StringComparison.Ordinal));

    /// <summary>GitLab's REST API identifies projects by their namespace path. Repository IDs are
    /// local workspace keys and must never be forwarded as provider project addresses.</summary>
    private static string ProjectAddress(RepositoryRef repository) =>
        $"{repository.OwnerOrNamespace}/{repository.Name}".Trim('/');

    /// <summary>Resolves the repository a linked GitLab issue belongs to. Returns
    /// <paramref name="rootRepository"/> unchanged for a same-project link — matched first by
    /// <see cref="GitLabIssueLink.ProjectId"/> against the numeric convention, then by owner/name
    /// parsed from <see cref="GitLabIssueLink.WebUrl"/> against the path convention — so the root's
    /// originally configured <c>Id</c> (and its workspace/bare-repo directory) is preserved rather
    /// than aliased under a different identifier for the same project. A genuinely different project
    /// gets <see cref="GitLabIssueLink.ProjectId"/> (GitLab's numeric project id, always a directly
    /// usable API identifier regardless of which convention <paramref name="rootRepository"/>'s own
    /// <c>Id</c> uses) rather than a synthesized string the GitLab API cannot resolve.</summary>
    private static RepositoryRef ToLinkedRepository(GitLabIssueLink link, RepositoryRef rootRepository)
    {
        var linkProjectId = link.ProjectId.ToString(CultureInfo.InvariantCulture);
        if (linkProjectId == rootRepository.Id)
        {
            return rootRepository;
        }

        if (Uri.TryCreate(link.WebUrl, UriKind.Absolute, out var issueUri))
        {
            var projectPath = issueUri.AbsolutePath.Split("/-/", 2, StringSplitOptions.None)[0].Trim('/');
            var separator = projectPath.LastIndexOf('/');
            if (separator > 0)
            {
                var owner = projectPath[..separator];
                var name = projectPath[(separator + 1)..];
                if (string.Equals(owner, rootRepository.OwnerOrNamespace, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(name, rootRepository.Name, StringComparison.OrdinalIgnoreCase))
                {
                    return rootRepository;
                }

                return new RepositoryRef(linkProjectId, owner, name);
            }
        }

        throw new InvalidOperationException($"GitLab linked issue {link.Iid} did not include a project URL.");
    }

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
        new AttachmentSource("merge-request-description", mergeRequest.Iid.ToString(CultureInfo.InvariantCulture)),
        ParseWebUrl(mergeRequest.WebUrl));

    private static Uri? ParseWebUrl(string? webUrl) =>
        Uri.TryCreate(webUrl, UriKind.Absolute, out var uri) ? uri : null;
}
