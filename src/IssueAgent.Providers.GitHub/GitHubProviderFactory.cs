using System.Net.Http.Headers;
using Octokit;

namespace IssueAgent.Providers.GitHub;

/// <summary>
/// Builds a fully wired <see cref="GitHubProvider"/> for one configured provider entry.
///
/// GitHub.com and GitHub Enterprise Server route REST and GraphQL differently. For github.com,
/// <c>ApiBaseUri</c> is used as-is for REST and GraphQL lives at <c>https://api.github.com/graphql</c>.
/// For an Enterprise Server host, Octokit automatically prefixes REST calls with <c>api/v3/</c>;
/// this factory mirrors that convention for the raw timeline client and routes GraphQL to
/// <c>api/graphql</c>, matching GitHub's documented Enterprise Server routing.
/// </summary>
public static class GitHubProviderFactory
{
    private const string GitHubDotComHost = "api.github.com";

    public static GitHubProvider Create(GitHubProviderConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var isGitHubDotCom = configuration.ApiBaseUri.Host.Equals(GitHubDotComHost, StringComparison.OrdinalIgnoreCase);
        var restRawBaseUri = isGitHubDotCom ? configuration.ApiBaseUri : new Uri(configuration.ApiBaseUri, "api/v3/");
        var graphQlBaseUri = isGitHubDotCom ? new Uri("https://api.github.com/") : new Uri(configuration.ApiBaseUri, "api/");

        var credentials = configuration.Token is null ? Credentials.Anonymous : new Credentials(configuration.Token);
        var restClient = new GitHubClient(new Octokit.ProductHeaderValue("IssueAgent"), configuration.ApiBaseUri)
        {
            Credentials = credentials,
        };

        var graphQlHttpClient = new HttpClient { BaseAddress = graphQlBaseUri };
        var timelineHttpClient = new HttpClient { BaseAddress = restRawBaseUri };
        ConfigureGitHubHeaders(graphQlHttpClient, configuration.Token);
        ConfigureGitHubHeaders(timelineHttpClient, configuration.Token);

        var authenticatedAttachmentClient = new HttpClient();
        ConfigureGitHubHeaders(authenticatedAttachmentClient, configuration.Token);
        var anonymousAttachmentClient = new HttpClient();

        return new GitHubProvider(
            restClient,
            new GitHubGraphQlClient(graphQlHttpClient),
            new GitHubTimelineClient(timelineHttpClient),
            authenticatedAttachmentClient,
            anonymousAttachmentClient,
            configuration.TrustedAttachmentHostSuffixes,
            configuration.Name);
    }

    private static void ConfigureGitHubHeaders(HttpClient httpClient, string? token)
    {
        httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("IssueAgent", "1.0"));
        httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        if (token is not null)
        {
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
    }
}

public sealed record GitHubProviderConfiguration(
    string Name,
    Uri ApiBaseUri,
    string? Token,
    IReadOnlyList<string> TrustedAttachmentHostSuffixes);
