using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace IssueAgent.Providers.GitHub;

/// <summary>Minimal GraphQL client for the stable GitHub review-thread resolution field, which has
/// no REST equivalent. All other GitHub access in this provider uses the REST API via Octokit.</summary>
public sealed partial class GitHubGraphQlClient(HttpClient httpClient)
{
    /// <summary>Hard ceiling on review-thread pages walked per pull request (specification §27's
    /// bounded-resource intent); exceeding it throws rather than silently truncating.</summary>
    private const int MaxPages = 200;

    private const string ReviewThreadsQuery = """
        query($owner: String!, $name: String!, $number: Int!, $after: String) {
          repository(owner: $owner, name: $name) {
            pullRequest(number: $number) {
              reviewThreads(first: 50, after: $after) {
                pageInfo { hasNextPage endCursor }
                nodes {
                  id
                  isResolved
                  comments(first: 100) {
                    pageInfo { hasNextPage endCursor }
                    nodes {
                      databaseId
                      body
                      createdAt
                      updatedAt
                      author { login }
                    }
                  }
                }
              }
            }
          }
        }
        """;

    private const string ThreadCommentsQuery = """
        query($threadId: ID!, $after: String) {
          node(id: $threadId) {
            ... on PullRequestReviewThread {
              comments(first: 100, after: $after) {
                pageInfo { hasNextPage endCursor }
                nodes {
                  databaseId
                  body
                  createdAt
                  updatedAt
                  author { login }
                }
              }
            }
          }
        }
        """;

    public async IAsyncEnumerable<ProviderReviewThread> GetReviewThreadsAsync(
        string owner,
        string name,
        int number,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? cursor = null;
        var page = 0;
        do
        {
            if (++page > MaxPages)
            {
                throw new InvalidOperationException(
                    $"GitHub GraphQL review threads for {owner}/{name}#{number} exceeded the {MaxPages}-page pagination limit.");
            }

            var payload = new GraphQlRequest(ReviewThreadsQuery, new GraphQlVariables(owner, name, number, cursor));
            using var response = await ProviderRetryPolicy.SendAsync(
                token => httpClient.PostAsJsonAsync("graphql", payload, GraphQlJsonContext.Default.GraphQlRequest, token),
                cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var result = await response.Content
                .ReadFromJsonAsync(GraphQlJsonContext.Default.GraphQlResponse, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException("GitHub GraphQL review-threads response was empty.");

            if (result.Errors is { Count: > 0 })
            {
                throw new InvalidOperationException($"GitHub GraphQL review-threads query failed: {string.Join("; ", result.Errors.Select(e => e.Message))}");
            }

            var connection = result.Data?.Repository?.PullRequest?.ReviewThreads
                ?? throw new InvalidOperationException("GitHub GraphQL response did not contain a pull request review-thread connection.");

            foreach (var node in connection.Nodes)
            {
                yield return new ProviderReviewThread(
                    node.Id,
                    node.IsResolved,
                    (await GetAllThreadCommentsAsync(node, cancellationToken).ConfigureAwait(false))
                        .Select(c => new ProviderComment(
                            c.DatabaseId ?? 0,
                            c.Author?.Login ?? "ghost",
                            c.Body,
                            c.CreatedAt,
                            c.UpdatedAt,
                            new AttachmentSource("pull-request-review-comment", node.Id, node.Id),
                            IsBotLogin(c.Author?.Login)))
                        .ToList());
            }

            cursor = GetNextCursor(connection.PageInfo, "review-thread");
        }
        while (cursor is not null);
    }

    private static string? GetNextCursor(GraphQlPageInfo pageInfo, string connection) =>
        !pageInfo.HasNextPage
            ? null
            : pageInfo.EndCursor ?? throw new InvalidOperationException(
                $"GitHub GraphQL {connection} connection indicated a next page without an end cursor.");

    private static bool IsBotLogin(string? login) => login is not null && login.EndsWith("[bot]", StringComparison.Ordinal);

    private async Task<IReadOnlyList<GraphQlComment>> GetAllThreadCommentsAsync(
        GraphQlReviewThread thread,
        CancellationToken cancellationToken)
    {
        var comments = new List<GraphQlComment>(thread.Comments.Nodes);
        var cursor = GetNextCursor(thread.Comments.PageInfo, $"review-thread comment for '{thread.Id}'");
        var page = 0;
        while (cursor is not null)
        {
            if (++page > MaxPages)
            {
                throw new InvalidOperationException(
                    $"GitHub GraphQL review-thread comments for '{thread.Id}' exceeded the {MaxPages}-page pagination limit.");
            }

            var payload = new GraphQlThreadCommentsRequest(
                ThreadCommentsQuery,
                new GraphQlThreadCommentsVariables(thread.Id, cursor));
            using var response = await ProviderRetryPolicy.SendAsync(
                token => httpClient.PostAsJsonAsync("graphql", payload, GraphQlJsonContext.Default.GraphQlThreadCommentsRequest, token),
                cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var result = await response.Content
                .ReadFromJsonAsync(GraphQlJsonContext.Default.GraphQlThreadCommentsResponse, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException("GitHub GraphQL review-thread comments response was empty.");
            if (result.Errors is { Count: > 0 })
            {
                throw new InvalidOperationException($"GitHub GraphQL review-thread comments query failed: {string.Join("; ", result.Errors.Select(e => e.Message))}");
            }

            var connection = result.Data?.Node?.Comments
                ?? throw new InvalidOperationException("GitHub GraphQL response did not contain a review-thread comment connection.");
            comments.AddRange(connection.Nodes);
            cursor = GetNextCursor(connection.PageInfo, $"review-thread comment for '{thread.Id}'");
        }

        return comments;
    }

    private sealed record GraphQlRequest(string Query, GraphQlVariables Variables);

    private sealed record GraphQlVariables(string Owner, string Name, int Number, string? After);

    private sealed record GraphQlResponse(GraphQlData? Data, IReadOnlyList<GraphQlError>? Errors);

    private sealed record GraphQlError(string Message);

    private sealed record GraphQlData(GraphQlRepository? Repository);

    private sealed record GraphQlRepository(GraphQlPullRequest? PullRequest);

    private sealed record GraphQlPullRequest(GraphQlReviewThreadConnection ReviewThreads);

    private sealed record GraphQlReviewThreadConnection(GraphQlPageInfo PageInfo, IReadOnlyList<GraphQlReviewThread> Nodes);

    private sealed record GraphQlPageInfo(bool HasNextPage, string? EndCursor);

    private sealed record GraphQlReviewThread(string Id, bool IsResolved, GraphQlCommentConnection Comments);

    private sealed record GraphQlCommentConnection(GraphQlPageInfo PageInfo, IReadOnlyList<GraphQlComment> Nodes);

    private sealed record GraphQlComment(long? DatabaseId, string Body, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, GraphQlAuthor? Author);

    private sealed record GraphQlAuthor(string Login);

    private sealed record GraphQlThreadCommentsRequest(string Query, GraphQlThreadCommentsVariables Variables);

    private sealed record GraphQlThreadCommentsVariables(string ThreadId, string? After);

    private sealed record GraphQlThreadCommentsResponse(GraphQlThreadCommentsData? Data, IReadOnlyList<GraphQlError>? Errors);

    private sealed record GraphQlThreadCommentsData(GraphQlThreadCommentNode? Node);

    private sealed record GraphQlThreadCommentNode(GraphQlCommentConnection? Comments);

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true)]
    [JsonSerializable(typeof(GraphQlRequest))]
    [JsonSerializable(typeof(GraphQlResponse))]
    [JsonSerializable(typeof(GraphQlThreadCommentsRequest))]
    [JsonSerializable(typeof(GraphQlThreadCommentsResponse))]
    private sealed partial class GraphQlJsonContext : JsonSerializerContext;
}
