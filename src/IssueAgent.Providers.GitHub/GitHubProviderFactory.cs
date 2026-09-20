using System.Net.Http.Headers;
using IssueAgent.Domain;

using IssueAgent.Git;
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
        RequireHttpsForCredentials(configuration.ApiBaseUri, configuration.Token);

        var isGitHubDotCom = configuration.ApiBaseUri.Host.Equals(GitHubDotComHost, StringComparison.OrdinalIgnoreCase);
        var restRawBaseUri = isGitHubDotCom ? configuration.ApiBaseUri : new Uri(configuration.ApiBaseUri, "api/v3/");
        var graphQlBaseUri = isGitHubDotCom ? new Uri("https://api.github.com/") : new Uri(configuration.ApiBaseUri, "api/");

        var credentials = configuration.Token is null ? Credentials.Anonymous : new Credentials(configuration.Token);
        var connection = new Connection(
            new Octokit.ProductHeaderValue("IssueAgent"),
            restRawBaseUri,
            new Octokit.Internal.InMemoryCredentialStore(credentials),
            new Octokit.Internal.HttpClientAdapter(() => TlsHttpHandlerFactory.Create(configuration.TlsTrust)),
            new Octokit.Internal.SimpleJsonSerializer());
        var restClient = new GitHubClient(connection);

        var graphQlHttpClient = CreateHttpClient(configuration.TlsTrust, graphQlBaseUri);
        var timelineHttpClient = CreateHttpClient(configuration.TlsTrust, restRawBaseUri);
        var mutationHttpClient = CreateHttpClient(configuration.TlsTrust, restRawBaseUri);
        ConfigureGitHubHeaders(graphQlHttpClient, configuration.Token);
        ConfigureGitHubHeaders(timelineHttpClient, configuration.Token);
        ConfigureGitHubHeaders(mutationHttpClient, configuration.Token);

        var authenticatedAttachmentClient = CreateHttpClient(configuration.TlsTrust);
        ConfigureGitHubHeaders(authenticatedAttachmentClient, configuration.Token);
        var anonymousAttachmentClient = new HttpClient(TlsHttpHandlerFactory.CreateForAnonymousAttachmentDownloads(configuration.TlsTrust));

        return new GitHubProvider(
            restClient,
            new GitHubGraphQlClient(graphQlHttpClient, configuration.RetryPolicy),
            new GitHubTimelineClient(timelineHttpClient, configuration.RetryPolicy),
            mutationHttpClient,
            authenticatedAttachmentClient,
            anonymousAttachmentClient,
            configuration.TrustedAttachmentAuthorities,
            isGitHubDotCom,
            configuration.Name,
            configuration.RetryPolicy);
    }

    private static void RequireHttpsForCredentials(Uri apiBaseUri, string? token)
    {
        if (token is not null && !apiBaseUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("A provider BaseUri must use HTTPS when credentials are configured.", nameof(apiBaseUri));
        }
    }

    private static HttpClient CreateHttpClient(TlsTrust tlsTrust, Uri? baseAddress = null) =>
        new(TlsHttpHandlerFactory.Create(tlsTrust)) { BaseAddress = baseAddress };

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
    IReadOnlyList<string> TrustedAttachmentAuthorities,
    TlsTrust? Trust = null,
    RetryPolicy? Retry = null)
{
    public TlsTrust TlsTrust { get; init; } = Trust ?? IssueAgent.Git.TlsTrust.System;

    public RetryPolicy RetryPolicy { get; init; } = Retry ?? RetryPolicy.Default;
}
