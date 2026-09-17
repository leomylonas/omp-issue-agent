using IssueAgent.Configuration;
using Microsoft.Extensions.Options;

namespace IssueAgent.Configuration.Tests;

public sealed class IssueAgentOptionsValidatorTests
{
    private readonly IssueAgentOptionsValidator validator = new();

    [Fact]
    public void ValidateRejectsAnonymousProviderWithoutIdentityOverride()
    {
        var options = CreateOptions() with
        {
            Providers = [CreateProvider() with { Token = null, IdentityOverride = null }],
        };

        var result = validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, failure => failure.Contains("requires IdentityOverride", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidateRejectsSecretWithEnvAndFileSources()
    {
        var options = CreateOptions() with
        {
            Providers = [CreateProvider() with { Token = new SecretSource { Env = "TOKEN", File = "/run/secrets/token" } }],
        };

        var result = validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, failure => failure.Contains("exactly one", StringComparison.Ordinal));
    }

    [Fact]
    public void SecretSourceResolvesExactlyOneConfiguredSource()
    {
        var secret = new SecretSource { Env = "TOKEN" };

        var value = secret.Resolve(name => name == "TOKEN" ? " value " : null, _ => throw new InvalidOperationException());

        Assert.Equal("value", value);
    }

    [Fact]
    public void SecretSourceRejectsEmptyResolvedValue()
    {
        var secret = new SecretSource { Env = "TOKEN" };

        var exception = Assert.Throws<InvalidOperationException>(() => secret.Resolve(_ => " ", _ => throw new InvalidOperationException()));

        Assert.Equal("The configured secret source resolved to an empty value.", exception.Message);
    }

    private static IssueAgentOptions CreateOptions() => new()
    {
        Workspace = new WorkspaceOptions { RootPath = "/data" },
        Omp = new OmpOptions { ExecutablePath = "/usr/local/bin/omp" },
        Providers = [CreateProvider()],
    };

    private static ProviderOptions CreateProvider() => new()
    {
        Name = "github",
        Kind = ProviderKind.GitHub,
        BaseUri = new Uri("https://api.github.example/"),
        Token = new SecretSource { Env = "GITHUB_TOKEN" },
        Repositories = [new RepositoryOptions { Id = "github/example/repo", Name = "example/repo" }],
    };
}
