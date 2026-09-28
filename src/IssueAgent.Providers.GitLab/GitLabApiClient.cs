using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

using IssueAgent.Domain;

namespace IssueAgent.Providers.GitLab;

/// <summary>Thin typed REST client for the GitLab API v4 surface IssueAgent needs. GitLab access
/// never uses NGitLab in production; this hand-rolled client keeps the dependency footprint small
/// and the JSON contract explicit.</summary>
public sealed partial class GitLabApiClient(HttpClient httpClient, RetryPolicy retryPolicy)
{

    public async Task<GitLabProject> GetProjectAsync(string projectId, CancellationToken cancellationToken) =>
        await GetAsync($"projects/{Encode(projectId)}", GitLabJsonContext.Default.GitLabProject, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"GitLab project '{projectId}' was not found.");

    public async Task<GitLabUser> GetCurrentUserAsync(CancellationToken cancellationToken) =>
        await GetAsync("user", GitLabJsonContext.Default.GitLabUser, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("GitLab did not return the current user.");

    public async Task<IReadOnlyList<GitLabIssue>> GetIssuesAsync(
        string projectId,
        string? assigneeUsername,
        string state,
        CancellationToken cancellationToken)
    {
        var assignee = string.IsNullOrWhiteSpace(assigneeUsername)
            ? string.Empty
            : $"assignee_username={Uri.EscapeDataString(assigneeUsername)}&";
        var query = $"{assignee}state={state}&order_by=created_at&sort=asc&per_page=100";
        return await GetAllPagesAsync($"projects/{Encode(projectId)}/issues?{query}", GitLabJsonContext.Default.GitLabIssueArray, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<GitLabIssue> GetIssueAsync(string projectId, long issueIid, CancellationToken cancellationToken) =>
        await GetAsync($"projects/{Encode(projectId)}/issues/{issueIid}", GitLabJsonContext.Default.GitLabIssue, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"GitLab issue {issueIid} was not found.");

    public Task<IReadOnlyList<GitLabNote>> GetIssueNotesAsync(string projectId, long issueIid, CancellationToken cancellationToken) =>
        GetAllPagesAsync($"projects/{Encode(projectId)}/issues/{issueIid}/notes?per_page=100&sort=asc&order_by=created_at", GitLabJsonContext.Default.GitLabNoteArray, cancellationToken);

    public async Task<GitLabNote> CreateIssueNoteAsync(string projectId, long issueIid, string body, CancellationToken cancellationToken) =>
        await PostAsync($"projects/{Encode(projectId)}/issues/{issueIid}/notes", new GitLabCreateNoteRequest(body), GitLabJsonContext.Default.GitLabCreateNoteRequest, GitLabJsonContext.Default.GitLabNote, cancellationToken)
            .ConfigureAwait(false);

    public async Task<GitLabNote> UpdateIssueNoteAsync(string projectId, long issueIid, long noteId, string body, CancellationToken cancellationToken) =>
        await PutAsync($"projects/{Encode(projectId)}/issues/{issueIid}/notes/{noteId}", new GitLabCreateNoteRequest(body), GitLabJsonContext.Default.GitLabCreateNoteRequest, GitLabJsonContext.Default.GitLabNote, cancellationToken)
            .ConfigureAwait(false);

    public Task<IReadOnlyList<GitLabNote>> GetMergeRequestNotesAsync(string projectId, long mergeRequestIid, CancellationToken cancellationToken) =>
        GetAllPagesAsync($"projects/{Encode(projectId)}/merge_requests/{mergeRequestIid}/notes?per_page=100&sort=asc&order_by=created_at", GitLabJsonContext.Default.GitLabNoteArray, cancellationToken);

    public Task<IReadOnlyList<GitLabDiscussion>> GetMergeRequestDiscussionsAsync(string projectId, long mergeRequestIid, CancellationToken cancellationToken) =>
        GetAllPagesAsync($"projects/{Encode(projectId)}/merge_requests/{mergeRequestIid}/discussions?per_page=100", GitLabJsonContext.Default.GitLabDiscussionArray, cancellationToken);

    public async Task<IReadOnlyList<string>> UpdateIssueLabelsAsync(string projectId, long issueIid, IReadOnlyCollection<string>? addLabels, IReadOnlyCollection<string>? removeLabel, CancellationToken cancellationToken)
    {
        var body = new GitLabUpdateLabelsRequest(
            addLabels is { Count: > 0 } ? string.Join(',', addLabels) : null,
            removeLabel is { Count: > 0 } ? string.Join(',', removeLabel) : null);
        var issue = await PutAsync($"projects/{Encode(projectId)}/issues/{issueIid}", body, GitLabJsonContext.Default.GitLabUpdateLabelsRequest, GitLabJsonContext.Default.GitLabIssue, cancellationToken)
            .ConfigureAwait(false);
        return issue.Labels;
    }

    public async Task<GitLabLabel?> FindLabelAsync(string projectId, string name, CancellationToken cancellationToken)
    {
        var labels = await GetAllPagesAsync(
            $"projects/{Encode(projectId)}/labels?search={Uri.EscapeDataString(name)}&per_page=100",
            GitLabJsonContext.Default.GitLabLabelArray,
            cancellationToken).ConfigureAwait(false);
        return labels.FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.Ordinal));
    }

    public async Task<GitLabLabel> CreateLabelAsync(string projectId, string name, string color, string description, CancellationToken cancellationToken) =>
        await PostAsync($"projects/{Encode(projectId)}/labels", new GitLabCreateLabelRequest(name, "#" + color.TrimStart('#'), description), GitLabJsonContext.Default.GitLabCreateLabelRequest, GitLabJsonContext.Default.GitLabLabel, cancellationToken)
            .ConfigureAwait(false);

    public Task<IReadOnlyList<GitLabMergeRequest>> FindMergeRequestsAsync(string projectId, string sourceBranch, string targetBranch, CancellationToken cancellationToken) =>
        GetAllPagesAsync(
            $"projects/{Encode(projectId)}/merge_requests?source_branch={Uri.EscapeDataString(sourceBranch)}&target_branch={Uri.EscapeDataString(targetBranch)}&state=all",
            GitLabJsonContext.Default.GitLabMergeRequestArray,
            cancellationToken);

    public async Task<GitLabMergeRequest> CreateMergeRequestAsync(string projectId, string sourceBranch, string targetBranch, string title, string description, CancellationToken cancellationToken) =>
        await PostAsync(
            $"projects/{Encode(projectId)}/merge_requests",
            new GitLabCreateMergeRequestRequest(sourceBranch, targetBranch, title, description),
            GitLabJsonContext.Default.GitLabCreateMergeRequestRequest,
            GitLabJsonContext.Default.GitLabMergeRequest,
            cancellationToken).ConfigureAwait(false);
    public async Task<GitLabMergeRequest> GetMergeRequestAsync(string projectId, long mergeRequestIid, CancellationToken cancellationToken) =>
        await GetAsync($"projects/{Encode(projectId)}/merge_requests/{mergeRequestIid}", GitLabJsonContext.Default.GitLabMergeRequest, cancellationToken).ConfigureAwait(false)
        ?? throw new ProviderResourceNotFoundException($"GitLab merge request {mergeRequestIid} was not found.");
    public async Task<IReadOnlyList<string>> UpdateMergeRequestLabelsAsync(string projectId, long mergeRequestIid, IReadOnlyCollection<string>? addLabels, IReadOnlyCollection<string>? removeLabel, CancellationToken cancellationToken)
    {
        var body = new GitLabUpdateLabelsRequest(
            addLabels is { Count: > 0 } ? string.Join(',', addLabels) : null,
            removeLabel is { Count: > 0 } ? string.Join(',', removeLabel) : null);
        var mergeRequest = await PutAsync($"projects/{Encode(projectId)}/merge_requests/{mergeRequestIid}", body, GitLabJsonContext.Default.GitLabUpdateLabelsRequest, GitLabJsonContext.Default.GitLabMergeRequest, cancellationToken)
            .ConfigureAwait(false);
        return mergeRequest.Labels;
    }

    public Task<IReadOnlyList<GitLabIssueLink>> GetIssueLinksAsync(string projectId, long issueIid, CancellationToken cancellationToken) =>
        GetAllPagesAsync($"projects/{Encode(projectId)}/issues/{issueIid}/links", GitLabJsonContext.Default.GitLabIssueLinkArray, cancellationToken);

    private static string Encode(string projectId) => Uri.EscapeDataString(projectId);

    private async Task<T?> GetAsync<T>(string relativeUrl, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken)
    {
        using var response = await ProviderRetryPolicy.SendAsync(
            token => httpClient.GetAsync(relativeUrl, token),
            cancellationToken,
            retryPolicy: retryPolicy).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return default;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync(typeInfo, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<T>> GetAllPagesAsync<T>(string relativeUrl, JsonTypeInfo<T[]> typeInfo, CancellationToken cancellationToken)
    {
        var results = new List<T>();
        var visitedUrls = new HashSet<string>(StringComparer.Ordinal);
        string? nextUrl = relativeUrl;
        while (nextUrl is not null)
        {
            if (!visitedUrls.Add(nextUrl))
            {
                throw new InvalidOperationException($"GitLab request '{relativeUrl}' repeated page URL '{nextUrl}'.");
            }

            using var response = await ProviderRetryPolicy.SendAsync(
                token => httpClient.GetAsync(nextUrl, token),
                cancellationToken,
                retryPolicy: retryPolicy).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var page = await response.Content.ReadFromJsonAsync(typeInfo, cancellationToken).ConfigureAwait(false) ?? [];
            results.AddRange(page);
            nextUrl = GetNextPageUrl(response, httpClient.BaseAddress);
        }

        return results;
    }

    private static string? GetNextPageUrl(HttpResponseMessage response, Uri? baseAddress)
    {
        if (baseAddress is null || !response.Headers.TryGetValues("Link", out var linkValues))
        {
            return null;
        }

        foreach (var link in linkValues.SelectMany(value => value.Split(',', StringSplitOptions.TrimEntries)))
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
                throw new InvalidOperationException($"GitLab pagination contained a malformed next-page URI '{target}'.");
            }

            if (Uri.Compare(nextUri, baseAddress, UriComponents.SchemeAndServer, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) != 0)
            {
                throw new InvalidOperationException($"GitLab pagination next-page URI '{nextUri}' has a different authority than '{baseAddress}'.");
            }

            return nextUri.PathAndQuery;
        }

        return null;
    }

    private async Task<TResponse> PostAsync<TRequest, TResponse>(
        string relativeUrl, TRequest body, JsonTypeInfo<TRequest> requestType, JsonTypeInfo<TResponse> responseType, CancellationToken cancellationToken)
    {
        using var response = await ProviderRetryPolicy.SendAsync(
            token => httpClient.PostAsJsonAsync(relativeUrl, body, requestType, token),
            cancellationToken, isIdempotent: false, retryPolicy: retryPolicy).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync(responseType, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"GitLab POST {relativeUrl} returned an empty body.");
    }

    private async Task<TResponse> PutAsync<TRequest, TResponse>(
        string relativeUrl, TRequest body, JsonTypeInfo<TRequest> requestType, JsonTypeInfo<TResponse> responseType, CancellationToken cancellationToken)
    {
        using var response = await ProviderRetryPolicy.SendAsync(
            token => httpClient.PutAsJsonAsync(relativeUrl, body, requestType, token),
            cancellationToken,
            retryPolicy: retryPolicy).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync(responseType, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"GitLab PUT {relativeUrl} returned an empty body.");
    }
}

public sealed record GitLabUser(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("username")] string Username,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("email")] string? Email = null,
    [property: JsonPropertyName("commit_email")] string? CommitEmail = null);

public sealed record GitLabAuthor(
    [property: JsonPropertyName("username")] string Username,
    [property: JsonPropertyName("bot")] bool Bot = false,
    [property: JsonPropertyName("user_type")] string? UserType = null);

public sealed record GitLabIssue(
    [property: JsonPropertyName("iid")] long Iid,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt,
    [property: JsonPropertyName("labels")] IReadOnlyList<string> Labels,
    [property: JsonPropertyName("assignees")] IReadOnlyList<GitLabAuthor> Assignees);

public sealed record GitLabNote(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("body")] string Body,
    [property: JsonPropertyName("author")] GitLabAuthor Author,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt,
    [property: JsonPropertyName("system")] bool System,
    [property: JsonPropertyName("resolvable")] bool Resolvable = false,
    [property: JsonPropertyName("resolved")] bool Resolved = false);

public sealed record GitLabDiscussion(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("individual_note")] bool IndividualNote,
    [property: JsonPropertyName("notes")] IReadOnlyList<GitLabNote> Notes);

public sealed record GitLabCreateNoteRequest([property: JsonPropertyName("body")] string Body);

public sealed record GitLabUpdateLabelsRequest(
    [property: JsonPropertyName("add_labels")] string? AddLabels,
    [property: JsonPropertyName("remove_labels")] string? RemoveLabels);

public sealed record GitLabLabel(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("color")] string Color,
    [property: JsonPropertyName("description")] string? Description);

public sealed record GitLabCreateLabelRequest(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("color")] string Color,
    [property: JsonPropertyName("description")] string? Description);

public sealed record GitLabMergeRequest(
    [property: JsonPropertyName("iid")] long Iid,
    [property: JsonPropertyName("source_branch")] string SourceBranch,
    [property: JsonPropertyName("target_branch")] string TargetBranch,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("draft")] bool Draft,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("labels")] IReadOnlyList<string> Labels,
    [property: JsonPropertyName("web_url")] string? WebUrl = null);

public sealed record GitLabCreateMergeRequestRequest(
    [property: JsonPropertyName("source_branch")] string SourceBranch,
    [property: JsonPropertyName("target_branch")] string TargetBranch,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("description")] string Description);

public sealed record GitLabIssueLink(
    [property: JsonPropertyName("iid")] long Iid,
    [property: JsonPropertyName("project_id")] long ProjectId,
    [property: JsonPropertyName("link_type")] string LinkType,
    [property: JsonPropertyName("web_url")] string? WebUrl = null);

public sealed record GitLabProject(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("default_branch")] string? DefaultBranch);

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(GitLabUser))]
[JsonSerializable(typeof(GitLabIssue))]
[JsonSerializable(typeof(GitLabIssue[]))]
[JsonSerializable(typeof(GitLabNote))]
[JsonSerializable(typeof(GitLabNote[]))]
[JsonSerializable(typeof(GitLabDiscussion))]
[JsonSerializable(typeof(GitLabDiscussion[]))]
[JsonSerializable(typeof(GitLabCreateNoteRequest))]
[JsonSerializable(typeof(GitLabUpdateLabelsRequest))]
[JsonSerializable(typeof(GitLabLabel))]
[JsonSerializable(typeof(GitLabLabel[]))]
[JsonSerializable(typeof(GitLabCreateLabelRequest))]
[JsonSerializable(typeof(GitLabMergeRequest))]
[JsonSerializable(typeof(GitLabMergeRequest[]))]
[JsonSerializable(typeof(GitLabCreateMergeRequestRequest))]
[JsonSerializable(typeof(GitLabIssueLink))]
[JsonSerializable(typeof(GitLabIssueLink[]))]
[JsonSerializable(typeof(GitLabProject))]
internal sealed partial class GitLabJsonContext : JsonSerializerContext;
