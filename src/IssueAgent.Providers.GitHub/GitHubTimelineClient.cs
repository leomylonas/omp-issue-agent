using System.Net.Http.Json;
using System.Text.Json.Serialization;

using IssueAgent.Domain;

namespace IssueAgent.Providers.GitHub;

/// <summary>
/// Reads the stable, generally-available Issue Timeline REST API to discover "cross-referenced"
/// events, the only issue-relationship signal GitHub exposes without GraphQL sub-issue/project
/// preview fields. Parent/child, blocks/blocked-by, and duplicate relationships require those
/// additional preview APIs and are out of scope for this slice.
/// </summary>
public sealed partial class GitHubTimelineClient(HttpClient httpClient, RetryPolicy? configuredRetryPolicy = null)
{
    private readonly RetryPolicy retryPolicy = configuredRetryPolicy ?? RetryPolicy.Default;
    /// <summary>Hard ceiling on timeline pages walked per issue (specification §27's bounded-
    /// resource intent). An issue with more history than this is vanishingly unlikely; exceeding it
    /// throws rather than silently truncating the relationship set.</summary>
    private const int MaxPages = 200;

    public async IAsyncEnumerable<CrossReference> GetCrossReferencedIssuesAsync(
        string owner,
        string name,
        long issueNumber,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var page = 1;
        while (true)
        {
            if (page > MaxPages)
            {
                throw new InvalidOperationException(
                    $"GitHub issue timeline for {owner}/{name}#{issueNumber} exceeded the {MaxPages}-page pagination limit.");
            }

            using var response = await ProviderRetryPolicy.SendAsync(
                token => httpClient.GetAsync($"repos/{owner}/{name}/issues/{issueNumber}/timeline?per_page=100&page={page}", token),
                cancellationToken,
                retryPolicy: retryPolicy).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var events = await response.Content
                .ReadFromJsonAsync(TimelineJsonContext.Default.TimelineEventArray, cancellationToken)
                .ConfigureAwait(false)
                ?? [];

            if (events.Length == 0)
            {
                yield break;
            }

            foreach (var timelineEvent in events)
            {
                if (timelineEvent is { Event: "cross-referenced", Source.Issue: { } referenced } &&
                    referenced.Repository is { } repository)
                {
                    yield return new CrossReference(
                        repository.Owner.Login,
                        repository.Name,
                        referenced.Number);
                }
            }

            page++;
        }
    }

    public sealed record CrossReference(string Owner, string Name, long Number);

    private sealed record TimelineEvent(
        [property: JsonPropertyName("event")] string Event,
        [property: JsonPropertyName("source")] TimelineSource? Source);

    private sealed record TimelineSource([property: JsonPropertyName("issue")] TimelineIssue? Issue);

    private sealed record TimelineIssue(
        [property: JsonPropertyName("number")] long Number,
        [property: JsonPropertyName("repository")] TimelineRepository? Repository);

    private sealed record TimelineRepository(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("owner")] TimelineOwner Owner);

    private sealed record TimelineOwner([property: JsonPropertyName("login")] string Login);

    [JsonSerializable(typeof(TimelineEvent[]))]
    private sealed partial class TimelineJsonContext : JsonSerializerContext;
}
