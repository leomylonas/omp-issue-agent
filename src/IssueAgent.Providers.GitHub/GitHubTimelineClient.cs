using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace IssueAgent.Providers.GitHub;

/// <summary>
/// Reads the stable, generally-available Issue Timeline REST API to discover "cross-referenced"
/// events, the only issue-relationship signal GitHub exposes without GraphQL sub-issue/project
/// preview fields. Parent/child, blocks/blocked-by, and duplicate relationships require those
/// additional preview APIs and are out of scope for this slice; see AGENTS.md follow-up tracking.
/// </summary>
public sealed partial class GitHubTimelineClient(HttpClient httpClient)
{
    public async IAsyncEnumerable<CrossReference> GetCrossReferencedIssuesAsync(
        string owner,
        string name,
        long issueNumber,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var page = 1;
        while (true)
        {
            using var response = await httpClient
                .GetAsync($"repos/{owner}/{name}/issues/{issueNumber}/timeline?per_page=100&page={page}", cancellationToken)
                .ConfigureAwait(false);
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
