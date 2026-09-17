using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace IssueAgent.Providers.GitHub;

/// <summary>Minimal GraphQL client for the stable GitHub review-thread resolution field, which has
/// no REST equivalent. All other GitHub access in this provider uses the REST API via Octokit.</summary>
public sealed partial class GitHubGraphQlClient(HttpClient httpClient)
{
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

    public async IAsyncEnumerable<ProviderReviewThread> GetReviewThreadsAsync(
        string owner,
        string name,
        int number,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? cursor = null;
        do
        {
            var payload = new GraphQlRequest(ReviewThreadsQuery, new GraphQlVariables(owner, name, number, cursor));
            using var response = await httpClient.PostAsJsonAsync("graphql", payload, GraphQlJsonContext.Default.GraphQlRequest, cancellationToken)
                .ConfigureAwait(false);
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
                    node.Comments.Nodes
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

            cursor = connection.PageInfo.HasNextPage ? connection.PageInfo.EndCursor : null;
        }
        while (cursor is not null);
    }

    private static bool IsBotLogin(string? login) => login is not null && login.EndsWith("[bot]", StringComparison.Ordinal);

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

    private sealed record GraphQlCommentConnection(IReadOnlyList<GraphQlComment> Nodes);

    private sealed record GraphQlComment(long? DatabaseId, string Body, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, GraphQlAuthor? Author);

    private sealed record GraphQlAuthor(string Login);

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true)]
    [JsonSerializable(typeof(GraphQlRequest))]
    [JsonSerializable(typeof(GraphQlResponse))]
    private sealed partial class GraphQlJsonContext : JsonSerializerContext;
}
