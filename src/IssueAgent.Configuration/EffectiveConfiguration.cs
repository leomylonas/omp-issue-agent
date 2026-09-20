namespace IssueAgent.Configuration;

public enum ConfiguredWorkflowMode
{
    Full,
    PlanOnly,
}

public enum ConfiguredGitAuthenticationMode
{
    ProviderToken,
    Token,
    Ssh,
    Anonymous,
}

public enum ConfiguredTlsTrustMode
{
    System,
    SystemPlusAdditionalCa,
    Pinned,
    None,
}

public enum ConfiguredSshHostVerificationMode
{
    Pinned,
    None,
}

/// <summary>Values that inherit global → provider → repository. Null means inherit.</summary>
public sealed record RepositorySettingsOptions
{
    public bool? IgnoreBotComments { get; init; }
    public int? RelatedIssueTraversalDepth { get; init; }
    /// <summary>Whether provider-native closing syntax is added to newly-created PR/MR bodies.</summary>
    public bool? CloseIssueOnMerge { get; init; }
    public long? MaxAttachmentSizeBytes { get; init; }
    public long? MaxTotalAttachmentSizeBytes { get; init; }
    public ConfiguredWorkflowMode? WorkflowMode { get; init; }
    public IReadOnlyList<string> SupplementalInstructions { get; init; } = [];
    public GitTransportOptions? Git { get; init; }

    /// <summary>Commit author/committer identity for OMP-authored commits (specification §10:
    /// defaults to the authenticated provider login when an identity override is configured,
    /// with a stable local fallback for anonymous providers).</summary>
    public GitIdentityOptions? GitIdentity { get; init; }
    public TimeSpan? OmpTimeout { get; init; }
    public IReadOnlyDictionary<string, string> OmpRoles { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);
}

public sealed record GitIdentityOptions
{
    public string? Name { get; init; }
    public string? Email { get; init; }
}

public sealed record GitTransportOptions
{
    public ConfiguredGitAuthenticationMode? Mode { get; init; }
    public string? HttpsUsername { get; init; }
    public SecretSource? Token { get; init; }
    public SecretSource? SshPrivateKey { get; init; }
    public SecretSource? SshPrivateKeyPassphrase { get; init; }
    public string? SshUsername { get; init; }
    public TlsTrustOptions? Tls { get; init; }
    public SshTrustOptions? SshTrust { get; init; }
}

public sealed record TlsTrustOptions
{
    public ConfiguredTlsTrustMode? Mode { get; init; }
    public IReadOnlyList<string> AdditionalCaCertificatePaths { get; init; } = [];
    public IReadOnlyList<string> Fingerprints { get; init; } = [];
}

public sealed record SshTrustOptions
{
    public ConfiguredSshHostVerificationMode? Mode { get; init; }
    public IReadOnlyList<string> Fingerprints { get; init; } = [];
}

public sealed record EffectiveIssueAgentConfiguration(
    IssueAgentOptions Source,
    IReadOnlyList<EffectiveProviderConfiguration> Providers,
    EffectiveOmpConfiguration Omp,
    EffectiveNotificationsConfiguration Notifications)
{
    public EffectiveProviderConfiguration GetProvider(string name) =>
        Providers.Single(provider => provider.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
}

public sealed record EffectiveProviderConfiguration(
    ProviderOptions Source,
    string Name,
    string? ApiToken,
    IReadOnlyList<EffectiveRepositoryConfiguration> Repositories);

public sealed record EffectiveRepositoryConfiguration(
    RepositoryOptions Source,
    string ProviderName,
    ProviderKind ProviderKind,
    string Id,
    string OwnerOrNamespace,
    string Name,
    string CloneUrl,
    string? TargetBranch,
    bool Enabled,
    DateTimeOffset StartDate,
    bool IgnoreBotComments,
    int RelatedIssueTraversalDepth,
    long MaxAttachmentSizeBytes,
    long MaxTotalAttachmentSizeBytes,
    ConfiguredWorkflowMode WorkflowMode,
    IReadOnlyList<string> SupplementalInstructions,
    EffectiveGitConfiguration Git,
    string GitIdentityName,
    string GitIdentityEmail,
    TimeSpan? OmpTimeout,
    IReadOnlyDictionary<string, string> OmpRoles)
{
    /// <summary>True when the Git author name should come from the authenticated provider user.</summary>
    public bool UsesProviderIdentityForName { get; init; }

    /// <summary>True when the Git author email should come from the authenticated provider user.</summary>
    public bool UsesProviderIdentityForEmail { get; init; }
    public bool CloseIssueOnMerge { get; init; } = true;
}

public sealed record EffectiveGitConfiguration(
    ConfiguredGitAuthenticationMode Mode,
    string? HttpsUsername,
    string? Token,
    string? SshPrivateKey,
    string? SshPrivateKeyPassphrase,
    string? SshUsername,
    ConfiguredTlsTrustMode TlsMode,
    IReadOnlyList<string> AdditionalCaCertificatePaths,
    IReadOnlyList<string> TlsFingerprints,
    ConfiguredSshHostVerificationMode? SshHostVerificationMode,
    IReadOnlyList<string> SshFingerprints);

public sealed record EffectiveOmpConfiguration(
    string ExecutablePath,
    TimeSpan? Timeout,
    string? AuthBrokerUrl,
    IReadOnlyDictionary<string, string> ConnectionSettings,
    IReadOnlyDictionary<string, string> ExecutionSecrets,
    IReadOnlyDictionary<string, string> Roles);

/// <summary>TLS trust policy for outbound notification sink requests (specification §11).</summary>
public sealed record EffectiveNotificationsConfiguration(
    ConfiguredTlsTrustMode TlsMode,
    IReadOnlyList<string> AdditionalCaCertificatePaths,
    IReadOnlyList<string> TlsFingerprints);

/// <summary>Resolves the immutable process configuration once, including secrets and all inheritance.</summary>
public static class EffectiveConfigurationResolver
{
    public static EffectiveIssueAgentConfiguration Resolve(
        IssueAgentOptions options,
        Func<string, string?> environmentReader,
        Func<string, string> fileReader)
    {
        ArgumentNullException.ThrowIfNull(options);
        var providers = options.Providers.Select(provider => ResolveProvider(options, provider, environmentReader, fileReader)).ToArray();
        var executionSecrets = options.Omp.ExecutionSecrets.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.Resolve(environmentReader, fileReader),
            StringComparer.Ordinal);
        var omp = new EffectiveOmpConfiguration(
            options.Omp.ExecutablePath,
            options.Omp.Timeout,
            options.Omp.AuthBrokerUrl,
            new Dictionary<string, string>(options.Omp.ConnectionSettings, StringComparer.Ordinal),
            executionSecrets,
            new Dictionary<string, string>(options.Omp.Roles, StringComparer.Ordinal));
        var notifications = ResolveNotifications(options.Notifications.Tls);
        return new EffectiveIssueAgentConfiguration(options, providers, omp, notifications);
    }

    private static EffectiveNotificationsConfiguration ResolveNotifications(TlsTrustOptions? tls) => new(
        tls?.Mode ?? ConfiguredTlsTrustMode.System,
        tls?.AdditionalCaCertificatePaths ?? [],
        tls?.Fingerprints ?? []);

    private static EffectiveProviderConfiguration ResolveProvider(
        IssueAgentOptions root,
        ProviderOptions provider,
        Func<string, string?> environmentReader,
        Func<string, string> fileReader)
    {
        var apiToken = provider.Token?.Resolve(environmentReader, fileReader);
        var repositories = provider.Repositories
            .Select(repository => ResolveRepository(root, provider, repository, apiToken, environmentReader, fileReader))
            .ToArray();
        return new EffectiveProviderConfiguration(provider, provider.Name, apiToken, repositories);
    }

    private static EffectiveRepositoryConfiguration ResolveRepository(
        IssueAgentOptions root,
        ProviderOptions provider,
        RepositoryOptions repository,
        string? providerToken,
        Func<string, string?> environmentReader,
        Func<string, string> fileReader)
    {
        var nameParts = repository.Name.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var repositoryName = nameParts.Length == 0 ? repository.Name : nameParts[^1];
        var owner = repository.OwnerOrNamespace
            ?? (nameParts.Length > 1 ? string.Join('/', nameParts[..^1]) : null)
            ?? provider.DefaultOwnerOrNamespace
            ?? throw new InvalidOperationException($"Repository '{repository.Id}' requires an owner/namespace.");
        var settings = Merge(root.Defaults, provider.Defaults, repository.Settings);
        var git = ResolveGit(settings.Git, providerToken, environmentReader, fileReader);
        var cloneUrl = repository.CloneUrl ?? DeriveCloneUrl(provider.Kind, provider.BaseUri, owner, repositoryName, git);
        var targetBranch = repository.TargetBranch;
        var startDate = repository.StartDate ?? root.StartDate;
        var roles = new Dictionary<string, string>(root.Omp.Roles, StringComparer.Ordinal);
        foreach (var pair in settings.OmpRoles) roles[pair.Key] = pair.Value;
        return new EffectiveRepositoryConfiguration(
            repository,
            provider.Name,
            provider.Kind,
            repository.Id,
            owner,
            repositoryName,
            cloneUrl,
            targetBranch,
            repository.Enabled,
            startDate,
            settings.IgnoreBotComments ?? true,
            settings.RelatedIssueTraversalDepth ?? 1,
            settings.MaxAttachmentSizeBytes ?? 25L * 1024 * 1024,
            settings.MaxTotalAttachmentSizeBytes ?? 100L * 1024 * 1024,
            settings.WorkflowMode ?? ConfiguredWorkflowMode.Full,
            settings.SupplementalInstructions,
            git,
            settings.GitIdentity?.Name ?? provider.IdentityOverride ?? "IssueAgent",
            settings.GitIdentity?.Email ?? "issue-agent@localhost",
            settings.OmpTimeout ?? root.Omp.Timeout,
            roles)
        {
            UsesProviderIdentityForName = settings.GitIdentity?.Name is null && string.IsNullOrWhiteSpace(provider.IdentityOverride),
            // An anonymous provider with an explicit identity override cannot resolve provider
            // metadata (notably email) without making an authenticated /user request.
            UsesProviderIdentityForEmail = settings.GitIdentity?.Email is null && providerToken is not null,
            CloseIssueOnMerge = settings.CloseIssueOnMerge ?? true,
        };
    }
    internal static RepositorySettingsOptions Merge(params RepositorySettingsOptions[] levels)
    {
        bool? ignoreBots = null;
        bool? closeIssueOnMerge = null;
        long? maxFile = null;
        int? relatedDepth = null;
        long? maxTotal = null;
        ConfiguredWorkflowMode? workflowMode = null;
        GitTransportOptions? git = null;
        GitIdentityOptions? gitIdentity = null;
        TimeSpan? ompTimeout = null;
        var instructions = new List<string>();
        var roles = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var level in levels)
        {
            ignoreBots = level.IgnoreBotComments ?? ignoreBots;
            closeIssueOnMerge = level.CloseIssueOnMerge ?? closeIssueOnMerge;
            relatedDepth = level.RelatedIssueTraversalDepth ?? relatedDepth;
            maxFile = level.MaxAttachmentSizeBytes ?? maxFile;
            maxTotal = level.MaxTotalAttachmentSizeBytes ?? maxTotal;
            workflowMode = level.WorkflowMode ?? workflowMode;
            git = MergeGit(git, level.Git);
            gitIdentity = MergeGitIdentity(gitIdentity, level.GitIdentity);
            ompTimeout = level.OmpTimeout ?? ompTimeout;
            instructions.AddRange(level.SupplementalInstructions.Where(value => !string.IsNullOrWhiteSpace(value)));
            foreach (var pair in level.OmpRoles) roles[pair.Key] = pair.Value;
        }
        return new RepositorySettingsOptions
        {
            IgnoreBotComments = ignoreBots,
            CloseIssueOnMerge = closeIssueOnMerge,
            RelatedIssueTraversalDepth = relatedDepth,
            MaxAttachmentSizeBytes = maxFile,
            MaxTotalAttachmentSizeBytes = maxTotal,
            WorkflowMode = workflowMode,
            SupplementalInstructions = instructions,
            Git = git,
            GitIdentity = gitIdentity,
            OmpTimeout = ompTimeout,
            OmpRoles = roles,
        };
    }

    private static GitIdentityOptions? MergeGitIdentity(GitIdentityOptions? inherited, GitIdentityOptions? current)
    {
        if (current is null) return inherited;
        return new GitIdentityOptions
        {
            Name = current.Name ?? inherited?.Name,
            Email = current.Email ?? inherited?.Email,
        };
    }

    private static GitTransportOptions? MergeGit(GitTransportOptions? inherited, GitTransportOptions? current)
    {
        if (current is null) return inherited;
        inherited ??= new GitTransportOptions();
        return new GitTransportOptions
        {
            Mode = current.Mode ?? inherited.Mode,
            HttpsUsername = current.HttpsUsername ?? inherited.HttpsUsername,
            Token = current.Token ?? inherited.Token,
            SshPrivateKey = current.SshPrivateKey ?? inherited.SshPrivateKey,
            SshPrivateKeyPassphrase = current.SshPrivateKeyPassphrase ?? inherited.SshPrivateKeyPassphrase,
            SshUsername = current.SshUsername ?? inherited.SshUsername,
            Tls = MergeTls(inherited.Tls, current.Tls),
            SshTrust = MergeSshTrust(inherited.SshTrust, current.SshTrust),
        };
    }

    private static TlsTrustOptions? MergeTls(TlsTrustOptions? inherited, TlsTrustOptions? current)
    {
        if (current is null) return inherited;
        if (inherited is null) return current;
        return new TlsTrustOptions
        {
            Mode = current.Mode ?? inherited.Mode,
            AdditionalCaCertificatePaths = current.AdditionalCaCertificatePaths.Count > 0
                ? current.AdditionalCaCertificatePaths
                : inherited.AdditionalCaCertificatePaths,
            Fingerprints = current.Fingerprints.Count > 0
                ? current.Fingerprints
                : inherited.Fingerprints,
        };
    }

    private static SshTrustOptions? MergeSshTrust(SshTrustOptions? inherited, SshTrustOptions? current)
    {
        if (current is null) return inherited;
        if (inherited is null) return current;
        return new SshTrustOptions
        {
            Mode = current.Mode ?? inherited.Mode,
            Fingerprints = current.Fingerprints.Count > 0
                ? current.Fingerprints
                : inherited.Fingerprints,
        };
    }

    private static EffectiveGitConfiguration ResolveGit(
        GitTransportOptions? git,
        string? providerToken,
        Func<string, string?> environmentReader,
        Func<string, string> fileReader)
    {
        var mode = git?.Mode ?? (providerToken is null ? ConfiguredGitAuthenticationMode.Anonymous : ConfiguredGitAuthenticationMode.ProviderToken);
        var token = mode == ConfiguredGitAuthenticationMode.ProviderToken
            ? providerToken
            : git?.Token?.Resolve(environmentReader, fileReader);
        var tls = git?.Tls;
        return new EffectiveGitConfiguration(
            mode,
            git?.HttpsUsername ?? "x-access-token",
            token,
            git?.SshPrivateKey?.Resolve(environmentReader, fileReader),
            git?.SshPrivateKeyPassphrase?.Resolve(environmentReader, fileReader),
            git?.SshUsername,
            tls?.Mode ?? ConfiguredTlsTrustMode.System,
            tls?.AdditionalCaCertificatePaths ?? [],
            tls?.Fingerprints ?? [],
            git?.SshTrust?.Mode,
            git?.SshTrust?.Fingerprints ?? []);
    }

    private static string DeriveCloneUrl(
        ProviderKind kind,
        Uri baseUri,
        string owner,
        string repository,
        EffectiveGitConfiguration git)
    {
        var builder = new UriBuilder(baseUri) { Query = string.Empty, Fragment = string.Empty };
        var path = builder.Path.TrimEnd('/');
        path = kind switch
        {
            ProviderKind.GitHub when path.EndsWith("/api/v3", StringComparison.OrdinalIgnoreCase) => path[..^7],
            ProviderKind.GitHub when builder.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase) => string.Empty,
            ProviderKind.GitLab when path.EndsWith("/api/v4", StringComparison.OrdinalIgnoreCase) => path[..^7],
            _ => path,
        };
        builder.Host = builder.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase) ? "github.com" : builder.Host;
        builder.Path = $"{path}/{owner}/{repository}.git";
        if (git.Mode == ConfiguredGitAuthenticationMode.Ssh)
        {
            builder.Scheme = Uri.UriSchemeSsh;
            builder.Port = -1;
            builder.UserName = git.SshUsername ?? string.Empty;
        }
        return builder.Uri.ToString();
    }
}
