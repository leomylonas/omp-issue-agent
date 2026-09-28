using System.Net;

using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Text.Json.Serialization;
using IssueAgent.Providers;
using IssueAgent.Domain;

using Octokit;

namespace IssueAgent.Providers.GitHub;


/// <summary>
/// GitHub implementation of <see cref="IGitProvider"/>. REST operations use Octokit.NET; review
/// thread resolution and cross-issue relationship discovery use the raw HTTP clients in this
/// assembly because Octokit does not expose GraphQL review-thread resolution or issue timeline
/// events with strongly typed models.
/// </summary>
public sealed partial class GitHubProvider(
    IGitHubClient client,
    GitHubGraphQlClient graphQlClient,
    GitHubTimelineClient timelineClient,
    HttpClient mutationClient,
    HttpClient authenticatedAttachmentClient,
    HttpClient anonymousAttachmentClient,
    IReadOnlyList<string> trustedAttachmentAuthorities,
    bool trustsGitHubDotComAttachmentHosts,
    string attachmentPathPrefix,
    string name,
    RetryPolicy? configuredRetryPolicy = null) : IGitProvider
{
    private readonly RetryPolicy retryPolicy = configuredRetryPolicy ?? RetryPolicy.Default;
    public string Name { get; } = name;

    public async ValueTask<ProviderIdentity> GetCurrentIdentityAsync(CancellationToken cancellationToken)
    {
        var user = await ExecuteReadWithCancellationAsync(
            token => client.Connection.Get<User>(new Uri("user", UriKind.Relative), null, null, token),
            cancellationToken).ConfigureAwait(false);
        return new ProviderIdentity(
            user.Login,
            user.Name ?? user.Login,
            user.Email ?? (trustsGitHubDotComAttachmentHosts ? $"{user.Id}+{user.Login}@users.noreply.github.com" : null));
    }

    public async ValueTask<string> GetDefaultBranchAsync(RepositoryRef repository, CancellationToken cancellationToken)
    {
        var repo = await ExecuteReadWithCancellationAsync(
            token => client.Connection.Get<Repository>(
                new Uri($"repos/{repository.OwnerOrNamespace}/{repository.Name}", UriKind.Relative),
                null,
                null,
                token),
            cancellationToken).ConfigureAwait(false);
        return repo.DefaultBranch;
    }

    public async IAsyncEnumerable<IssueSummary> DiscoverAssignedOpenIssuesAsync(
        RepositoryRef repository,
        string identity,
        DateTimeOffset startDate,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var issues = await GetAllReadPagesAsync<Issue>(
            new Uri($"repos/{repository.OwnerOrNamespace}/{repository.Name}/issues?assignee={Uri.EscapeDataString(identity)}&state=open&sort=created&direction=asc&per_page=100", UriKind.Relative),
            cancellationToken).ConfigureAwait(false);

        foreach (var issue in issues)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (issue.PullRequest is not null ||
                issue.CreatedAt < startDate ||
                !issue.Assignees.Any(assignee => string.Equals(assignee.Login, identity, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            yield return new IssueSummary(
                issue.Number,
                issue.Title,
                issue.CreatedAt,
                issue.Assignees.Select(a => a.Login).ToHashSet(StringComparer.OrdinalIgnoreCase));
        }
    }

    public async IAsyncEnumerable<IssueSummary> DiscoverManagedIssuesAsync(
        RepositoryRef repository,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var issues = await GetAllReadPagesAsync<Issue>(
            new Uri($"repos/{repository.OwnerOrNamespace}/{repository.Name}/issues?state=all&sort=updated&direction=asc&per_page=100", UriKind.Relative),
            cancellationToken).ConfigureAwait(false);
        foreach (var issue in issues)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (issue.PullRequest is not null ||
                !issue.Labels.Any(label => label.Name.StartsWith("agent:phase:", StringComparison.Ordinal)))
            {
                continue;
            }
            yield return new IssueSummary(
                issue.Number,
                issue.Title,
                issue.CreatedAt,
                issue.Assignees.Select(assignee => assignee.Login).ToHashSet(StringComparer.OrdinalIgnoreCase));
        }
    }

    public async ValueTask<ProviderIssue> GetIssueAsync(
        RepositoryRef repository,
        long issueNumber,
        CancellationToken cancellationToken)
    {
        var issue = await ExecuteReadWithCancellationAsync(
            token => client.Connection.Get<Issue>(
                new Uri($"repos/{repository.OwnerOrNamespace}/{repository.Name}/issues/{checked((int)issueNumber)}", UriKind.Relative),
                null,
                null,
                token),
            cancellationToken).ConfigureAwait(false);

        return new ProviderIssue(
            repository,
            issue.Number,
            issue.Title,
            issue.Body ?? string.Empty,
            issue.CreatedAt,
            issue.UpdatedAt ?? issue.CreatedAt,
            issue.Labels.Select(l => l.Name).ToHashSet(StringComparer.Ordinal),
            issue.Assignees.Select(a => a.Login).ToHashSet(StringComparer.OrdinalIgnoreCase),
            new AttachmentSource("issue-description", issue.Number.ToString(System.Globalization.CultureInfo.InvariantCulture)));
    }

    public async IAsyncEnumerable<ProviderComment> GetIssueCommentsAsync(
        RepositoryRef repository,
        long issueNumber,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var comments = await GetAllReadPagesAsync<IssueComment>(
            new Uri($"repos/{repository.OwnerOrNamespace}/{repository.Name}/issues/{checked((int)issueNumber)}/comments?per_page=100", UriKind.Relative),
            cancellationToken).ConfigureAwait(false);

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
        var comment = await ExecuteWithRetryAsync(
            token => PostAsync<IssueComment>(client.Connection, new Uri($"repos/{repository.OwnerOrNamespace}/{repository.Name}/issues/{checked((int)issueNumber)}/comments", UriKind.Relative), new { body }, null, null, new Dictionary<string, string>(), token),
            cancellationToken,
            isIdempotent: false).ConfigureAwait(false);
        return ToProviderComment(comment, issueNumber);
    }

    public async ValueTask<ProviderComment> UpdateIssueCommentAsync(
        RepositoryRef repository,
        long issueNumber,
        long commentId,
        string body,
        CancellationToken cancellationToken)
    {
        using var response = await ProviderRetryPolicy.SendAsync(
            async token =>
            {
                using var request = new HttpRequestMessage(
                    HttpMethod.Patch,
                    new Uri($"repos/{repository.OwnerOrNamespace}/{repository.Name}/issues/comments/{commentId}", UriKind.Relative))
                {
                    Content = JsonContent.Create(new { body }),
                };
                return await mutationClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            },
            cancellationToken,
            retryPolicy: retryPolicy).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var comment = await response.Content.ReadFromJsonAsync<GitHubCommentResponse>(cancellationToken: cancellationToken)
            .ConfigureAwait(false) ?? throw new InvalidOperationException("GitHub did not return the updated comment.");
        return ToProviderComment(comment, issueNumber);
    }

    public async ValueTask<IReadOnlySet<string>> GetLabelsAsync(
        ProviderWorkItemReference workItem,
        CancellationToken cancellationToken)
    {
        var labels = await GetAllReadPagesAsync<Label>(
            new Uri($"repos/{workItem.Repository.OwnerOrNamespace}/{workItem.Repository.Name}/issues/{checked((int)workItem.Number)}/labels?per_page=100", UriKind.Relative),
            cancellationToken).ConfigureAwait(false);
        return labels.Select(l => l.Name).ToHashSet(StringComparer.Ordinal);
    }

    public async ValueTask AddLabelsAsync(
        ProviderWorkItemReference workItem,
        IReadOnlyCollection<string> labels,
        CancellationToken cancellationToken)
    {
        await ExecuteWithRetryAsync(
            token => PostAsync<IReadOnlyList<Label>>(client.Connection, new Uri($"repos/{workItem.Repository.OwnerOrNamespace}/{workItem.Repository.Name}/issues/{checked((int)workItem.Number)}/labels", UriKind.Relative), new { labels }, null, null, new Dictionary<string, string>(), token),
            cancellationToken,
            isIdempotent: true).ConfigureAwait(false);
    }

    public async ValueTask RemoveLabelAsync(
        ProviderWorkItemReference workItem,
        string label,
        CancellationToken cancellationToken)
    {
        using var response = await ProviderRetryPolicy.SendAsync(
            async token =>
            {
                using var request = new HttpRequestMessage(
                    HttpMethod.Delete,
                    new Uri($"repos/{workItem.Repository.OwnerOrNamespace}/{workItem.Repository.Name}/issues/{checked((int)workItem.Number)}/labels/{Uri.EscapeDataString(label)}", UriKind.Relative));
                return await mutationClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            },
            cancellationToken,
            retryPolicy: retryPolicy).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.NotFound)
        {
            response.EnsureSuccessStatusCode();
        }
    }

    public async ValueTask EnsureLabelAsync(
        RepositoryRef repository,
        ProviderLabel label,
        CancellationToken cancellationToken)
    {
        if (await LabelExistsAsync(repository, label.Name, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            await ExecuteWithRetryAsync(
                token => PostAsync<Label>(client.Connection, new Uri($"repos/{repository.OwnerOrNamespace}/{repository.Name}/labels", UriKind.Relative), new NewLabel(label.Name, label.Color) { Description = label.Description }, null, null, new Dictionary<string, string>(), token),
                cancellationToken,
                isIdempotent: false).ConfigureAwait(false);
        }
        catch (ApiException exception) when (exception.StatusCode == HttpStatusCode.UnprocessableEntity)
        {
            if (!await LabelExistsAsync(repository, label.Name, cancellationToken).ConfigureAwait(false))
            {
                throw;
            }
        }
    }

    public async ValueTask<ProviderMergeRequest?> FindMergeRequestAsync(
        RepositoryRef repository,
        string sourceBranch,
        string targetBranch,
        CancellationToken cancellationToken)
    {
        var pullRequests = await GetAllReadPagesAsync<PullRequest>(
            new Uri($"repos/{repository.OwnerOrNamespace}/{repository.Name}/pulls?head={Uri.EscapeDataString($"{repository.OwnerOrNamespace}:{sourceBranch}")}&base={Uri.EscapeDataString(targetBranch)}&state=all&per_page=100", UriKind.Relative),
            cancellationToken).ConfigureAwait(false);

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

        var created = await ExecuteWithRetryAsync(
            token => PostAsync<PullRequest>(client.Connection, new Uri($"repos/{request.Repository.OwnerOrNamespace}/{request.Repository.Name}/pulls", UriKind.Relative), newPullRequest, null, null, new Dictionary<string, string>(), token),
            cancellationToken,
            isIdempotent: false).ConfigureAwait(false);

        return ToProviderMergeRequest(request.Repository, created);
    }

    public async ValueTask<ProviderMergeRequest> GetMergeRequestAsync(
        RepositoryRef repository,
        long number,
        CancellationToken cancellationToken)
    {
        var pullRequest = await ExecuteReadWithCancellationAsync(
            token => client.Connection.Get<PullRequest>(
                new Uri($"repos/{repository.OwnerOrNamespace}/{repository.Name}/pulls/{checked((int)number)}", UriKind.Relative),
                null,
                null,
                token),
            cancellationToken).ConfigureAwait(false);
        return ToProviderMergeRequest(repository, pullRequest);
    }

    public async IAsyncEnumerable<ProviderComment> GetMergeRequestCommentsAsync(
        RepositoryRef repository,
        long number,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var comment in GetIssueCommentsAsync(repository, number, cancellationToken).ConfigureAwait(false))
        {
            yield return comment with
            {
                Source = new AttachmentSource(
                    "merge-request-comment",
                    number.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    comment.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            };
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

    [GeneratedRegex(@"^/user-attachments/(?:assets/[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}|files/[0-9]+/[^/]+)$", RegexOptions.CultureInvariant)]
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
            async token =>
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, attachment.Url);
                if (httpClient == anonymousAttachmentClient)
                {
                    request.Options.Set(IssueAgent.Git.TlsHttpHandlerFactory.ValidatedAddressesOptionKey, attachment.ValidatedAddresses!);
                }

                return await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            },
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
    private async Task<T> ExecuteReadWithCancellationAsync<T>(
        Func<CancellationToken, Task<IApiResponse<T>>> execute,
        CancellationToken cancellationToken)
    {
        var response = await ExecuteWithRetryAsync(
            () => execute(cancellationToken),
            cancellationToken).ConfigureAwait(false);
        return response.Body;
    }

    private async Task<IReadOnlyList<T>> GetAllReadPagesAsync<T>(Uri initialUri, CancellationToken cancellationToken)
    {
        var results = new List<T>();
        var visitedPageUris = new HashSet<string>(StringComparer.Ordinal);
        var nextUri = initialUri;
        do
        {
            var pageUri = nextUri.IsAbsoluteUri ? nextUri : new Uri(client.Connection.BaseAddress, nextUri);
            if (!visitedPageUris.Add(pageUri.AbsoluteUri))
            {
                throw new InvalidOperationException($"GitHub REST pagination repeated page URI '{pageUri}'.");
            }

            var response = await ExecuteWithRetryAsync(
                () => client.Connection.Get<IReadOnlyList<T>>(nextUri, null, null, cancellationToken),
                cancellationToken).ConfigureAwait(false);
            results.AddRange(response.Body);
            nextUri = GetNextPageUri(response.HttpResponse.Headers, client.Connection.BaseAddress);
        }
        while (nextUri is not null);

        return results;
    }
    private static Uri? GetNextPageUri(IReadOnlyDictionary<string, string> headers, Uri baseAddress)
    {
        if (!headers.TryGetValue("Link", out var links))
        {
            return null;
        }

        foreach (var link in links.Split(',', StringSplitOptions.TrimEntries))
        {
            var parts = link.Split(';', StringSplitOptions.TrimEntries);
            if (parts.Length <= 1 || !parts.Skip(1).Any(part => part == "rel=\"next\""))
            {
                continue;
            }

            var target = parts[0];
            if (target.Length < 3 || target[0] != '<' || target[^1] != '>' ||
                !Uri.IsWellFormedUriString(target[1..^1], UriKind.RelativeOrAbsolute) ||
                !Uri.TryCreate(baseAddress, target[1..^1], out var nextUri))
            {
                throw new InvalidOperationException($"GitHub REST pagination contained a malformed next-page URI '{target}'.");
            }

            if (Uri.Compare(nextUri, baseAddress, UriComponents.SchemeAndServer, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) != 0)
            {
                throw new InvalidOperationException($"GitHub REST pagination next-page URI '{nextUri}' has a different authority than '{baseAddress}'.");
            }

            return nextUri;
        }

        return null;
    }

    private static ProviderComment ToProviderComment(IssueComment comment, long issueNumber) => new(
        comment.Id,
        comment.User.Login,
        comment.Body,
        comment.CreatedAt,
        comment.UpdatedAt ?? comment.CreatedAt,
        new AttachmentSource("issue-comment", issueNumber.ToString(System.Globalization.CultureInfo.InvariantCulture), comment.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        comment.User.Login.EndsWith("[bot]", StringComparison.Ordinal));

    private async Task<bool> LabelExistsAsync(RepositoryRef repository, string labelName, CancellationToken cancellationToken)
    {
        try
        {
            await ExecuteReadWithCancellationAsync(
                token => client.Connection.Get<Label>(
                    new Uri($"repos/{repository.OwnerOrNamespace}/{repository.Name}/labels/{Uri.EscapeDataString(labelName)}", UriKind.Relative),
                    null,
                    null,
                    token),
                cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (NotFoundException)
        {
            return false;
        }
    }

    private Task<T> ExecuteWithRetryAsync<T>(
        Func<CancellationToken, Task<T>> execute,
        CancellationToken cancellationToken,
        bool isIdempotent = true) =>
        ExecuteWithRetryAsync(() => execute(cancellationToken), cancellationToken, isIdempotent);

    private static async Task<T> PostAsync<T>(
        IConnection connection,
        Uri uri,
        object body,
        string? accepts,
        string? contentType,
        IDictionary<string, string> parameters,
        CancellationToken cancellationToken)
    {
        var response = await connection
            .Post<T>(uri, body, accepts, contentType, parameters, cancellationToken)
            .ConfigureAwait(false);
        return response.Body;
    }

    private async Task<T> ExecuteWithRetryAsync<T>(
        Func<Task<T>> execute,
        CancellationToken cancellationToken,
        bool isIdempotent = true)
    {
        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await execute().WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (RateLimitExceededException exception)
            {
                var delay = GetRateLimitRetryAfter(exception, attempt);
                PollingRateLimitScheduling.ThrowIfEnabled(delay);
                if (attempt >= retryPolicy.MaxAttempts)
                {
                    throw;
                }

                await DelayForProviderInstructionAsync(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (SecondaryRateLimitExceededException exception)
            {
                var delay = GetRetryAfter(exception) ?? GetRateLimitFallbackDelay(attempt);
                PollingRateLimitScheduling.ThrowIfEnabled(delay);
                if (attempt >= retryPolicy.MaxAttempts)
                {
                    throw;
                }

                await DelayForProviderInstructionAsync(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (ApiException exception) when ((int)exception.StatusCode == (int)HttpStatusCode.TooManyRequests)
            {
                var delay = GetRetryAfter(exception) ?? GetRateLimitFallbackDelay(attempt);
                PollingRateLimitScheduling.ThrowIfEnabled(delay);
                if (attempt >= retryPolicy.MaxAttempts)
                {
                    throw;
                }

                await DelayForProviderInstructionAsync(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (ApiException exception) when (isIdempotent && ((int)exception.StatusCode == (int)HttpStatusCode.RequestTimeout || (int)exception.StatusCode >= 500) && attempt < retryPolicy.MaxAttempts)
            {
                await Task.Delay(retryPolicy.GetDelay(attempt), cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException) when (isIdempotent && attempt < retryPolicy.MaxAttempts)
            {
                await Task.Delay(retryPolicy.GetDelay(attempt), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (isIdempotent && !cancellationToken.IsCancellationRequested && attempt < retryPolicy.MaxAttempts)
            {
                await Task.Delay(retryPolicy.GetDelay(attempt), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static async Task DelayForProviderInstructionAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        PollingRateLimitScheduling.ThrowIfEnabled(delay);
        foreach (var chunk in GetProviderRetryDelayChunks(delay))
        {
            await Task.Delay(chunk, cancellationToken).ConfigureAwait(false);
        }
    }

    private static IEnumerable<TimeSpan> GetProviderRetryDelayChunks(TimeSpan delay)
    {
        while (delay > MaxRetryDelay)
        {
            yield return MaxRetryDelay;
            delay -= MaxRetryDelay;
        }

        yield return delay <= TimeSpan.Zero ? TimeSpan.Zero : delay;
    }


    private static TimeSpan? GetRetryAfter(ApiException exception)
    {
        if (exception.HttpResponse?.Headers is { } headers &&
            headers.TryGetValue("Retry-After", out var value))
        {
            if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var seconds) &&
                double.IsFinite(seconds) &&
                seconds >= TimeSpan.MinValue.TotalSeconds &&
                seconds <= TimeSpan.MaxValue.TotalSeconds)
            {
                return TimeSpan.FromSeconds(seconds);
            }

            if (DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var date))
            {
                return date - DateTimeOffset.UtcNow;
            }
        }

        return null;
    }

    private TimeSpan GetRateLimitRetryAfter(RateLimitExceededException exception, int attempt) =>
        GetRetryAfter(exception) ??
        (HasHeader(exception, "X-RateLimit-Reset")
            ? exception.GetRetryAfterTimeSpan()
            : GetRateLimitFallbackDelay(attempt));

    private static bool HasHeader(ApiException exception, string name) =>
        exception.HttpResponse?.Headers is { } headers && headers.TryGetValue(name, out _);

    private TimeSpan GetRateLimitFallbackDelay(int attempt) => retryPolicy.GetRateLimitFallbackDelay(attempt);

    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(5);

    private static ProviderComment ToProviderComment(GitHubCommentResponse comment, long issueNumber) => new(
        comment.Id,
        comment.User.Login,
        comment.Body,
        comment.CreatedAt,
        comment.UpdatedAt ?? comment.CreatedAt,
        new AttachmentSource("issue-comment", issueNumber.ToString(System.Globalization.CultureInfo.InvariantCulture), comment.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        comment.User.Login.EndsWith("[bot]", StringComparison.Ordinal));

    private sealed record GitHubCommentResponse(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("body")] string Body,
        [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
        [property: JsonPropertyName("updated_at")] DateTimeOffset? UpdatedAt,
        [property: JsonPropertyName("user")] GitHubCommentAuthor User);

    private sealed record GitHubCommentAuthor([property: JsonPropertyName("login")] string Login);
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
        new AttachmentSource("merge-request-description", pullRequest.Number.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        ParseWebUrl(pullRequest.HtmlUrl));

    private static Uri? ParseWebUrl(string? webUrl) =>
        Uri.TryCreate(webUrl, UriKind.Absolute, out var uri) ? uri : null;
}
