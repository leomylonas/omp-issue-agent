using Xunit;

namespace IssueAgent.Security.Tests;

/// <summary>Checks deployment artifacts for the security boundaries that cannot be exercised by the
/// .NET unit tests (specification §§32–35).</summary>
public sealed class DeploymentArtifactSecurityTests
{
    [Fact]
    public void HelmWorkloadsUseFileBasedOmpConfigAndWritableNativeState()
    {
        var deployment = ReadRepositoryFile("deploy/helm/issue-agent/templates/deployment.yaml");
        var broker = ReadRepositoryFile("deploy/helm/issue-agent/templates/auth-broker.yaml");
        var values = ReadRepositoryFile("deploy/helm/issue-agent/values.yaml");

        Assert.Contains("automountServiceAccountToken: false", deployment, StringComparison.Ordinal);
        Assert.Contains("PI_CONFIG_FILES", deployment, StringComparison.Ordinal);
        Assert.Contains("omp.config.file", deployment, StringComparison.Ordinal);
        Assert.DoesNotContain("OMP_CONFIG_DIR", deployment, StringComparison.Ordinal);
        Assert.Contains("mountPath: {{ .Values.omp.config.mountPath }}", deployment, StringComparison.Ordinal);
        Assert.Contains("readOnly: true", deployment, StringComparison.Ordinal);
        Assert.Contains("HOME", deployment, StringComparison.Ordinal);
        Assert.Contains("PI_CODING_AGENT_DIR", deployment, StringComparison.Ordinal);
        Assert.Contains("fsGroup: 10001", deployment, StringComparison.Ordinal);
        Assert.Contains("fsGroup: 10001", broker, StringComparison.Ordinal);
        Assert.Contains("command: [\"/usr/local/bin/omp\"]", broker, StringComparison.Ordinal);
        Assert.Contains("auth-broker\", \"serve\", \"--bind=0.0.0.0:8081", broker, StringComparison.Ordinal);
        Assert.Contains("path: /v1/healthz", broker, StringComparison.Ordinal);
        Assert.Contains("strategy:", broker, StringComparison.Ordinal);
        Assert.Contains("type: Recreate", broker, StringComparison.Ordinal);
        Assert.Contains("checksum/secret", broker, StringComparison.Ordinal);

        Assert.Contains("default .Values.image.repository .Values.omp.authBroker.image.repository", broker, StringComparison.Ordinal);
        Assert.Contains("repository: \"\"", values, StringComparison.Ordinal);
        Assert.Contains("provision-bearer-token", broker, StringComparison.Ordinal);
        Assert.Contains("auth-broker.token", broker, StringComparison.Ordinal);
        Assert.Contains("OMP_AUTH_BROKER_TOKEN", values, StringComparison.Ordinal);
        Assert.Contains("IssueAgent__Omp__ExecutionSecrets__OMP_AUTH_BROKER_TOKEN__File", deployment, StringComparison.Ordinal);
        Assert.Contains("repository: ghcr.io/leomylonas/omp-issue-agent", values, StringComparison.Ordinal);

    }

    [Fact]
    public void HelmChartUsesReleaseImageAndExplicitExternalResourceChecksums()
    {
        var values = ReadRepositoryFile("deploy/helm/issue-agent/values.yaml");
        var deployment = ReadRepositoryFile("deploy/helm/issue-agent/templates/deployment.yaml");
        var configMap = ReadRepositoryFile("deploy/helm/issue-agent/templates/configmap.yaml");

        Assert.DoesNotContain("ghcr.io/example/issue-agent", values, StringComparison.Ordinal);
        Assert.Contains("ghcr.io/leomylonas/omp-issue-agent", values, StringComparison.Ordinal);
        Assert.Contains("existingConfigMapChecksum", values, StringComparison.Ordinal);
        Assert.Contains("existingSecretChecksum", values, StringComparison.Ordinal);
        Assert.Contains("issue-agent.omp-config.data", configMap, StringComparison.Ordinal);
        Assert.Contains("required when the existing", deployment, StringComparison.Ordinal);
    }

    [Fact]
    public void PlainDockerExamplePersistsDataAndUsesSupportedOmpConfigSetting()
    {
        var readme = ReadRepositoryFile("deploy/README.md");
        var environment = ReadRepositoryFile("deploy/issue-agent.env");

        Assert.Contains("--read-only", readme, StringComparison.Ordinal);
        Assert.Contains("--cap-drop=ALL", readme, StringComparison.Ordinal);
        Assert.Contains("--cap-add=CHOWN", readme, StringComparison.Ordinal);
        Assert.Contains("--cap-add=FOWNER", readme, StringComparison.Ordinal);
        Assert.Contains("--cap-add=DAC_OVERRIDE", readme, StringComparison.Ordinal);
        Assert.Contains("--cap-add=SETGID", readme, StringComparison.Ordinal);
        Assert.Contains("--cap-add=SETPCAP", readme, StringComparison.Ordinal);
        Assert.Contains("--cap-add=SETUID", readme, StringComparison.Ordinal);
        Assert.Contains("-v issue-agent-data:/data", readme, StringComparison.Ordinal);
        Assert.Contains("-e IssueAgent__Omp__ExecutablePath=/usr/local/bin/omp-unprivileged", readme, StringComparison.Ordinal);
        Assert.Contains("-e PI_CONFIG_FILES=/etc/omp/config.yml", readme, StringComparison.Ordinal);
        Assert.DoesNotContain("OMP_CONFIG_DIR", readme, StringComparison.Ordinal);
        Assert.DoesNotContain("OMP_CONFIG_DIR", environment, StringComparison.Ordinal);
    }

    [Fact]
    public void ComposeKeepsBrokerSecretOutOfTheBrokerlessConfiguration()
    {
        var compose = ReadRepositoryFile("deploy/docker-compose.yml");
        var brokerOverlay = ReadRepositoryFile("deploy/docker-compose.auth-broker.yml");

        Assert.DoesNotContain("IssueAgent__Omp__AuthBrokerUrl", compose, StringComparison.Ordinal);
        Assert.DoesNotContain("omp_auth_broker_token", compose, StringComparison.Ordinal);
        Assert.Contains("restart: unless-stopped", compose, StringComparison.Ordinal);
        Assert.Contains("IssueAgent__Omp__AuthBrokerUrl: http://omp-auth-broker:8081", brokerOverlay, StringComparison.Ordinal);
        Assert.Contains("IssueAgent__Omp__ExecutionSecrets__OMP_AUTH_BROKER_TOKEN__File: /run/issue-agent-secrets/omp_auth_broker_token", brokerOverlay, StringComparison.Ordinal);
        Assert.Contains("target: /run/secrets-source/omp_auth_broker_token", brokerOverlay, StringComparison.Ordinal);
        Assert.Contains("mode: 0400", brokerOverlay, StringComparison.Ordinal);
        Assert.Contains("omp_auth_broker_token:", brokerOverlay, StringComparison.Ordinal);
        Assert.DoesNotContain("OMP_AUTH_BROKER_TOKEN:", brokerOverlay, StringComparison.Ordinal);
        Assert.Contains("PI_CONFIG_FILES: /etc/omp/config.yml", compose, StringComparison.Ordinal);
        Assert.Contains("entrypoint: [\"/bin/sh\", \"-ec\"]", brokerOverlay, StringComparison.Ordinal);
        Assert.DoesNotContain("OMP_BROKER_LISTEN", brokerOverlay, StringComparison.Ordinal);
        Assert.DoesNotContain("OMP_CONFIG_DIR", brokerOverlay, StringComparison.Ordinal);
        Assert.Contains("./omp:/etc/omp:ro", compose, StringComparison.Ordinal);
    }

    [Fact]
    public void BrokerEnabledHelmCiUsesATrackedDummyTokenFixture()
    {
        var ci = ReadRepositoryFile(".github/workflows/ci.yml");
        var brokerFixture = ReadRepositoryFile(".github/fixtures/omp-auth-broker-token");
        var providerFixture = ReadRepositoryFile(".github/fixtures/github-token");
        Assert.Contains("docker-compose.auth-broker.yml config", ci, StringComparison.Ordinal);

        Assert.Contains("--set-file secret.stringData.OMP_AUTH_BROKER_TOKEN=.github/fixtures/omp-auth-broker-token", ci, StringComparison.Ordinal);
        Assert.Contains("Install pinned OMP smoke binary", ci, StringComparison.Ordinal);
        Assert.Contains("OMP_TEST_BINARY: /tmp/omp", ci, StringComparison.Ordinal);
        Assert.Contains("61b4cd50ceaea70baccae7b52a22034469130ea2985a0b2e9adc0f7b3a77a85f", ci, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(brokerFixture));
        Assert.False(string.IsNullOrWhiteSpace(providerFixture));
        Assert.Contains("install --mode=0600 .github/fixtures/github-token deploy/secrets/github-token", ci, StringComparison.Ordinal);

    }

    [Fact]
    public void BrokerInstructionsMigrateLocalOAuthStateAndProtectTheCallback()
    {
        var readme = ReadRepositoryFile("deploy/README.md");

        Assert.Contains("omp auth-broker login", readme, StringComparison.Ordinal);
        Assert.Contains("omp auth-broker migrate --from-local --include-oauth", readme, StringComparison.Ordinal);
        Assert.Contains("callback", readme, StringComparison.Ordinal);
        Assert.DoesNotContain("\nomp login\n", readme, StringComparison.Ordinal);
        Assert.DoesNotContain("interactive setup endpoint", readme, StringComparison.Ordinal);
    }
    [Fact]
    public void ProductionImageInitializesFreshStateAndDropsAmbientCapabilitiesBeforeOmpExecutes()
    {
        var dockerfile = ReadRepositoryFile("Dockerfile");
        var entrypoint = ReadRepositoryFile("docker/issue-agent-entrypoint.sh");
        var ompWrapper = ReadRepositoryFile("docker/omp-unprivileged.sh");

        Assert.Contains("ENV HOME=/data", dockerfile, StringComparison.Ordinal);
        Assert.Contains("PI_CODING_AGENT_DIR=/data/omp/agent", dockerfile, StringComparison.Ordinal);
        Assert.Contains("PI_CODING_AGENT_SESSION_DIR=/data/omp", dockerfile, StringComparison.Ordinal);
        Assert.Contains("mkdir --parents /data/omp/agent", dockerfile, StringComparison.Ordinal);
        Assert.Contains("chmod 2770 /data /data/omp /data/omp/agent", dockerfile, StringComparison.Ordinal);
        Assert.Contains("umask 0077", entrypoint, StringComparison.Ordinal);
        Assert.Contains("if [ \"$(id -u)\" -eq 0 ]; then", entrypoint, StringComparison.Ordinal);
        Assert.Contains("mkdir -p /data/omp/agent", entrypoint, StringComparison.Ordinal);
        Assert.Contains("--reuid=10001 --regid=10001", entrypoint, StringComparison.Ordinal);
        Assert.Contains("--reuid=10002 --regid=10001", ompWrapper, StringComparison.Ordinal);
        Assert.DoesNotContain("--ambient-caps +setuid,+setgid", ompWrapper, StringComparison.Ordinal);
        Assert.Contains("--ambient-caps -setuid,-setgid", ompWrapper, StringComparison.Ordinal);
    }

    [Fact]
    public void ProviderSecretSourceIsCopiedBeforeTheNonRootHostStartsAndOmpCannotReadIt()
    {
        var dockerfile = ReadRepositoryFile("Dockerfile");
        var ompWrapper = ReadRepositoryFile("docker/omp-unprivileged.sh");
        var entrypoint = ReadRepositoryFile("docker/issue-agent-entrypoint.sh");
        var compose = ReadRepositoryFile("deploy/docker-compose.yml");
        var brokerOverlay = ReadRepositoryFile("deploy/docker-compose.auth-broker.yml");
        var deployment = ReadRepositoryFile("deploy/helm/issue-agent/templates/deployment.yaml");

        Assert.Contains("useradd --create-home --uid 10002 omp", dockerfile, StringComparison.Ordinal);
        Assert.Contains("--reuid=10002 --regid=10001", ompWrapper, StringComparison.Ordinal);
        Assert.Contains("umask 0002", ompWrapper, StringComparison.Ordinal);
        Assert.Contains("install --owner=10001 --group=10001 --mode=0400", entrypoint, StringComparison.Ordinal);
        Assert.Contains("FOWNER", compose, StringComparison.Ordinal);
        Assert.Contains("/run/secrets-source", compose, StringComparison.Ordinal);
        Assert.Contains("install -o 10001 -g 10001 -Dm 600 /run/secrets/omp_auth_broker_token /data/.omp/auth-broker.token", brokerOverlay, StringComparison.Ordinal);
        Assert.Contains("--reuid=10001 --regid=10001", brokerOverlay, StringComparison.Ordinal);
        Assert.Contains("DAC_OVERRIDE", brokerOverlay, StringComparison.Ordinal);
        var mainContainer = deployment[deployment.IndexOf("      containers:", StringComparison.Ordinal)..deployment.IndexOf("      volumes:", StringComparison.Ordinal)];
        Assert.Contains("copy-issue-agent-secrets", deployment, StringComparison.Ordinal);
        Assert.Contains("runAsNonRoot: true", deployment, StringComparison.Ordinal);
        Assert.Contains("runAsUser: 10001", deployment, StringComparison.Ordinal);
        Assert.Contains("runAsUser: 0", deployment, StringComparison.Ordinal);
        Assert.Contains("FOWNER", deployment, StringComparison.Ordinal);
        var mainInitContainer = deployment[deployment.IndexOf("copy-issue-agent-secrets", StringComparison.Ordinal)..deployment.IndexOf("      containers:", StringComparison.Ordinal)];
        Assert.Contains("add: [CHOWN, FOWNER, DAC_OVERRIDE]", mainInitContainer, StringComparison.Ordinal);
        Assert.DoesNotContain("issue-agent-secret-source", mainContainer, StringComparison.Ordinal);
        Assert.Contains("defaultMode: 0400", deployment, StringComparison.Ordinal);
    }

    [Fact]
    public void HelmMainSelectorRemainsUpgradeSafeAndItsWorkspaceRolloutIsPvcSafe()
    {
        var deployment = ReadRepositoryFile("deploy/helm/issue-agent/templates/deployment.yaml");
        var service = ReadRepositoryFile("deploy/helm/issue-agent/templates/service.yaml");
        var broker = ReadRepositoryFile("deploy/helm/issue-agent/templates/auth-broker.yaml");
        var mainSelector = deployment[..deployment.IndexOf("  template:", StringComparison.Ordinal)];
        var brokerSelector = broker[broker.IndexOf("  selector:", StringComparison.Ordinal)..broker.IndexOf("  template:", StringComparison.Ordinal)];

        Assert.DoesNotContain("app.kubernetes.io/component", mainSelector, StringComparison.Ordinal);
        Assert.Contains("app.kubernetes.io/component: auth-broker", brokerSelector, StringComparison.Ordinal);
        Assert.Contains("type: Recreate", deployment, StringComparison.Ordinal);
        Assert.Contains("app.kubernetes.io/component: issue-agent", deployment, StringComparison.Ordinal);
        Assert.Contains("app.kubernetes.io/component: issue-agent", service, StringComparison.Ordinal);
        Assert.Contains("app.kubernetes.io/component: auth-broker", broker, StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseAndMonitoringArtifactsCoverValidVersionsAndMissingSeries()
    {
        var ci = ReadRepositoryFile(".github/workflows/ci.yml");
        var release = ReadRepositoryFile(".github/workflows/release.yml");
        var resources = ReadRepositoryFile("deploy/helm/issue-agent/templates/resources.yaml");

        Assert.Contains("v(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)", release, StringComparison.Ordinal);
        Assert.Contains("absent(up{", resources, StringComparison.Ordinal);
        Assert.Contains("absent(issueagent_poll_count_total", resources, StringComparison.Ordinal);
        Assert.Contains("cancel-in-progress: true", ci, StringComparison.Ordinal);
        Assert.Contains("issueagent_poll_errors_total", resources, StringComparison.Ordinal);
    }

    [Fact]
    public void OptionalComposeOverlaysMountOnlyTheirRequiredSecrets()
    {
        var notifications = ReadRepositoryFile("deploy/docker-compose.notifications.yml");
        var broker = ReadRepositoryFile("deploy/docker-compose.auth-broker.yml");

        Assert.Contains("telegram_bot_token", notifications, StringComparison.Ordinal);
        Assert.Contains("slack_webhook_url", notifications, StringComparison.Ordinal);
        Assert.DoesNotContain("telegram_bot_token", broker, StringComparison.Ordinal);
        Assert.DoesNotContain("slack_webhook_url", broker, StringComparison.Ordinal);
    }

    [Fact]
    public void BrokerPortForwardIsScopedToTheHelmRelease()
    {
        var readme = ReadRepositoryFile("deploy/README.md");

        Assert.Contains("app.kubernetes.io/instance=\"$RELEASE\"", readme, StringComparison.Ordinal);
    }

    private static string ReadRepositoryFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AGENTS.md")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine(directory!.FullName, relativePath));
    }
}
