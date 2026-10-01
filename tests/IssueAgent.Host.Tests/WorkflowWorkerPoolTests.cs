using IssueAgent.Configuration;
using IssueAgent.Domain;
using IssueAgent.Providers;
using IssueAgent.Observability;
using IssueAgent.Omp;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace IssueAgent.Host.Tests;

public sealed class WorkflowWorkerPoolTests
{
    [Fact]
    public async Task HumanCommandRunsBeforeEarlierPlanningCandidate()
    {
        using var fixture = new PoolFixture(agentConcurrency: 1);
        var order = new List<long>();
        await fixture.Pool.AdmitAsync(
        [
            Candidate(1, WorkflowWorkPriority.NewPlanning, null, 1, _ => { order.Add(1); return Task.CompletedTask; }),
            Candidate(2, WorkflowWorkPriority.HumanCommand, WorkflowCommand.Implement, 2, _ => { order.Add(2); return Task.CompletedTask; }),
        ], CancellationToken.None);

        await fixture.StartAndDrainAsync();

        Assert.Equal([2L, 1L], order);
    }

    [Fact]
    public async Task FailedCandidateDoesNotBlockLaterCandidate()
    {
        using var fixture = new PoolFixture(agentConcurrency: 1);
        var completed = false;
        await fixture.Pool.AdmitAsync(
        [
            Candidate(1, WorkflowWorkPriority.NewPlanning, null, 1, _ => throw new InvalidOperationException("expected")),
            Candidate(2, WorkflowWorkPriority.NewPlanning, null, 2, _ => { completed = true; return Task.CompletedTask; }),
        ], CancellationToken.None);

        await fixture.StartAndDrainAsync();

        Assert.True(completed);
    }

    [Fact]
    public async Task RateLimitedCandidateRequeuesAfterDeferralWithoutOccupyingAgentCapacity()
    {
        using var fixture = new PoolFixture(agentConcurrency: 1);
        var laterCandidateRan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var retried = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        await fixture.Pool.AdmitAsync(
        [
            Candidate(1, WorkflowWorkPriority.NewPlanning, null, 1, _ =>
            {
                if (Interlocked.Increment(ref attempts) == 1)
                {
                    throw new PollingRateLimitedException(TimeSpan.FromMilliseconds(50));
                }

                retried.SetResult();
                return Task.CompletedTask;
            }),
            Candidate(2, WorkflowWorkPriority.NewPlanning, null, 2, _ =>
            {
                laterCandidateRan.SetResult();
                return Task.CompletedTask;
            }),
        ], CancellationToken.None);
        fixture.Start();

        await laterCandidateRan.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        await retried.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        Assert.True(await fixture.Pool.WaitForDrainAsync(TimeSpan.FromSeconds(2), CancellationToken.None));
        await fixture.StopAsync();
    }

    [Fact]
    public async Task RateLimitedCandidateRefreshesDurableClassificationBeforeRequeue()
    {
        using var fixture = new PoolFixture(agentConcurrency: 1);
        var key = new WorkflowWorkKey("github", "repo", 1);
        var resumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var refreshes = 0;
        var initialAttempts = 0;
        var candidate = new WorkflowCandidate(
            key,
            WorkflowCandidateKind.ExistingWorkflow,
            WorkflowWorkPriority.NewPlanning,
            null,
            1,
            _ =>
            {
                if (Interlocked.Increment(ref initialAttempts) == 1)
                {
                    throw new PollingRateLimitedException(TimeSpan.FromMilliseconds(20));
                }

                throw new InvalidOperationException("The stale candidate must not resume.");
            },
            _ =>
            {
                Interlocked.Increment(ref refreshes);
                return Task.FromResult<WorkflowCandidate?>(new WorkflowCandidate(
                    key,
                    WorkflowCandidateKind.ExistingWorkflow,
                    WorkflowWorkPriority.HumanCommand,
                    WorkflowCommand.Continue,
                    2,
                    _ =>
                    {
                        resumed.SetResult();
                        return Task.CompletedTask;
                    }));
            });
        await fixture.Pool.AdmitAsync([candidate], CancellationToken.None);
        fixture.Start();

        await resumed.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        Assert.Equal(1, Volatile.Read(ref refreshes));
        Assert.Equal(1, Volatile.Read(ref initialAttempts));
        Assert.True(await fixture.Pool.WaitForDrainAsync(TimeSpan.FromSeconds(2), CancellationToken.None));
        await fixture.StopAsync();
    }

    [Fact]
    public async Task RateLimitedRefreshReservesWorkflowKeyAgainstConcurrentPolling()
    {
        using var fixture = new PoolFixture(agentConcurrency: 1);
        var key = new WorkflowWorkKey("github", "repo", 1);
        var refreshStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRefresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        var duplicateExecutions = 0;
        var candidate = new WorkflowCandidate(
            key,
            WorkflowCandidateKind.ExistingWorkflow,
            WorkflowWorkPriority.NewPlanning,
            null,
            1,
            _ =>
            {
                if (Interlocked.Increment(ref attempts) == 1)
                {
                    throw new PollingRateLimitedException(TimeSpan.FromMilliseconds(20));
                }

                throw new InvalidOperationException("The stale candidate must not resume.");
            },
            async _ =>
            {
                refreshStarted.SetResult();
                await releaseRefresh.Task;
                return new WorkflowCandidate(
                    key,
                    WorkflowCandidateKind.ExistingWorkflow,
                    WorkflowWorkPriority.Reconciliation,
                    null,
                    2,
                    _ =>
                    {
                        resumed.SetResult();
                        return Task.CompletedTask;
                    });
            });
        await fixture.Pool.AdmitAsync([candidate], CancellationToken.None);
        fixture.Start();

        await refreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        await fixture.Pool.AdmitAsync(
            [Candidate(1, WorkflowWorkPriority.NewPlanning, null, 3, _ =>
            {
                Interlocked.Increment(ref duplicateExecutions);
                return Task.CompletedTask;
            })],
            CancellationToken.None);
        releaseRefresh.SetResult();

        await resumed.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        Assert.True(await fixture.Pool.WaitForDrainAsync(TimeSpan.FromSeconds(2), CancellationToken.None));
        Assert.Equal(0, Volatile.Read(ref duplicateExecutions));
        await fixture.StopAsync();
    }

    [Fact]
    public async Task AgentConcurrencyBoundsOnlyCandidateExecution()
    {
        using var fixture = new PoolFixture(agentConcurrency: 2);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var twoStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var current = 0;
        var maximum = 0;
        Task Execute(CancellationToken _)
        {
            var active = Interlocked.Increment(ref current);
            SetMaximum(ref maximum, active);
            if (active == 2) twoStarted.TrySetResult();
            return FinishAsync();

            async Task FinishAsync()
            {
                await release.Task;
                Interlocked.Decrement(ref current);
            }
        }
        await fixture.Pool.AdmitAsync(
        [
            Candidate(1, WorkflowWorkPriority.NewPlanning, null, 1, Execute),
            Candidate(2, WorkflowWorkPriority.NewPlanning, null, 2, Execute),
            Candidate(3, WorkflowWorkPriority.NewPlanning, null, 3, Execute),
        ], CancellationToken.None);
        fixture.Start();

        await twoStarted.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        Assert.Equal(2, Volatile.Read(ref maximum));
        Assert.Equal(1, fixture.Pool.QueuedCount);
        release.SetResult();
        Assert.True(await fixture.Pool.WaitForDrainAsync(TimeSpan.FromSeconds(2), CancellationToken.None));
        await fixture.StopAsync();
    }

    [Fact]
    public async Task ExplicitCancelInterruptsActiveSessionThenRunsDurableCancellationCandidate()
    {
        using var fixture = new PoolFixture(agentConcurrency: 1);
        var key = new WorkflowWorkKey("github", "repo", 1);
        var activeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationRan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var activeAttemptCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var omp = new BlockingOmpClient();
        async Task Active(CancellationToken cancellationToken)
        {
            await using var tracked = fixture.Registry.Track(key, omp);
            await tracked.ResumeSessionAsync("session-1", "/data/omp/session-1.jsonl", cancellationToken);
            activeStarted.SetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                activeAttemptCancelled.SetResult();
                throw;
            }
        }
        await fixture.Pool.AdmitAsync(
            [new WorkflowCandidate(key, WorkflowCandidateKind.ExistingWorkflow, WorkflowWorkPriority.HumanCommand, WorkflowCommand.Implement, 1, Active)],
            CancellationToken.None);
        fixture.Start();
        await activeStarted.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        await fixture.Pool.AdmitAsync(
            [new WorkflowCandidate(key, WorkflowCandidateKind.ExistingWorkflow, WorkflowWorkPriority.HumanCommand, WorkflowCommand.Cancel, 2, _ => { cancellationRan.SetResult(); return Task.CompletedTask; })],
            CancellationToken.None);

        await activeAttemptCancelled.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        await cancellationRan.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        Assert.Equal(["session-1"], omp.CancelledSessions);
        Assert.True(await fixture.Pool.WaitForDrainAsync(TimeSpan.FromSeconds(2), CancellationToken.None));
        await fixture.StopAsync();
    }

    [Fact]
    public async Task ExplicitCancelReplacesQueuedWorkForSameWorkflow()
    {
        using var fixture = new PoolFixture(agentConcurrency: 1);
        var blockingKey = new WorkflowWorkKey("github", "repo", 1);
        var queuedKey = new WorkflowWorkKey("github", "repo", 2);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executed = new List<string>();

        await fixture.Pool.AdmitAsync(
        [
            new WorkflowCandidate(blockingKey, WorkflowCandidateKind.ExistingWorkflow, WorkflowWorkPriority.HumanCommand, WorkflowCommand.Implement, 1, async _ =>
            {
                started.SetResult();
                await release.Task;
            }),
            new WorkflowCandidate(queuedKey, WorkflowCandidateKind.ExistingWorkflow, WorkflowWorkPriority.HumanCommand, WorkflowCommand.Implement, 2, _ =>
            {
                executed.Add("implementation");
                return Task.CompletedTask;
            }),
        ], CancellationToken.None);
        fixture.Start();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        await fixture.Pool.AdmitAsync(
            [new WorkflowCandidate(queuedKey, WorkflowCandidateKind.ExistingWorkflow, WorkflowWorkPriority.HumanCommand, WorkflowCommand.Cancel, 3, _ =>
            {
                executed.Add("cancel");
                return Task.CompletedTask;
            })],
            CancellationToken.None);
        release.SetResult();

        Assert.True(await fixture.Pool.WaitForDrainAsync(TimeSpan.FromSeconds(2), CancellationToken.None));
        await fixture.StopAsync();
        Assert.Equal(["cancel"], executed);
    }

    [Fact]
    public async Task StopAdmissionDiscardsQueuedWorkButAllowsInFlightGracefulCompletion()
    {
        using var fixture = new PoolFixture(agentConcurrency: 1);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await fixture.Pool.AdmitAsync(
        [
            Candidate(1, WorkflowWorkPriority.HumanCommand, WorkflowCommand.Implement, 1, async _ => { started.SetResult(); await release.Task; }),
            Candidate(2, WorkflowWorkPriority.NewPlanning, null, 2, _ => Task.CompletedTask),
        ], CancellationToken.None);
        fixture.Start();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        Assert.Equal(1, fixture.Pool.StopAdmission());
        Assert.False(await fixture.Pool.WaitForDrainAsync(TimeSpan.FromMilliseconds(25), CancellationToken.None));
        release.SetResult();
        Assert.True(await fixture.Pool.WaitForDrainAsync(TimeSpan.FromSeconds(2), CancellationToken.None));
        await fixture.StopAsync();
    }

    [Fact]
    public async Task ShutdownAllowsActiveAttemptToCompleteBeforeCancellingOmp()
    {
        using var fixture = new PoolFixture(agentConcurrency: 1);
        var key = new WorkflowWorkKey("github", "repo", 1);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var omp = new BlockingOmpClient();
        await fixture.Pool.AdmitAsync(
            [new WorkflowCandidate(key, WorkflowCandidateKind.ExistingWorkflow, WorkflowWorkPriority.HumanCommand, WorkflowCommand.Implement, 1, async cancellationToken =>
            {
                await using var tracked = fixture.Registry.Track(key, omp);
                await tracked.ResumeSessionAsync("session-1", "/data/omp/session-1.jsonl", cancellationToken);
                started.SetResult();
                await release.Task;
            })],
            CancellationToken.None);
        fixture.Start();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        fixture.Pool.StopAdmission();
        var coordinator = new WorkflowShutdownCoordinator(fixture.Pool, fixture.Registry);

        var shutdown = coordinator.DrainAndCancelAsync(
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1),
            fixture.WorkerCancellation);
        Assert.Empty(omp.CancelledSessions);
        release.SetResult();
        var result = await shutdown;

        Assert.True(result.Drained);
        Assert.Equal(0, result.CancelledSessions);
        Assert.Empty(omp.CancelledSessions);
        await fixture.StopAsync();
    }

    [Fact]
    public async Task ShutdownRequestsOmpCancellationOnlyAfterGraceExpires()
    {
        using var fixture = new PoolFixture(agentConcurrency: 1);
        var key = new WorkflowWorkKey("github", "repo", 1);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var omp = new BlockingOmpClient();
        await fixture.Pool.AdmitAsync(
            [new WorkflowCandidate(key, WorkflowCandidateKind.ExistingWorkflow, WorkflowWorkPriority.HumanCommand, WorkflowCommand.Implement, 1, async cancellationToken =>
            {
                await using var tracked = fixture.Registry.Track(key, omp);
                await tracked.ResumeSessionAsync("session-1", "/data/omp/session-1.jsonl", cancellationToken);
                started.SetResult();
                await omp.Cancelled.Task.WaitAsync(cancellationToken);
            })],
            CancellationToken.None);
        fixture.Start();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        fixture.Pool.StopAdmission();
        var coordinator = new WorkflowShutdownCoordinator(fixture.Pool, fixture.Registry);

        var result = await coordinator.DrainAndCancelAsync(
            TimeSpan.FromMilliseconds(30),
            TimeSpan.FromSeconds(1),
            fixture.WorkerCancellation);

        Assert.False(result.Drained);
        Assert.Equal(1, result.CancelledSessions);
        Assert.Equal(["session-1"], omp.CancelledSessions);
        await fixture.StopAsync();
    }

    [Fact]
    public async Task DeterministicDualProviderHarnessIsolatesFailureAndRunsRestartRetryOnce()
    {
        using var fixture = new PoolFixture(agentConcurrency: 2);
        var completed = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var githubKey = new WorkflowWorkKey("github", "github/octo/widgets", 1);
        var gitlabKey = new WorkflowWorkKey("gitlab", "gitlab/acme/widgets", 2);
        await fixture.Pool.AdmitAsync(
        [
            new WorkflowCandidate(
                githubKey,
                WorkflowCandidateKind.ExistingWorkflow,
                WorkflowWorkPriority.HumanCommand,
                WorkflowCommand.Implement,
                1,
                _ => throw new InvalidOperationException("injected provider boundary failure")),
            new WorkflowCandidate(
                gitlabKey,
                WorkflowCandidateKind.ExistingWorkflow,
                WorkflowWorkPriority.HumanCommand,
                WorkflowCommand.Implement,
                2,
                _ =>
                {
                    completed.Enqueue("gitlab");
                    return Task.CompletedTask;
                }),
        ], CancellationToken.None);
        fixture.Start();
        Assert.True(await fixture.Pool.WaitForDrainAsync(TimeSpan.FromSeconds(2), CancellationToken.None));

        await fixture.Pool.AdmitAsync(
            [new WorkflowCandidate(
                githubKey,
                WorkflowCandidateKind.ExistingWorkflow,
                WorkflowWorkPriority.HumanCommand,
                WorkflowCommand.Continue,
                3,
                _ =>
                {
                    completed.Enqueue("github-restart");
                    return Task.CompletedTask;
                })],
            CancellationToken.None);
        Assert.True(await fixture.Pool.WaitForDrainAsync(TimeSpan.FromSeconds(2), CancellationToken.None));
        await fixture.StopAsync();

        Assert.Equal(2, completed.Count);
        Assert.Contains("gitlab", completed);
        Assert.Contains("github-restart", completed);
    }

    private static WorkflowCandidate Candidate(
        long issue,
        WorkflowWorkPriority priority,
        WorkflowCommand? command,
        long sequence,
        Func<CancellationToken, Task> execute) =>
        new(new WorkflowWorkKey("github", "repo", issue), WorkflowCandidateKind.ExistingWorkflow, priority, command, sequence, execute);

    private static void SetMaximum(ref int maximum, int value)
    {
        var observed = Volatile.Read(ref maximum);
        while (value > observed)
        {
            var previous = Interlocked.CompareExchange(ref maximum, value, observed);
            if (previous == observed) return;
            observed = previous;
        }
    }

    private sealed class PoolFixture : IDisposable
    {
        public CancellationTokenSource WorkerCancellation { get; } = new();
        private readonly IssueAgentMetrics metrics = new();
        private Task? workers;

        public PoolFixture(int agentConcurrency)
        {
            Registry = new ActiveOmpSessionRegistry(NullLogger<ActiveOmpSessionRegistry>.Instance);
            var options = Options.Create(new IssueAgentOptions
            {
                Workspace = new WorkspaceOptions { RootPath = Path.GetTempPath() },
                Omp = new OmpOptions { ExecutablePath = "omp" },
                Concurrency = new ConcurrencyOptions { Agent = agentConcurrency, Polling = 1 },
            });
            Pool = new WorkflowWorkerPool(options, Registry, metrics, NullLogger<WorkflowWorkerPool>.Instance);
        }

        public ActiveOmpSessionRegistry Registry { get; }

        public WorkflowWorkerPool Pool { get; }

        public void Start() => workers = Pool.RunAsync(WorkerCancellation.Token);

        public async Task StartAndDrainAsync()
        {
            Start();
            Assert.True(await Pool.WaitForDrainAsync(TimeSpan.FromSeconds(2), CancellationToken.None));
            await StopAsync();
        }

        public async Task StopAsync()
        {
            WorkerCancellation.Cancel();
            if (workers is null) return;
            try
            {
                await workers;
            }
            catch (OperationCanceledException) when (WorkerCancellation.IsCancellationRequested)
            {
            }
        }

        public void Dispose()
        {
            WorkerCancellation.Cancel();
            Pool.Dispose();
            metrics.Dispose();
            WorkerCancellation.Dispose();
        }
    }

    private sealed class BlockingOmpClient : IOmpClient
    {
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<string> CancelledSessions { get; } = [];

        public ValueTask<OmpSession> CreateSessionAsync(string role, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new OmpSession("session-1", role));

        public ValueTask<OmpSession> ResumeSessionAsync(string sessionId, string sessionFile, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new OmpSession(sessionId, "task", sessionFile));

        public async IAsyncEnumerable<OmpEvent> RunAsync(OmpRunRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            yield break;
        }

        public ValueTask CancelAsync(string sessionId, CancellationToken cancellationToken)
        {
            CancelledSessions.Add(sessionId);
            Cancelled.TrySetResult();
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
