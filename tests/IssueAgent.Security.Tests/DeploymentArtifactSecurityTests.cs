using Xunit;

namespace IssueAgent.Security.Tests;

/// <summary>Checks deployment artifacts for the security boundaries that cannot be exercised by the
/// .NET unit tests (specification §§32–35).</summary>
public sealed class DeploymentArtifactSecurityTests
{
    [Fact]
    public void HelmWorkloadsDisableServiceAccountTokenAutomountAndMountOmpConfigReadOnly()
    {
        var deployment = ReadRepositoryFile("deploy/helm/issue-agent/templates/deployment.yaml");
        var broker = ReadRepositoryFile("deploy/helm/issue-agent/templates/auth-broker.yaml");

        Assert.Contains("automountServiceAccountToken: false", deployment, StringComparison.Ordinal);
        Assert.Contains("PI_CONFIG_FILES", deployment, StringComparison.Ordinal);
        Assert.DoesNotContain("OMP_CONFIG_DIR", deployment, StringComparison.Ordinal);
        Assert.Contains("mountPath: {{ .Values.omp.config.mountPath }}", deployment, StringComparison.Ordinal);
        Assert.Contains("readOnly: true", deployment, StringComparison.Ordinal);
        Assert.Contains("automountServiceAccountToken: false", broker, StringComparison.Ordinal);
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
        Assert.Contains("-e PI_CONFIG_FILES=/etc/omp", readme, StringComparison.Ordinal);
        Assert.Contains("PI_CONFIG_FILES=/etc/omp", environment, StringComparison.Ordinal);
        Assert.DoesNotContain("OMP_CONFIG_DIR", readme, StringComparison.Ordinal);
        Assert.DoesNotContain("OMP_CONFIG_DIR", environment, StringComparison.Ordinal);
    }

    [Fact]
    public void ComposeExposesBrokerSetupOnlyOnLoopbackAndKeepsOmpConfigReadOnly()
    {
        var compose = ReadRepositoryFile("deploy/docker-compose.yml");

        Assert.Contains("127.0.0.1:${OMP_AUTH_BROKER_PORT:-8081}:8081", compose, StringComparison.Ordinal);
        Assert.Contains("PI_CONFIG_FILES: /etc/omp", compose, StringComparison.Ordinal);
        Assert.DoesNotContain("OMP_CONFIG_DIR", compose, StringComparison.Ordinal);
        Assert.Contains("./omp:/etc/omp:ro", compose, StringComparison.Ordinal);
    }

    [Fact]
    public void CiInstallsGitLfsFromPinnedVerifiedReleaseArtifact()
    {
        var workflow = ReadRepositoryFile(".github/workflows/ci.yml");

        Assert.Contains("GIT_LFS_VERSION: 3.8.0", workflow, StringComparison.Ordinal);
        Assert.Contains("GIT_LFS_SHA256:", workflow, StringComparison.Ordinal);
        Assert.Contains("sha256sum --check --strict", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("apt-get install -y --no-install-recommends git-lfs", workflow, StringComparison.Ordinal);
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
