using System.Net.Http.Headers;

namespace IssueAgent.Providers.GitLab;

/// <summary>Builds a fully wired <see cref="GitLabProvider"/> for one configured provider entry.
/// <c>ApiBaseUri</c> must include the <c>api/v4/</c> path (for example <c>https://gitlab.com/api/v4/</c>).</summary>
public static class GitLabProviderFactory
{
    public static GitLabProvider Create(GitLabProviderConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var restHttpClient = new HttpClient { BaseAddress = configuration.ApiBaseUri };
        ConfigureHeaders(restHttpClient, configuration.Token);

        var authenticatedAttachmentClient = new HttpClient();
        ConfigureHeaders(authenticatedAttachmentClient, configuration.Token);
        var anonymousAttachmentClient = new HttpClient();

        return new GitLabProvider(
            new GitLabApiClient(restHttpClient),
            authenticatedAttachmentClient,
            anonymousAttachmentClient,
            configuration.TrustedAttachmentHostSuffixes,
            configuration.Name);
    }

    private static void ConfigureHeaders(HttpClient httpClient, string? token)
    {
        httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("IssueAgent", "1.0"));
        httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (token is not null)
        {
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
    }
}

public sealed record GitLabProviderConfiguration(
    string Name,
    Uri ApiBaseUri,
    string? Token,
    IReadOnlyList<string> TrustedAttachmentHostSuffixes);
