using IssueAgent.Configuration;
using IssueAgent.Git;
using Xunit;

namespace IssueAgent.Host.Tests;

public sealed class OmpRuntimeEnvironmentFactoryTests
{
    [Fact]
    public void CreateUsesOnlyEffectiveRepositoryExecutionValues()
    {
        var options = new IssueAgentOptions
        {
            Workspace = new WorkspaceOptions { RootPath = "/data" },
            Omp = new OmpOptions
            {
                ExecutablePath = "omp",
                ConnectionSettings = new Dictionary<string, string> { ["OMP_CONNECTION"] = "connection" },
                ExecutionVariables = new Dictionary<string, string> { ["GLOBAL_FLAG"] = "global", ["OVERRIDE"] = "global" },
                ExecutionSecrets = new Dictionary<string, SecretSource> { ["GLOBAL_SECRET"] = new() { Env = "GLOBAL_SECRET" } },
            },
            Providers =
            [
                new ProviderOptions
                {
                    Name = "github",
                    Kind = ProviderKind.GitHub,
                    BaseUri = new Uri("https://api.github.com/"),
                    Token = new SecretSource { Env = "HTTP_PROXY" },
                    IdentityOverride = "bot",
                    Repositories =
                    [
                        new RepositoryOptions
                        {
                            Id = "github/octo/first",
                            Name = "octo/first",
                            Settings = new RepositorySettingsOptions
                            {
                                OmpExecutionVariables = new Dictionary<string, string> { ["OVERRIDE"] = "first" },
                                OmpExecutionSecrets = new Dictionary<string, SecretSource> { ["FIRST_SECRET"] = new() { Env = "FIRST_SECRET" } },
                            },
                        },
                        new RepositoryOptions
                        {
                            Id = "github/octo/second",
                            Name = "octo/second",
                            Settings = new RepositorySettingsOptions
                            {
                                OmpExecutionSecrets = new Dictionary<string, SecretSource> { ["SECOND_SECRET"] = new() { Env = "SECOND_SECRET" } },
                            },
                        },
                    ],
                },
            ],
        };
        var configuration = EffectiveConfigurationResolver.Resolve(options, name => $"{name}-value", _ => throw new InvalidOperationException());
        var repository = Assert.Single(configuration.Providers).Repositories.Single(value => value.Id.EndsWith("first", StringComparison.Ordinal));

        var environment = new OmpRuntimeEnvironmentFactory(configuration).Create(
            new Dictionary<string, string?>
            {
                ["PATH"] = "/usr/bin",
                ["HTTP_PROXY"] = "http://provider-secret@proxy.example:8080",
                ["GITHUB_TOKEN"] = "provider-token",
                ["SECOND_SECRET"] = "ambient-secret",
            },
            new GitIdentity("Agent", "agent@example.test"),
            repository);

        Assert.Equal("/usr/bin", environment["PATH"]);
        Assert.Equal("connection", environment["OMP_CONNECTION"]);
        Assert.Equal("global", environment["GLOBAL_FLAG"]);
        Assert.Equal("first", environment["OVERRIDE"]);
        Assert.Equal("GLOBAL_SECRET-value", environment["GLOBAL_SECRET"]);
        Assert.Equal("FIRST_SECRET-value", environment["FIRST_SECRET"]);
        Assert.DoesNotContain("GITHUB_TOKEN", environment.Keys);
        Assert.DoesNotContain("SECOND_SECRET", environment.Keys);
        Assert.DoesNotContain("HTTP_PROXY", environment.Keys);
    }
}
