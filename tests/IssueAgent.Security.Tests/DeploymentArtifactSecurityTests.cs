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
        Assert.Contains("--tmpfs /tmp", readme, StringComparison.Ordinal);
        Assert.Contains("-v issue-agent-data:/data", readme, StringComparison.Ordinal);
        Assert.Contains("-e PI_CONFIG_FILES=/etc/omp/config.yml", readme, StringComparison.Ordinal);
        Assert.Contains("PI_CONFIG_FILES=/etc/omp/config.yml", environment, StringComparison.Ordinal);
        Assert.DoesNotContain("OMP_CONFIG_DIR", readme, StringComparison.Ordinal);
        Assert.DoesNotContain("OMP_CONFIG_DIR", environment, StringComparison.Ordinal);
    }

    [Fact]
    public void ComposeSharesBrokerTokenAsASecretAndKeepsOmpConfigReadOnly()
    {
        var compose = ReadRepositoryFile("deploy/docker-compose.yml");

        Assert.Contains("IssueAgent__Omp__AuthBrokerUrl: ${ISSUE_AGENT_OMP_AUTH_BROKER_URL-http://omp-auth-broker:8081}", compose, StringComparison.Ordinal);
        Assert.Contains("IssueAgent__Omp__ExecutionSecrets__OMP_AUTH_BROKER_TOKEN__File: /run/secrets/omp_auth_broker_token", compose, StringComparison.Ordinal);
        Assert.Contains("install -Dm 600 /run/secrets/omp_auth_broker_token /data/.omp/auth-broker.token", compose, StringComparison.Ordinal);
        Assert.Contains("omp_auth_broker_token:", compose, StringComparison.Ordinal);
        Assert.DoesNotContain("OMP_AUTH_BROKER_TOKEN:", compose, StringComparison.Ordinal);
        Assert.Contains("PI_CONFIG_FILES: /etc/omp/config.yml", compose, StringComparison.Ordinal);
        Assert.Contains("entrypoint: [\"/bin/sh\", \"-ec\"]", compose, StringComparison.Ordinal);
        Assert.DoesNotContain("OMP_BROKER_LISTEN", compose, StringComparison.Ordinal);
        Assert.DoesNotContain("OMP_CONFIG_DIR", compose, StringComparison.Ordinal);
        Assert.Contains("./omp:/etc/omp:ro", compose, StringComparison.Ordinal);
    }

    [Fact]
    public void BrokerInstructionsUseCliLoginAndProtectTheCallback()
    {
        var readme = ReadRepositoryFile("deploy/README.md");

        Assert.Contains("omp auth-broker login", readme, StringComparison.Ordinal);
        Assert.Contains("omp auth-broker token --regenerate", readme, StringComparison.Ordinal);
        Assert.Contains("callback", readme, StringComparison.Ordinal);
        Assert.DoesNotContain("interactive setup endpoint", readme, StringComparison.Ordinal);
    }
    [Fact]
    public void ProductionImageRedirectsNativeOmpStateToPersistentData()
    {
        var dockerfile = ReadRepositoryFile("Dockerfile");

        Assert.Contains("USER issueagent", dockerfile, StringComparison.Ordinal);
        Assert.Contains("ENV HOME=/data", dockerfile, StringComparison.Ordinal);
        Assert.Contains("PI_CODING_AGENT_DIR=/data/omp/agent", dockerfile, StringComparison.Ordinal);
        Assert.Contains("PI_CODING_AGENT_SESSION_DIR=/data/omp", dockerfile, StringComparison.Ordinal);
        Assert.Contains("mkdir --parents /data", dockerfile, StringComparison.Ordinal);
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
