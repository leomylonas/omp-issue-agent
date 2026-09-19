using IssueAgent.Configuration;
using Microsoft.Extensions.Options;

namespace IssueAgent.Host;

/// <summary>Owns startup, periodic discovery, independently running workflow workers, and graceful
/// shutdown. Shutdown stops admission first, grants active attempts their grace period, then
/// cancels any attempt still running.</summary>
public sealed partial class Worker(
    IOptions<IssueAgentOptions> options,
    StartupValidator startupValidator,
    PollingScheduler pollingScheduler,
    WorkflowShutdownCoordinator shutdownCoordinator,
    ReadinessState readiness,
    ILogger<Worker> logger) : BackgroundService
{
    // Program configures HostOptions.ShutdownTimeout as grace plus this bounded cancellation
    // headroom. OMP cancellation must not consume the whole per-run timeout after grace expires.
    public static readonly TimeSpan ShutdownCancellationHeadroom = TimeSpan.FromSeconds(10);
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var configuration = options.Value;
        await startupValidator.ValidateAsync(stoppingToken).ConfigureAwait(false);
        using var workerCancellation = new CancellationTokenSource();
        var workers = pollingScheduler.RunWorkersAsync(workerCancellation.Token);
        readiness.MarkInitialized();
        LogInitialized(logger, configuration.Providers.Count, configuration.Concurrency.Agent, configuration.Concurrency.Polling);

        using var timer = new PeriodicTimer(configuration.PollInterval);
        try
        {
            await pollingScheduler.PollOnceAsync(stoppingToken).ConfigureAwait(false);
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                LogPollCycle(logger);
                await pollingScheduler.PollOnceAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            var shutdown = await shutdownCoordinator.DrainAndCancelAsync(
                configuration.ShutdownGracePeriod,
                ShutdownCancellationHeadroom,
                workerCancellation).ConfigureAwait(false);
            try
            {
                await workers.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (workerCancellation.IsCancellationRequested)
            {
            }
            LogShutdownComplete(logger, shutdown.Drained, shutdown.CancelledSessions);
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information,
        Message = "IssueAgent initialized with {ProviderCount} configured provider(s), agent concurrency {AgentConcurrency}, and polling concurrency {PollingConcurrency}")]
    private static partial void LogInitialized(ILogger logger, int providerCount, int agentConcurrency, int pollingConcurrency);

    [LoggerMessage(EventId = 2, Level = LogLevel.Debug, Message = "IssueAgent poll cycle started")]
    private static partial void LogPollCycle(ILogger logger);

    [LoggerMessage(EventId = 3, Level = LogLevel.Information,
        Message = "IssueAgent stopping; discarded {DiscardedQueuedWork} queued attempt(s) and active work has up to {ShutdownGracePeriod} to finish")]
    private static partial void LogShutdown(ILogger logger, TimeSpan shutdownGracePeriod, int discardedQueuedWork);

    [LoggerMessage(EventId = 4, Level = LogLevel.Information,
        Message = "IssueAgent workflow workers stopped; graceful drain completed: {Drained}; OMP cancellation requested for {CancelledSessions} session(s)")]
    private static partial void LogShutdownComplete(ILogger logger, bool drained, int cancelledSessions);
}
