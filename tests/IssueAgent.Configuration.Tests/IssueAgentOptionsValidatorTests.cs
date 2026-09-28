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

    [Theory]
    [InlineData(0, 1, 0)]
    [InlineData(3, 0.5, 0)]
    [InlineData(3, 2, -1)]
    public void ValidateRejectsInvalidRootRetryPolicy(int attempts, double multiplier, int jitterMilliseconds)
    {
        var options = CreateOptions() with
        {
            Retry = new RetryOptions
            {
                MaxAttempts = attempts,
                BackoffMultiplier = multiplier,
                MaxJitter = TimeSpan.FromMilliseconds(jitterMilliseconds),
            },
        };

        var result = validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, failure => failure.StartsWith("IssueAgent:Retry:", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidateRejectsInvalidRateLimitFallbackDelay()
    {
        var options = CreateOptions() with
        {
            Retry = new RetryOptions { RateLimitFallbackDelay = TimeSpan.Zero },
        };

        var result = validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, failure => failure.Contains("RateLimitFallbackDelay", StringComparison.Ordinal));
    }

    [Fact]
    public void RootRetryOptionsResolveTheConfiguredPolicy()
    {
        var options = new RetryOptions
        {
            MaxAttempts = 4,
            InitialDelay = TimeSpan.FromMilliseconds(20),
            BackoffMultiplier = 3,
            MaxJitter = TimeSpan.Zero,
            RateLimitFallbackDelay = TimeSpan.FromSeconds(10),
        };

        var policy = options.ToPolicy();

        Assert.Equal(4, policy.MaxAttempts);
        Assert.Equal(TimeSpan.FromMilliseconds(20), policy.GetDelay(1));
        Assert.Equal(TimeSpan.FromMilliseconds(60), policy.GetDelay(2));
        Assert.Equal(TimeSpan.FromSeconds(10), policy.GetRateLimitFallbackDelay(1));
        Assert.Equal(TimeSpan.FromSeconds(30), policy.GetRateLimitFallbackDelay(2));
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
    public void ValidateRejectsRepositoryOmpExecutionSecretWithMultipleSources()
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
                            Id = "github/example/repository",
                            Name = "example/repository",
                            Settings = new RepositorySettingsOptions
                            {
                                OmpExecutionSecrets = new Dictionary<string, SecretSource>
                                {
                                    ["SECRET"] = new() { Env = "SECRET", File = "/run/secrets/secret" },
                                },
                            },
                        },
                    ],
                },
            ],
        };

        var result = validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, failure => failure.Contains("OMP execution secret 'SECRET' must configure exactly one", StringComparison.Ordinal));
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

    [Theory]
    [InlineData(ConfiguredGitAuthenticationMode.ProviderToken)]
    [InlineData(ConfiguredGitAuthenticationMode.Token)]
    public void ValidateRejectsPlaintextCloneUrlWhenTokenAuthenticationIsConfigured(ConfiguredGitAuthenticationMode mode)
    {
        var options = CreateOptions() with
        {
            Defaults = new RepositorySettingsOptions
            {
                Git = new GitTransportOptions
                {
                    Mode = mode,
                    Token = mode == ConfiguredGitAuthenticationMode.Token
                        ? new SecretSource { Env = "GIT_TOKEN" }
                        : null,
                },
            },
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
                            CloneUrl = "http://github.example/example/repo.git",
                        },
                    ],
                },
            ],
        };

        var result = validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, failure => failure.Contains("CloneUrl must use HTTPS with token authentication", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidateRejectsPlaintextDerivedCloneUrlWhenInheritedTokenAuthenticationIsConfigured()
    {
        var options = CreateOptions() with
        {
            Defaults = new RepositorySettingsOptions
            {
                Git = new GitTransportOptions
                {
                    Mode = ConfiguredGitAuthenticationMode.Token,
                    Token = new SecretSource { Env = "GIT_TOKEN" },
                },
            },
            Providers =
            [
                CreateProvider() with
                {
                    BaseUri = new Uri("http://github.example/"),
                    Token = null,
                    IdentityOverride = "IssueAgent",
                },
            ],
        };

        var result = validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, failure => failure.Contains("derived CloneUrl must use HTTPS with token authentication", StringComparison.Ordinal));
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
    public void ValidateRejectsUnknownRoutingEventsAndSinksThatAreNotEnabled()
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
                Routing = new Dictionary<string, IReadOnlySet<string>>
                {
                    ["UnknownEvent"] = new HashSet<string> { "telegram" },
                    ["PlanReady"] = new HashSet<string> { "slack" },
                },
            },
        };

        var result = validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, failure => failure.Contains("Routing event 'UnknownEvent' is not supported", StringComparison.Ordinal));
        Assert.Contains(result.Failures!, failure => failure.Contains("Routing:PlanReady names sink 'slack', which is not enabled", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidateAcceptsRoutingToEnabledSinksForKnownEvents()
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
                Slack = new SlackOptions
                {
                    WebhookUrl = new SecretSource { Env = "SLACK_WEBHOOK_URL" },
                },
                Routing = new Dictionary<string, IReadOnlySet<string>>
                {
                    ["PlanReady"] = new HashSet<string> { "telegram", "slack" },
                },
            },
        };

        Assert.False(validator.Validate(null, options).Failed);
    }

    [Fact]
    public void ValidateRejectsDuplicateProviderNativeRepositoryIdentity()
    {
        var options = CreateOptions() with
        {
            Providers =
            [
                CreateProvider() with
                {
                    DefaultOwnerOrNamespace = "Example",
                    Repositories =
                    [
                        new RepositoryOptions { Id = "github/example/first", Name = "Repository" },
                        new RepositoryOptions { Id = "github/example/second", Name = "example/repository" },
                    ],
                },
            ],
        };

        var result = validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, failure => failure.Contains("duplicates a provider-native owner/name identity", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void ValidateRejectsBlankRepositoryName(string repositoryName)
    {
        var options = CreateOptions() with
        {
            Providers = [CreateProvider() with
            {
                Repositories = [new RepositoryOptions { Id = "github/example/repository", Name = repositoryName }],
            }],
        };

        var result = validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, failure => failure.Contains("Name must be non-empty", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void ValidateRejectsBlankExplicitRepositoryOwner(string ownerOrNamespace)
    {
        var options = CreateOptions() with
        {
            Providers = [CreateProvider() with
            {
                Repositories = [new RepositoryOptions
                {
                    Id = "github/example/repository",
                    Name = "repository",
                    OwnerOrNamespace = ownerOrNamespace,
                }],
            }],
        };

        var result = validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, failure => failure.Contains("OwnerOrNamespace", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("/repository")]
    [InlineData("owner/")]
    [InlineData("owner//repository")]
    public void ValidateRejectsRepositoryNameWithBlankPathComponent(string repositoryName)
    {
        var options = CreateOptions() with
        {
            Providers = [CreateProvider() with
            {
                Repositories = [new RepositoryOptions { Id = "github/example/repository", Name = repositoryName }],
            }],
        };

        var result = validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, failure => failure.Contains("Name must not contain blank path components", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("/owner")]
    [InlineData("owner/")]
    [InlineData("owner//subgroup")]
    public void ValidateRejectsExplicitOwnerWithBlankPathComponent(string ownerOrNamespace)
    {
        var options = CreateOptions() with
        {
            Providers = [CreateProvider() with
            {
                Repositories = [new RepositoryOptions
                {
                    Id = "github/example/repository",
                    Name = "repository",
                    OwnerOrNamespace = ownerOrNamespace,
                }],
            }],
        };

        var result = validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, failure => failure.Contains("OwnerOrNamespace", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("/repository")]
    [InlineData("../repository")]
    [InlineData("github/../repository")]
    public void ValidateRejectsUnsafeRepositoryIds(string repositoryId)
    {
        var options = CreateOptions() with
        {
            Providers = [CreateProvider() with
            {
                Repositories = [new RepositoryOptions { Id = repositoryId, Name = "example/repository" }],
            }],
        };

        var result = validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, failure => failure.Contains("Repository id", StringComparison.Ordinal));
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
    public void ValidateAcceptsSshPassphraseSecretAndStillRejectsMissingTokenCredential()
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
        Assert.DoesNotContain(result.Failures!, failure => failure.Contains("passphrases are not supported", StringComparison.Ordinal));
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
