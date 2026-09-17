using Microsoft.Extensions.Options;
using IssueAgent.Configuration;

namespace IssueAgent.Host;

public sealed partial class Worker(
    IOptions<IssueAgentOptions> options,
    ReadinessState readiness,
    ILogger<Worker> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var configuration = options.Value;
        readiness.MarkInitialized();
        LogInitialized(
            logger,
            configuration.Providers.Count,
            configuration.Concurrency.Agent,
            configuration.Concurrency.Polling);
        return Task.CompletedTask;
    }

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Information,
        Message = "IssueAgent initialized with {ProviderCount} configured provider(s), agent concurrency {AgentConcurrency}, and polling concurrency {PollingConcurrency}")]
    private static partial void LogInitialized(
        ILogger logger,
        int providerCount,
        int agentConcurrency,
        int pollingConcurrency);
}
