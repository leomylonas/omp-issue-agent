using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace IssueAgent.Configuration;

/// <summary>Root immutable configuration for the IssueAgent process.</summary>
public sealed record IssueAgentOptions
{
    public const string SectionName = "IssueAgent";

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(60);

    public DateTimeOffset StartDate { get; init; } = DateTimeOffset.MinValue;

    public TimeSpan ShutdownGracePeriod { get; init; } = TimeSpan.FromSeconds(15);

    public ConcurrencyOptions Concurrency { get; init; } = new();

    public required WorkspaceOptions Workspace { get; init; }

    public required OmpOptions Omp { get; init; }

    public NotificationsOptions Notifications { get; init; } = new();
    public RepositorySettingsOptions Defaults { get; init; } = new();
    public IReadOnlyList<ProviderOptions> Providers { get; init; } = [];
}

public sealed record ConcurrencyOptions
{
    public int Agent { get; init; } = 3;

    public int Polling { get; init; } = 10;
}

public sealed record WorkspaceOptions
{
    public required string RootPath { get; init; }
}
public sealed record OmpOptions
{
    public required string ExecutablePath { get; init; }

    public TimeSpan? Timeout { get; init; }

    public string? AuthBrokerUrl { get; init; }

    public IReadOnlyDictionary<string, string> ConnectionSettings { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Non-secret execution variables supplied to every OMP process unless a repository
    /// setting overrides them.</summary>
    public IReadOnlyDictionary<string, string> ExecutionVariables { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, SecretSource> ExecutionSecrets { get; init; } =
        new Dictionary<string, SecretSource>(StringComparer.Ordinal);

    /// <summary>Semantic OMP role aliases resolved by OMP's native configuration at process
    /// startup (for example <c>plan</c> and <c>task</c>), never through its RPC protocol.</summary>
    public IReadOnlyDictionary<string, string> Roles { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["planning"] = "plan",
        ["implementation"] = "task",
        ["revision"] = "task",
        ["conflictResolution"] = "task",
    };
}
public sealed record NotificationsOptions
{
    public TelegramOptions? Telegram { get; init; }

    public SlackOptions? Slack { get; init; }

    /// <summary>TLS trust policy for outbound notification sink requests (specification §11: the
    /// configured trust policy applies to every managed HttpClient, not just Git/provider traffic).
    /// Defaults to system trust when unset.</summary>
    public TlsTrustOptions? Tls { get; init; }

    public IReadOnlyDictionary<string, IReadOnlySet<string>> Routing { get; init; } =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase);
}

public sealed record TelegramOptions
{
    public required SecretSource BotToken { get; init; }
    public required string ChatId { get; init; }
}

public sealed record SlackOptions
{
    public required SecretSource WebhookUrl { get; init; }
}

public sealed record ProviderOptions
{
    public required string Name { get; init; }

    public required ProviderKind Kind { get; init; }

    public required Uri BaseUri { get; init; }

    public SecretSource? Token { get; init; }

    public string? IdentityOverride { get; init; }

    public string? DefaultOwnerOrNamespace { get; init; }

    public RepositorySettingsOptions Defaults { get; init; } = new();
    public IReadOnlyList<RepositoryOptions> Repositories { get; init; } = [];
}

public enum ProviderKind
{
    GitHub,
    GitLab,
}

public sealed record RepositoryOptions
{
    public required string Id { get; init; }

    public required string Name { get; init; }
    public string? OwnerOrNamespace { get; init; }

    public string? CloneUrl { get; init; }

    public string? TargetBranch { get; init; }

    public bool Enabled { get; init; } = true;

    public DateTimeOffset? StartDate { get; init; }

    public RepositorySettingsOptions Settings { get; init; } = new();
}

/// <summary>A secret configured from exactly one source and resolved once at startup.</summary>
public sealed record SecretSource
{
    public string? Env { get; init; }

    public string? File { get; init; }

    public string Resolve(Func<string, string?> environmentReader, Func<string, string> fileReader)
    {
        ArgumentNullException.ThrowIfNull(environmentReader);
        ArgumentNullException.ThrowIfNull(fileReader);

        if (!IsExactlyOneSource())
        {
            throw new InvalidOperationException("A secret must configure exactly one of env or file.");
        }

        var value = Env is not null ? environmentReader(Env) : fileReader(File!);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException("The configured secret source resolved to an empty value.");
        }

        return value.Trim();
    }

    internal bool IsExactlyOneSource() => (Env is null) != (File is null);
}

/// <summary>Fail-fast semantic validation for root options that do not require network access.</summary>
public sealed class IssueAgentOptionsValidator : IValidateOptions<IssueAgentOptions>
{
    public ValidateOptionsResult Validate(string? name, IssueAgentOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();

        if (options.PollInterval <= TimeSpan.Zero)
        {
            failures.Add("IssueAgent:PollInterval must be greater than zero.");
        }

        if (options.ShutdownGracePeriod <= TimeSpan.Zero)
        {
            failures.Add("IssueAgent:ShutdownGracePeriod must be greater than zero.");
        }

        if (options.Concurrency.Agent <= 0 || options.Concurrency.Polling <= 0)
        {
            failures.Add("IssueAgent:Concurrency Agent and Polling values must be greater than zero.");
        }

        if (string.IsNullOrWhiteSpace(options.Workspace.RootPath))
        {
            failures.Add("IssueAgent:Workspace:RootPath is required.");
        }

        if (string.IsNullOrWhiteSpace(options.Omp.ExecutablePath))
        {
            failures.Add("IssueAgent:Omp:ExecutablePath is required.");
        }
        if (options.Omp.Timeout is { } timeout && timeout <= TimeSpan.Zero)
        {
            failures.Add("IssueAgent:Omp:Timeout must be greater than zero.");
        }
        foreach (var (secretName, source) in options.Omp.ExecutionSecrets)
        {
            if (!source.IsExactlyOneSource())
            {
                failures.Add($"IssueAgent:Omp:ExecutionSecrets:{secretName} must configure exactly one of env or file.");
            }
        }

        if (options.Notifications.Tls is { } notificationTls)
        {
            ValidateTlsTrust(notificationTls, "IssueAgent:Notifications:Tls", failures);
        }

        if (options.Notifications.Telegram is { } telegram)
        {
            if (!telegram.BotToken.IsExactlyOneSource())
            {
                failures.Add("IssueAgent:Notifications:Telegram:BotToken must configure exactly one of env or file.");
            }
            if (string.IsNullOrWhiteSpace(telegram.ChatId))
            {
                failures.Add("IssueAgent:Notifications:Telegram:ChatId must be non-empty.");
            }
        }

        if (options.Notifications.Slack is { } slack && !slack.WebhookUrl.IsExactlyOneSource())
        {
            failures.Add("IssueAgent:Notifications:Slack:WebhookUrl must configure exactly one secret source.");
        }


        var providerNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var repositoryIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ValidateLocalSettings(options.Defaults, "IssueAgent:Defaults", failures);
        foreach (var provider in options.Providers)
        {
            if (string.IsNullOrWhiteSpace(provider.Name))
            {
                failures.Add("Every provider must define a non-empty Name.");
            }
            else if (!providerNames.Add(provider.Name))
            {
                failures.Add($"Provider name '{provider.Name}' is duplicated.");
            }

            if (provider.BaseUri is null ||
                !provider.BaseUri.IsAbsoluteUri ||
                provider.BaseUri.Scheme is not ("https" or "http"))
            {
                failures.Add($"Provider '{provider.Name}' must define an absolute HTTP(S) BaseUri.");
            }
            else if (provider.BaseUri.UserInfo.Length > 0)
            {
                failures.Add($"Provider '{provider.Name}' BaseUri must not contain credentials.");
            }
            else if (provider.Token is not null &&
                     !provider.BaseUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                failures.Add($"Provider '{provider.Name}' BaseUri must use HTTPS when credentials are configured.");
            }

            if (provider.Token is { } token && !token.IsExactlyOneSource())
            {
                failures.Add($"Provider '{provider.Name}' token must configure exactly one of env or file.");
            }
            ValidateLocalSettings(provider.Defaults, $"Provider '{provider.Name}' defaults", failures);

            if (provider.Token is null && string.IsNullOrWhiteSpace(provider.IdentityOverride))
            {
                failures.Add($"Anonymous provider '{provider.Name}' requires IdentityOverride for assignment discovery.");
            }

            foreach (var repository in provider.Repositories)
            {
                if (!repositoryIds.Add(repository.Id))
                {
                    failures.Add($"Repository id '{repository.Id}' is duplicated across providers.");
                }
                ValidateLocalSettings(repository.Settings, $"Repository '{repository.Id}' settings", failures);
                var mergedTrust = EffectiveConfigurationResolver.Merge(options.Defaults, provider.Defaults, repository.Settings);
                ValidateMergedTrust(mergedTrust, $"Repository '{repository.Id}' (merged effective settings)", failures);
                ValidateGitCredentials(provider, mergedTrust, $"Repository '{repository.Id}'", failures);
                if (repository.CloneUrl is { } cloneUrl &&
                    Uri.TryCreate(cloneUrl, UriKind.Absolute, out var parsedCloneUrl) &&
                    ((parsedCloneUrl.Scheme is "http" or "https" && parsedCloneUrl.UserInfo.Length > 0) ||
                     (parsedCloneUrl.Scheme == "ssh" && parsedCloneUrl.UserInfo.Contains(':', StringComparison.Ordinal)) ||
                     parsedCloneUrl.Query.Length > 0 ||
                     parsedCloneUrl.Fragment.Length > 0))
                {
                    failures.Add($"Repository '{repository.Id}' CloneUrl must not contain credentials or query/fragment components.");
                }
                var hasOwner = !string.IsNullOrWhiteSpace(repository.OwnerOrNamespace) ||
                    repository.Name.Contains('/', StringComparison.Ordinal) ||
                    !string.IsNullOrWhiteSpace(provider.DefaultOwnerOrNamespace);
                if (!hasOwner)
                {
                    failures.Add($"Repository '{repository.Id}' requires an owner/namespace or an owner-qualified name.");
                }
            }
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    /// <summary>Intrinsically local shape checks (secret-source arity, non-negative numbers) that
    /// hold regardless of what a higher or lower configuration level supplies, so these run at every
    /// level independently rather than only on the merged result.</summary>
    private static void ValidateLocalSettings(RepositorySettingsOptions settings, string path, List<string> failures)
    {
        if (settings.RelatedIssueTraversalDepth is < 0)
        {
            failures.Add($"{path} RelatedIssueTraversalDepth cannot be negative.");
        }
        if (settings.MaxAttachmentSizeBytes is <= 0 || settings.MaxTotalAttachmentSizeBytes is <= 0)
        {
            failures.Add($"{path} attachment limits must be greater than zero.");
        }
        if (settings.OmpTimeout is { } timeout && timeout <= TimeSpan.Zero)
        {
            failures.Add($"{path} OmpTimeout must be greater than zero.");
        }
        var git = settings.Git;
        foreach (var (name, source) in settings.OmpExecutionSecrets)
        {
            if (!source.IsExactlyOneSource())
            {
                failures.Add($"{path} OMP execution secret '{name}' must configure exactly one of env or file.");
            }
        }
        ValidateTlsTrust(git?.Tls, $"{path} Git TLS", failures, requireMode: false);
        foreach (var (name, secret) in new[]
        {
            ("token", git?.Token),
            ("SSH private key", git?.SshPrivateKey),
            ("SSH private-key passphrase", git?.SshPrivateKeyPassphrase),
        })
        {
            if (secret is not null && !secret.IsExactlyOneSource())
            {
                failures.Add($"{path} {name} must configure exactly one of env or file.");
            }
        }
        if (git?.SshPrivateKeyPassphrase is not null)
        {
            failures.Add($"{path} SSH private-key passphrases are not supported; omit SshPrivateKeyPassphrase.");
        }
    }

    private static void ValidateGitCredentials(
        ProviderOptions provider,
        RepositorySettingsOptions merged,
        string path,
        List<string> failures)
    {
        var git = merged.Git;
        var mode = git?.Mode ?? (provider.Token is null
            ? ConfiguredGitAuthenticationMode.Anonymous
            : ConfiguredGitAuthenticationMode.ProviderToken);
        switch (mode)
        {
            case ConfiguredGitAuthenticationMode.ProviderToken when provider.Token is null:
                failures.Add($"{path} Git ProviderToken authentication requires a provider token.");
                break;
            case ConfiguredGitAuthenticationMode.Token when git?.Token is null:
                failures.Add($"{path} Git Token authentication requires a Git token.");
                break;
            case ConfiguredGitAuthenticationMode.Ssh when git?.SshPrivateKey is null:
                failures.Add($"{path} Git SSH authentication requires a private key.");
                break;
        }
    }

    private static void ValidateTlsTrust(
        TlsTrustOptions? tls,
        string path,
        List<string> failures,
        bool requireMode = true)
    {
        if (tls is null)
        {
            return;
        }

        if (requireMode && tls.Mode == ConfiguredTlsTrustMode.Pinned && tls.Fingerprints.Count == 0)
        {
            failures.Add($"{path} Pinned TLS trust requires at least one fingerprint.");
        }
        if (requireMode && tls.Mode == ConfiguredTlsTrustMode.SystemPlusAdditionalCa && tls.AdditionalCaCertificatePaths.Count == 0)
        {
            failures.Add($"{path} system-plus-additional-ca trust requires at least one CA certificate path.");
        }
        foreach (var certificatePath in tls.AdditionalCaCertificatePaths)
        {
            if (!Path.IsPathRooted(certificatePath) || !File.Exists(certificatePath))
            {
                failures.Add($"{path} CA certificate path '{certificatePath}' must be an existing absolute file path.");
            }
        }
    }

    /// <summary>Cross-field Git-trust checks (specification §7: providers define defaults,
    /// repositories inherit and may override) evaluated over the fully merged global → provider →
    /// repository result. A repository that overrides only <c>git.mode</c> while inheriting its
    /// trust policy from a shared default is valid and must not be flagged by evaluating any single
    /// level in isolation.</summary>
    private static void ValidateMergedTrust(RepositorySettingsOptions merged, string path, List<string> failures)
    {
        var git = merged.Git;
        if (git?.Mode == ConfiguredGitAuthenticationMode.Ssh && git.SshTrust?.Mode is null)
        {
            failures.Add($"{path} SSH Git authentication requires an explicit host verification policy.");
        }
        ValidateTlsTrust(git?.Tls, $"{path} TLS", failures);
        if (git?.SshTrust?.Mode == ConfiguredSshHostVerificationMode.Pinned && git.SshTrust.Fingerprints.Count == 0)
        {
            failures.Add($"{path} Pinned SSH host verification requires at least one fingerprint.");
        }
    }
}

public static class IssueAgentOptionsServiceCollectionExtensions
{
    public static IServiceCollection AddIssueAgentOptions(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<IssueAgentOptions>()
            .Bind(configuration.GetSection(IssueAgentOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<IssueAgentOptions>, IssueAgentOptionsValidator>();
        return services;
    }
}
