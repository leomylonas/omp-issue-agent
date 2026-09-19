using IssueAgent.Omp;

namespace IssueAgent.Omp.Tests;

public sealed class OmpEnvironmentTests
{
    [Fact]
    public void BuildAllowsSupportedConfigPathButNotLegacyConfigVariable()
    {
        var ambient = new Dictionary<string, string?>
        {
            ["PI_CONFIG_FILES"] = "/etc/omp/config.yml",
            ["OMP_CONFIG_DIR"] = "/etc/omp",
        };

        var result = OmpEnvironment.Build(ambient, new Dictionary<string, string>(), new Dictionary<string, string>());

        Assert.Equal("/etc/omp/config.yml", result["PI_CONFIG_FILES"]);
        Assert.DoesNotContain("OMP_CONFIG_DIR", result.Keys);
    }

    [Fact]
    public void BuildIncludesOnlyAllowListedAmbientVariables()
    {
        var ambient = new Dictionary<string, string?>
        {
            ["PATH"] = "/usr/bin",
            ["HOME"] = "/home/agent",
            ["LANG"] = "en_US.UTF-8",
            ["LC_ALL"] = "en_US.UTF-8",
            ["HTTP_PROXY"] = "http://proxy.example:8080",
            ["GITHUB_TOKEN"] = "super-secret-provider-token",
            ["KUBECONFIG"] = "/root/.kube/config",
            ["AWS_SECRET_ACCESS_KEY"] = "also-secret",
        };

        var result = OmpEnvironment.Build(ambient, new Dictionary<string, string>(), new Dictionary<string, string>());

        Assert.Equal("/usr/bin", result["PATH"]);
        Assert.Equal("/home/agent", result["HOME"]);
        Assert.Equal("en_US.UTF-8", result["LANG"]);
        Assert.Equal("en_US.UTF-8", result["LC_ALL"]);
        Assert.Equal("http://proxy.example:8080", result["HTTP_PROXY"]);
        Assert.DoesNotContain("GITHUB_TOKEN", result.Keys);
        Assert.DoesNotContain("KUBECONFIG", result.Keys);
        Assert.DoesNotContain("AWS_SECRET_ACCESS_KEY", result.Keys);
    }

    [Fact]
    public void BuildIncludesOmpConnectionSettingsAndExecutionVariables()
    {
        var ompSettings = new Dictionary<string, string> { ["OMP_AUTH_BROKER_URL"] = "http://broker:9000" };
        var executionVariables = new Dictionary<string, string> { ["CUSTOM_BUILD_FLAG"] = "1" };

        var result = OmpEnvironment.Build(new Dictionary<string, string?>(), ompSettings, executionVariables);

        Assert.Equal("http://broker:9000", result["OMP_AUTH_BROKER_URL"]);
        Assert.Equal("1", result["CUSTOM_BUILD_FLAG"]);
    }

    [Fact]
    public void BuildLetsExecutionVariablesOverrideAmbientValues()
    {
        var ambient = new Dictionary<string, string?> { ["HTTP_PROXY"] = "http://ambient-proxy:8080" };
        var executionVariables = new Dictionary<string, string> { ["HTTP_PROXY"] = "http://explicit-proxy:9090" };

        var result = OmpEnvironment.Build(ambient, new Dictionary<string, string>(), executionVariables);

        Assert.Equal("http://explicit-proxy:9090", result["HTTP_PROXY"]);
    }
}
