using IssueAgent.Configuration;
using IssueAgent.Domain;

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
    private readonly Dictionary<string, string?> gitHostByRepository;
    private readonly Dictionary<string, GitAuthentication> gitAuthenticationByHost;
    public ProviderRegistry(
        EffectiveIssueAgentConfiguration configuration,
        RetryPolicy retryPolicy,
        IssueAgentMetrics metrics,
        ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        var providerLogger = loggerFactory.CreateLogger<ObservableGitProvider>();
        ArgumentNullException.ThrowIfNull(retryPolicy);
        var built = new Dictionary<string, IGitProvider>(StringComparer.OrdinalIgnoreCase);
        var authentication = new Dictionary<string, GitAuthentication>(StringComparer.OrdinalIgnoreCase);
        var gitHosts = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var authenticationByHost = new Dictionary<string, GitAuthentication>(StringComparer.OrdinalIgnoreCase);
        var ambiguousGitAuthenticationHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in configuration.Providers)
        {
            var created = Create(provider.Source, provider.ApiToken, ResolveProviderTlsTrust(provider.Repositories), retryPolicy);
            if (!built.TryAdd(provider.Name, new ObservableGitProvider(created, metrics, providerLogger)))
            {
                throw new InvalidOperationException($"Provider name '{provider.Name}' is configured more than once.");
            }

            foreach (var repository in provider.Repositories)
            {
                var gitAuth = ToGitAuthentication(repository.Git);
                authentication[repository.Id] = gitAuth;
                var host = GitUrlHost.TryGetHost(repository.CloneUrl);
                gitHosts[repository.Id] = host;
                if (host is null || ambiguousGitAuthenticationHosts.Contains(host))
                {
                    continue;
                }

                if (authenticationByHost.TryGetValue(host, out var configuredAuthentication))
                {
                    if (!Equivalent(configuredAuthentication, gitAuth))
                    {
                        authenticationByHost.Remove(host);
                        ambiguousGitAuthenticationHosts.Add(host);
                    }
                }
                else
                {
                    authenticationByHost.Add(host, gitAuth);
                }
            }
        }

        providers = built;
        gitAuthentication = authentication;
        gitHostByRepository = gitHosts;
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

    /// <summary>Resolves recursive-submodule credentials by host (specification §11). The current
    /// repository's authentication is used only for its own configured host. A different configured
    /// host is usable when every configured mapping agrees; differing mappings fail closed.</summary>
    public GitAuthentication? GetSubmoduleGitAuthentication(string repositoryId, string host)
    {
        var authentication = GetGitAuthentication(repositoryId);
        if (gitHostByRepository.TryGetValue(repositoryId, out var repositoryHost) &&
            string.Equals(repositoryHost, host, StringComparison.OrdinalIgnoreCase))
        {
            return authentication;
        }

        return gitAuthenticationByHost.TryGetValue(host, out authentication) ? authentication : null;
    }


    private static IGitProvider Create(ProviderOptions configuration, string? token, TlsTrust tlsTrust, RetryPolicy retryPolicy)
    {
        var trustedAuthorities = GetTrustedAttachmentAuthorities(configuration);
        return configuration.Kind switch
        {
            ProviderKind.GitHub => GitHubProviderFactory.Create(new GitHubProviderConfiguration(
                configuration.Name, configuration.BaseUri, token, trustedAuthorities, tlsTrust, retryPolicy)),
            ProviderKind.GitLab => GitLabProviderFactory.Create(new GitLabProviderConfiguration(
                configuration.Name, configuration.BaseUri, token, trustedAuthorities, tlsTrust, retryPolicy)),
            _ => throw new InvalidOperationException($"Unsupported provider kind '{configuration.Kind}'."),
        };
    }

    private static string[] GetTrustedAttachmentAuthorities(ProviderOptions configuration)
    {
        var authorities = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { configuration.BaseUri.Authority };
        // GitHub.com serves some attachments from its web authority in addition to its API
        // authority. githubusercontent.com descendants are recognized by GitHubProvider as the
        // public GitHub-owned attachment family, not as configurable enterprise authorities.
        if (configuration.Kind == ProviderKind.GitHub &&
            configuration.BaseUri.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase))
        {
            authorities.Add("github.com");
        }

        return authorities.ToArray();
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

    private static bool Equivalent(GitAuthentication left, GitAuthentication right) =>
        left.Mode == right.Mode &&
        left.HttpsUsername == right.HttpsUsername &&
        left.HttpsToken == right.HttpsToken &&
        left.SshPrivateKey == right.SshPrivateKey &&
        left.SshPrivateKeyPassphrase == right.SshPrivateKeyPassphrase &&
        left.SshUsername == right.SshUsername &&
        Equivalent(left.TlsTrust, right.TlsTrust) &&
        Equivalent(left.SshTrust, right.SshTrust);

    private static bool Equivalent(SshTrust? left, SshTrust? right) =>
        left is null
            ? right is null
            : right is not null &&
              left.Mode == right.Mode &&
              EquivalentSet(left.Fingerprints, right.Fingerprints, StringComparer.OrdinalIgnoreCase);

    private static bool Equivalent(TlsTrust left, TlsTrust right) =>
        left.Mode == right.Mode &&
        EquivalentSet(left.AdditionalCaCertificatePaths, right.AdditionalCaCertificatePaths, StringComparer.Ordinal) &&
        EquivalentSet(left.Fingerprints, right.Fingerprints, StringComparer.OrdinalIgnoreCase);

    private static bool EquivalentSet(IReadOnlyList<string> left, IReadOnlyList<string> right, IEqualityComparer<string> comparer) =>
        left.ToHashSet(comparer).SetEquals(right);

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
