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

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ValidateRejectsNonPositiveGlobalOmpTimeout(int seconds)
    {
        var options = CreateOptions() with
        {
            Omp = new OmpOptions
            {
                ExecutablePath = "/usr/local/bin/omp",
                Timeout = TimeSpan.FromSeconds(seconds),
            },
        };

        var result = validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, failure => failure.Contains("IssueAgent:Omp:Timeout must be greater than zero.", StringComparison.Ordinal));
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

    [Fact]
    public void ValidateRejectsSshWithoutExplicitHostVerification()
    {
        var options = CreateOptions() with
        {
            Defaults = new RepositorySettingsOptions
            {
                Git = new GitTransportOptions { Mode = ConfiguredGitAuthenticationMode.Ssh },
            },
        };

        var result = validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, failure => failure.Contains("host verification", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidateAcceptsARepositoryThatOverridesOnlyGitModeWhileInheritingItsPinnedSshTrustPolicyFromDefaults()
    {
        // Regression: a repository-level override of only `git.mode` must be validated against the
        // merged global -> provider -> repository result, not evaluated in isolation — the merged
        // result here does have a host verification policy, inherited from Defaults.
        var options = CreateOptions() with
        {
            Defaults = new RepositorySettingsOptions
            {
                Git = new GitTransportOptions
                {
                    SshPrivateKey = new SecretSource { Env = "SSH_KEY" },
                    SshTrust = new SshTrustOptions { Mode = ConfiguredSshHostVerificationMode.Pinned, Fingerprints = ["sha256:host"] },
                },
            },
            Providers = [CreateProvider() with
            {
                Repositories = [new RepositoryOptions
                {
                    Id = "github/example/repo",
                    Name = "example/repo",
                    Settings = new RepositorySettingsOptions { Git = new GitTransportOptions { Mode = ConfiguredGitAuthenticationMode.Ssh } },
                }],
            }],
        };

        var result = validator.Validate(null, options);

        Assert.False(result.Failed);
    }

    [Fact]
    public void ValidateAcceptsARepositoryThatOverridesOnlyPinnedTlsModeWhileInheritingItsFingerprintsFromDefaults()
    {
        var options = CreateOptions() with
        {
            Defaults = new RepositorySettingsOptions
            {
                Git = new GitTransportOptions { Tls = new TlsTrustOptions { Fingerprints = ["sha256:cert"] } },
            },
            Providers = [CreateProvider() with
            {
                Repositories = [new RepositoryOptions
                {
                    Id = "github/example/repo",
                    Name = "example/repo",
                    Settings = new RepositorySettingsOptions { Git = new GitTransportOptions { Tls = new TlsTrustOptions { Mode = ConfiguredTlsTrustMode.Pinned } } },
                }],
            }],
        };

        var result = validator.Validate(null, options);

        Assert.False(result.Failed);
    }

    [Fact]
    public void ValidateStillRejectsAPinnedTrustModeThatHasNoFingerprintsAnywhereInTheMergedChain()
    {
        var options = CreateOptions() with
        {
            Providers = [CreateProvider() with
            {
                Repositories = [new RepositoryOptions
                {
                    Id = "github/example/repo",
                    Name = "example/repo",
                    Settings = new RepositorySettingsOptions { Git = new GitTransportOptions { Tls = new TlsTrustOptions { Mode = ConfiguredTlsTrustMode.Pinned } } },
                }],
            }],
        };

        var result = validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, failure => failure.Contains("Pinned TLS trust requires", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidateReportsMissingProviderFieldsInsteadOfThrowing()
    {
        var options = CreateOptions() with
        {
            Providers = [CreateProvider() with { Name = null!, BaseUri = null! }],
        };

        var result = validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, failure => failure.Contains("non-empty Name", StringComparison.Ordinal));
        Assert.Contains(result.Failures!, failure => failure.Contains("absolute HTTP(S) BaseUri", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidateRejectsCredentialsInProviderAndCloneUrls()
    {
        var options = CreateOptions() with
        {
            Providers =
            [
                CreateProvider() with
                {
                    BaseUri = new Uri("https://token:secret@example.test/"),
                    Repositories =
                    [
                        new RepositoryOptions
                        {
                            Id = "github/example/repo",
                            Name = "example/repo",
                            CloneUrl = "https://token:secret@example.test/example/repo.git",
                        },
                    ],
                },
            ],
        };

        var result = validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, failure => failure.Contains("must not contain credentials", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidateRejectsPlaintextProviderBaseUriWhenCredentialsAreConfigured()
    {
        var options = CreateOptions() with
        {
            Providers = [CreateProvider() with { BaseUri = new Uri("http://github.example/") }],
        };

        var result = validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, failure => failure.Contains("BaseUri must use HTTPS", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidateRejectsTelegramWithoutExactlyOneTokenSourceOrChatId()
    {
        var options = CreateOptions() with
        {
            Notifications = new NotificationsOptions
            {
                Telegram = new TelegramOptions
                {
                    BotToken = new SecretSource { Env = "TELEGRAM_TOKEN", File = "/run/secrets/telegram" },
                    ChatId = " ",
                },
            },
        };

        var result = validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, failure => failure.Contains("Telegram:BotToken must configure exactly one", StringComparison.Ordinal));
        Assert.Contains(result.Failures!, failure => failure.Contains("Telegram:ChatId must be non-empty", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidateAcceptsTelegramWithExactlyOneTokenSourceAndChatId()
    {
        var options = CreateOptions() with
        {
            Notifications = new NotificationsOptions
            {
                Telegram = new TelegramOptions
                {
                    BotToken = new SecretSource { Env = "TELEGRAM_TOKEN" },
                    ChatId = "123",
                },
            },
        };

        Assert.False(validator.Validate(null, options).Failed);
    }

    [Fact]
    public void ValidateRejectsCloneUrlQueryAndFragment()
    {
        var options = CreateOptions() with
        {
            Providers =
            [
                CreateProvider() with
                {
                    Repositories =
                    [
                        new RepositoryOptions
                        {
                            Id = "github/example/repo",
                            Name = "example/repo",
                            CloneUrl = "https://example.test/example/repo.git?access_token=secret#fragment",
                        },
                    ],
                },
            ],
        };

        var result = validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, failure => failure.Contains("CloneUrl must not contain credentials", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidateRejectsUnsupportedSshPassphrasesAndMissingModeCredentials()
    {
        var options = CreateOptions() with
        {
            Defaults = new RepositorySettingsOptions
            {
                Git = new GitTransportOptions
                {
                    Mode = ConfiguredGitAuthenticationMode.Token,
                    SshPrivateKeyPassphrase = new SecretSource { Env = "SSH_PASSPHRASE" },
                },
            },
            Providers = [CreateProvider() with { Token = null }],
        };

        var result = validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, failure => failure.Contains("passphrases are not supported", StringComparison.Ordinal));
        Assert.Contains(result.Failures!, failure => failure.Contains("Git Token authentication requires", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidateRejectsInvalidNotificationTlsTrust()
    {
        var options = CreateOptions() with
        {
            Notifications = new NotificationsOptions
            {
                Tls = new TlsTrustOptions { Mode = ConfiguredTlsTrustMode.Pinned },
            },
        };

        var result = validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, failure => failure.Contains("pinned TLS trust requires", StringComparison.OrdinalIgnoreCase));
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
