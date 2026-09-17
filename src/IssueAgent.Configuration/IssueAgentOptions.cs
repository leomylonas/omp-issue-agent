using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace IssueAgent.Configuration;

/// <summary>Root immutable configuration for the IssueAgent process.</summary>
public sealed record IssueAgentOptions
{
    public const string SectionName = "IssueAgent";

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(60);

    public ConcurrencyOptions Concurrency { get; init; } = new();

    public required WorkspaceOptions Workspace { get; init; }

    public required OmpOptions Omp { get; init; }

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

    public IReadOnlyDictionary<string, string> Roles { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["planning"] = "plan",
        ["implementation"] = "task",
        ["revision"] = "task",
        ["conflictResolution"] = "task",
    };
}

public sealed record ProviderOptions
{
    public required string Name { get; init; }

    public required ProviderKind Kind { get; init; }

    public required Uri BaseUri { get; init; }

    public SecretSource? Token { get; init; }

    public string? IdentityOverride { get; init; }

    public string? DefaultOwnerOrNamespace { get; init; }

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

    public bool Enabled { get; init; } = true;

    public DateTimeOffset? StartDate { get; init; }
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

        var providerNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var repositoryIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in options.Providers)
        {
            if (!providerNames.Add(provider.Name))
            {
                failures.Add($"Provider name '{provider.Name}' is duplicated.");
            }

            if (!provider.BaseUri.IsAbsoluteUri || provider.BaseUri.Scheme is not ("https" or "http"))
            {
                failures.Add($"Provider '{provider.Name}' must define an absolute HTTP(S) BaseUri.");
            }

            if (provider.Token is { } token && !token.IsExactlyOneSource())
            {
                failures.Add($"Provider '{provider.Name}' token must configure exactly one of env or file.");
            }

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
            }
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
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
