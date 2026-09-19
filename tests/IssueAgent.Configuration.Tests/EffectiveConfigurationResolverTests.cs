using IssueAgent.Configuration;

namespace IssueAgent.Configuration.Tests;

public sealed class EffectiveConfigurationResolverTests
{
    [Fact]
    public void ResolveAppliesGlobalProviderRepositoryPrecedenceAndDerivesGitHubCloneUrl()
    {
        var options = CreateOptions() with
        {
            StartDate = DateTimeOffset.Parse("2024-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
            Defaults = new RepositorySettingsOptions
            {
                IgnoreBotComments = true,
                RelatedIssueTraversalDepth = 1,
                MaxAttachmentSizeBytes = 10,
                SupplementalInstructions = ["global"],
                OmpRoles = new Dictionary<string, string> { ["planning"] = "global-plan" },
            },
            Providers =
            [
                CreateProvider() with
                {
                    DefaultOwnerOrNamespace = "octo",
                    Defaults = new RepositorySettingsOptions
                    {
                        RelatedIssueTraversalDepth = 2,
                        SupplementalInstructions = ["provider"],
                    },
                    Repositories =
                    [
                        new RepositoryOptions
                        {
                            Id = "github/octo/widgets",
                            Name = "widgets",
                            Settings = new RepositorySettingsOptions
                            {
                                IgnoreBotComments = false,
                                MaxTotalAttachmentSizeBytes = 50,
                                SupplementalInstructions = ["repository"],
                                OmpRoles = new Dictionary<string, string> { ["planning"] = "repo-plan" },
                            },
                        },
                    ],
                },
            ],
        };

        var effective = EffectiveConfigurationResolver.Resolve(options, _ => "token", _ => throw new InvalidOperationException());
        var repository = Assert.Single(Assert.Single(effective.Providers).Repositories);

        Assert.Equal("octo", repository.OwnerOrNamespace);
        Assert.Equal("widgets", repository.Name);
        Assert.Equal("https://github.com/octo/widgets.git", repository.CloneUrl);
        Assert.Null(repository.TargetBranch);
        Assert.Equal(options.StartDate, repository.StartDate);
        Assert.False(repository.IgnoreBotComments);
        Assert.Equal(2, repository.RelatedIssueTraversalDepth);
        Assert.Equal(10, repository.MaxAttachmentSizeBytes);
        Assert.Equal(50, repository.MaxTotalAttachmentSizeBytes);
        Assert.Equal(["global", "provider", "repository"], repository.SupplementalInstructions);
        Assert.Equal("repo-plan", repository.OmpRoles["planning"]);
        Assert.Equal(ConfiguredGitAuthenticationMode.ProviderToken, repository.Git.Mode);
        Assert.Equal("token", repository.Git.Token);
    }

    [Fact]
    public void ResolveInheritsIssueClosingSettingThroughProviderAndRepository()
    {
        var options = CreateOptions() with
        {
            Defaults = new RepositorySettingsOptions { CloseIssueOnMerge = false },
            Providers =
            [
                CreateProvider() with
                {
                    Defaults = new RepositorySettingsOptions { CloseIssueOnMerge = true },
                    Repositories =
                    [
                        new RepositoryOptions
                        {
                            Id = "github/octo/widgets",
                            Name = "octo/widgets",
                            Settings = new RepositorySettingsOptions { CloseIssueOnMerge = false },
                        },
                    ],
                },
            ],
        };

        var repository = Assert.Single(Assert.Single(
            EffectiveConfigurationResolver.Resolve(options, _ => "token", _ => throw new InvalidOperationException()).Providers).Repositories);

        Assert.False(repository.CloseIssueOnMerge);
    }

    [Fact]
    public void ResolveAppliesConfiguredGitIdentityInsteadOfHardcodingIssueAgentAtEveryLevel()
    {
        // Regression: commit author/committer identity was previously hardcoded downstream
        // (WorkflowDispatcher.cs), contradicting specification §10's "defaults to provider
        // identity, with override".
        var options = CreateOptions() with
        {
            Defaults = new RepositorySettingsOptions
            {
                GitIdentity = new GitIdentityOptions { Name = "Org Bot", Email = "bot@org.example" },
            },
            Providers = [CreateProvider() with { Repositories = [new RepositoryOptions { Id = "github/octo/widgets", Name = "octo/widgets" }] }],
        };

        var effective = EffectiveConfigurationResolver.Resolve(options, _ => "token", _ => throw new InvalidOperationException());
        var repository = Assert.Single(Assert.Single(effective.Providers).Repositories);

        Assert.Equal("Org Bot", repository.GitIdentityName);
        Assert.Equal("bot@org.example", repository.GitIdentityEmail);
    }

    [Fact]
    public void ResolveDefaultsGitIdentityWhenUnconfigured()
    {
        var options = CreateOptions() with
        {
            Providers = [CreateProvider() with { Repositories = [new RepositoryOptions { Id = "github/octo/widgets", Name = "octo/widgets" }] }],
        };

        var effective = EffectiveConfigurationResolver.Resolve(options, _ => "token", _ => throw new InvalidOperationException());
        var repository = Assert.Single(Assert.Single(effective.Providers).Repositories);

        Assert.Equal("bot", repository.GitIdentityName);
        Assert.Equal("issue-agent@localhost", repository.GitIdentityEmail);
    }

    [Fact]
    public void ResolveMarksAuthenticatedProviderIdentityAsGitIdentityDefault()
    {
        var options = CreateOptions() with
        {
            Providers =
            [
                CreateProvider() with
                {
                    IdentityOverride = null,
                    Repositories = [new RepositoryOptions { Id = "github/octo/widgets", Name = "octo/widgets" }],
                },
            ],
        };

        var repository = Assert.Single(Assert.Single(
            EffectiveConfigurationResolver.Resolve(options, _ => "token", _ => throw new InvalidOperationException()).Providers).Repositories);

        Assert.True(repository.UsesProviderIdentityForName);
        Assert.True(repository.UsesProviderIdentityForEmail);
    }

    [Fact]
    public void ResolveDoesNotRequireProviderIdentityForAnonymousOverride()
    {
        var options = CreateOptions() with
        {
            Providers =
            [
                CreateProvider() with
                {
                    Token = null,
                    IdentityOverride = "anonymous-bot",
                    Repositories = [new RepositoryOptions { Id = "github/octo/widgets", Name = "octo/widgets" }],
                },
            ],
        };

        var repository = Assert.Single(Assert.Single(
            EffectiveConfigurationResolver.Resolve(options, _ => null, _ => throw new InvalidOperationException()).Providers).Repositories);

        Assert.False(repository.UsesProviderIdentityForName);
        Assert.False(repository.UsesProviderIdentityForEmail);
        Assert.Equal("anonymous-bot", repository.GitIdentityName);
    }

    [Fact]
    public void ResolveAppliesConfiguredNotificationsTlsTrustInsteadOfDefaultingToSystemForEveryField()
    {
        // Regression: notification sink HttpClients previously hardcoded TlsTrust.System regardless
        // of configuration, unlike every other managed HttpClient (specification §11).
        var options = CreateOptions() with
        {
            Notifications = new NotificationsOptions
            {
                Tls = new TlsTrustOptions
                {
                    Mode = ConfiguredTlsTrustMode.Pinned,
                    Fingerprints = ["sha256:notify"],
                },
            },
        };

        var effective = EffectiveConfigurationResolver.Resolve(options, _ => "token", _ => throw new InvalidOperationException());

        Assert.Equal(ConfiguredTlsTrustMode.Pinned, effective.Notifications.TlsMode);
        Assert.Equal(["sha256:notify"], effective.Notifications.TlsFingerprints);
    }

    [Fact]
    public void ResolveDefaultsNotificationsTlsTrustToSystemWhenUnconfigured()
    {
        var effective = EffectiveConfigurationResolver.Resolve(CreateOptions(), _ => "token", _ => throw new InvalidOperationException());

        Assert.Equal(ConfiguredTlsTrustMode.System, effective.Notifications.TlsMode);
        Assert.Empty(effective.Notifications.TlsFingerprints);
    }

    [Fact]
    public void ResolveUsesRepositoryOwnerCloneTargetStartAndSecurityOverrides()
    {
        var startDate = DateTimeOffset.Parse("2025-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var repository = new RepositoryOptions
        {
            Id = "gitlab/team/widgets",
            Name = "ignored/widgets",
            OwnerOrNamespace = "team/subteam",
            CloneUrl = "ssh://git@example/widgets.git",
            TargetBranch = "develop",
            StartDate = startDate,
            Settings = new RepositorySettingsOptions
            {
                WorkflowMode = ConfiguredWorkflowMode.PlanOnly,
                Git = new GitTransportOptions
                {
                    Mode = ConfiguredGitAuthenticationMode.Ssh,
                    SshPrivateKey = new SecretSource { Env = "SSH_KEY" },
                    SshUsername = "git",
                    SshTrust = new SshTrustOptions
                    {
                        Mode = ConfiguredSshHostVerificationMode.Pinned,
                        Fingerprints = ["sha256:host"],
                    },
                    Tls = new TlsTrustOptions { Mode = ConfiguredTlsTrustMode.None },
                },
            },
        };
        var options = CreateOptions() with
        {
            Providers = [CreateProvider() with { Kind = ProviderKind.GitLab, Repositories = [repository] }],
        };

        var resolved = EffectiveConfigurationResolver.Resolve(
            options,
            name => name == "SSH_KEY" ? "private-key" : "api-token",
            _ => throw new InvalidOperationException());
        var effective = Assert.Single(Assert.Single(resolved.Providers).Repositories);

        Assert.Equal("team/subteam", effective.OwnerOrNamespace);
        Assert.Equal("ssh://git@example/widgets.git", effective.CloneUrl);
        Assert.Equal("develop", effective.TargetBranch);
        Assert.Equal(startDate, effective.StartDate);
        Assert.Equal(ConfiguredWorkflowMode.PlanOnly, effective.WorkflowMode);
        Assert.Equal(ConfiguredGitAuthenticationMode.Ssh, effective.Git.Mode);
        Assert.Equal("private-key", effective.Git.SshPrivateKey);
        Assert.Equal(ConfiguredSshHostVerificationMode.Pinned, effective.Git.SshHostVerificationMode);
    }

    private static IssueAgentOptions CreateOptions() => new()
    {
        Workspace = new WorkspaceOptions { RootPath = "/data" },
        Omp = new OmpOptions
        {
            ExecutablePath = "/usr/local/bin/omp",
            Roles = new Dictionary<string, string> { ["planning"] = "plan", ["implementation"] = "task" },
        },
        Providers = [CreateProvider()],
    };

    private static ProviderOptions CreateProvider() => new()
    {
        Name = "github",
        Kind = ProviderKind.GitHub,
        BaseUri = new Uri("https://api.github.com/"),
        Token = new SecretSource { Env = "TOKEN" },
        IdentityOverride = "bot",
    };
}
