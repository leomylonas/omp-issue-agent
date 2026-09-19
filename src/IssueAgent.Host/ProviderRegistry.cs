using IssueAgent.Configuration;
using IssueAgent.Git;
using IssueAgent.Observability;
using IssueAgent.Providers;
using IssueAgent.Providers.GitHub;
using IssueAgent.Providers.GitLab;
using Microsoft.Extensions.Logging;

namespace IssueAgent.Host;

/// <summary>Builds the configured provider set and resolved Git authentication once at startup.
/// Secret values are retained only in this process-lifetime registry and never copied into prompts.</summary>
public sealed class ProviderRegistry
{
    private readonly IReadOnlyDictionary<string, IGitProvider> providers;
    private readonly Dictionary<string, GitAuthentication> gitAuthentication;
    private readonly Dictionary<string, GitAuthentication> gitAuthenticationByHost;

    public ProviderRegistry(EffectiveIssueAgentConfiguration configuration, IssueAgentMetrics metrics, ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        var providerLogger = loggerFactory.CreateLogger<ObservableGitProvider>();
        var built = new Dictionary<string, IGitProvider>(StringComparer.OrdinalIgnoreCase);
        var authentication = new Dictionary<string, GitAuthentication>(StringComparer.OrdinalIgnoreCase);
        var authenticationByHost = new Dictionary<string, GitAuthentication>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in configuration.Providers)
        {
            var created = Create(provider.Source, provider.ApiToken, ResolveProviderTlsTrust(provider.Repositories));
            if (!built.TryAdd(provider.Name, new ObservableGitProvider(created, metrics, providerLogger)))
            {
                throw new InvalidOperationException($"Provider name '{provider.Name}' is configured more than once.");
            }
            foreach (var repository in provider.Repositories)
            {
                var gitAuth = ToGitAuthentication(repository.Git);
                authentication[repository.Id] = gitAuth;
                var host = GitUrlHost.TryGetHost(repository.CloneUrl);
                if (host is not null)
                {
                    authenticationByHost.TryAdd(host, gitAuth);
                }
            }
        }
        providers = built;
        gitAuthentication = authentication;
        gitAuthenticationByHost = authenticationByHost;
    }

    public IReadOnlyCollection<IGitProvider> All => providers.Values.ToArray();

    public IGitProvider Get(string name) => providers.TryGetValue(name, out var provider)
        ? provider
        : throw new KeyNotFoundException($"Configured provider '{name}' was not found.");

    public GitAuthentication GetGitAuthentication(string repositoryId) =>
        gitAuthentication.TryGetValue(repositoryId, out var authentication)
            ? authentication
            : throw new KeyNotFoundException($"Resolved Git authentication for repository '{repositoryId}' was not found.");

    /// <summary>Resolves recursive-submodule credentials by host (specification §11): a submodule
    /// hosted on a configured provider host receives that provider's Git credentials; every other
    /// host resolves to <see langword="null"/> and is updated anonymously.</summary>
    public GitAuthentication? TryGetGitAuthenticationForHost(string host) =>
        gitAuthenticationByHost.TryGetValue(host, out var authentication) ? authentication : null;


    private static IGitProvider Create(ProviderOptions configuration, string? token, TlsTrust tlsTrust)
    {
        var trustedHosts = GetTrustedAttachmentHosts(configuration);
        return configuration.Kind switch
        {
            ProviderKind.GitHub => GitHubProviderFactory.Create(new GitHubProviderConfiguration(
                configuration.Name, configuration.BaseUri, token, trustedHosts, tlsTrust)),
            ProviderKind.GitLab => GitLabProviderFactory.Create(new GitLabProviderConfiguration(
                configuration.Name, configuration.BaseUri, token, trustedHosts, tlsTrust)),
            _ => throw new InvalidOperationException($"Unsupported provider kind '{configuration.Kind}'."),
        };
    }

    private static string[] GetTrustedAttachmentHosts(ProviderOptions configuration)
    {
        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { configuration.BaseUri.Host };
        // GitHub.com serves user attachments from github.com and the CDN-backed
        // githubusercontent.com family, while its API endpoint is api.github.com.
        if (configuration.Kind == ProviderKind.GitHub &&
            (configuration.BaseUri.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase) ||
             configuration.BaseUri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)))
        {
            hosts.Add("github.com");
            hosts.Add("githubusercontent.com");
        }

        return hosts.ToArray();
    }

    private static TlsTrust ResolveProviderTlsTrust(IReadOnlyList<EffectiveRepositoryConfiguration> repositories)
    {
        var trusts = repositories.Select(repository => ToTlsTrust(repository.Git)).ToArray();
        if (trusts.Length == 0)
        {
            return TlsTrust.System;
        }

        if (trusts.Skip(1).Any(trust => !Equivalent(trust, trusts[0])))
        {
            throw new InvalidOperationException("Repositories sharing a provider must use the same TLS trust configuration for provider API calls.");
        }

        return trusts[0];
    }

    private static bool Equivalent(TlsTrust left, TlsTrust right) =>
        left.Mode == right.Mode &&
        left.AdditionalCaCertificatePaths.SequenceEqual(right.AdditionalCaCertificatePaths, StringComparer.Ordinal) &&
        left.Fingerprints.SequenceEqual(right.Fingerprints, StringComparer.OrdinalIgnoreCase);

    private static GitAuthentication ToGitAuthentication(EffectiveGitConfiguration configuration) => new()
    {
        Mode = configuration.Mode switch
        {
            ConfiguredGitAuthenticationMode.ProviderToken => GitAuthenticationMode.ProviderToken,
            ConfiguredGitAuthenticationMode.Token => GitAuthenticationMode.Token,
            ConfiguredGitAuthenticationMode.Ssh => GitAuthenticationMode.Ssh,
            ConfiguredGitAuthenticationMode.Anonymous => GitAuthenticationMode.Anonymous,
            _ => throw new ArgumentOutOfRangeException(nameof(configuration)),
        },
        HttpsUsername = configuration.HttpsUsername,
        HttpsToken = configuration.Token,
        SshPrivateKey = configuration.SshPrivateKey,
        SshPrivateKeyPassphrase = configuration.SshPrivateKeyPassphrase,
        SshUsername = configuration.SshUsername,
        TlsTrust = ToTlsTrust(configuration),
        SshTrust = configuration.SshHostVerificationMode is null
            ? null
            : new SshTrust
            {
                Mode = configuration.SshHostVerificationMode == ConfiguredSshHostVerificationMode.Pinned
                    ? SshHostVerificationMode.Pinned
                    : SshHostVerificationMode.None,
                Fingerprints = configuration.SshFingerprints,
            },
    };

    private static TlsTrust ToTlsTrust(EffectiveGitConfiguration configuration) => new()
    {
        Mode = configuration.TlsMode switch
        {
            ConfiguredTlsTrustMode.System => TlsTrustMode.System,
            ConfiguredTlsTrustMode.SystemPlusAdditionalCa => TlsTrustMode.SystemPlusAdditionalCa,
            ConfiguredTlsTrustMode.Pinned => TlsTrustMode.Pinned,
            ConfiguredTlsTrustMode.None => TlsTrustMode.None,
            _ => throw new ArgumentOutOfRangeException(nameof(configuration)),
        },
        AdditionalCaCertificatePaths = configuration.AdditionalCaCertificatePaths,
        Fingerprints = configuration.TlsFingerprints,
    };
}
