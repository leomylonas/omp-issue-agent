using IssueAgent.Omp;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IssueAgent.Host.Tests;

public sealed class ActiveOmpSessionRegistryTests
{
    private static readonly WorkflowWorkKey Key = new("github", "octo/widgets", 7);

    [Fact]
    public async Task RegisteredSessionCanBeCancelledAndIsRemovedOnDispose()
    {
        var registry = new ActiveOmpSessionRegistry(NullLogger<ActiveOmpSessionRegistry>.Instance);
        var inner = new RecordingOmpClient("session-7");
        var tracked = registry.Track(Key, inner);
        await tracked.CreateSessionAsync("task", CancellationToken.None);

        Assert.Equal(1, registry.Count);
        Assert.True(await registry.TryCancelAsync(Key, CancellationToken.None));
        Assert.Equal(["session-7"], inner.CancelledSessions);

        await tracked.DisposeAsync();
        Assert.Equal(0, registry.Count);
        Assert.False(await registry.TryCancelAsync(Key, CancellationToken.None));
    }

    [Fact]
    public async Task DisposingSupersededOwnerDoesNotRemoveCurrentAttempt()
    {
        var registry = new ActiveOmpSessionRegistry(NullLogger<ActiveOmpSessionRegistry>.Instance);
        var first = registry.Track(Key, new RecordingOmpClient("first"));
        await first.CreateSessionAsync("task", CancellationToken.None);
        var currentInner = new RecordingOmpClient("current");
        var current = registry.Track(Key, currentInner);
        await current.ResumeSessionAsync("current", "/data/omp/current.jsonl", CancellationToken.None);

        await first.DisposeAsync();
        Assert.Equal(1, registry.Count);
        Assert.True(await registry.TryCancelAsync(Key, CancellationToken.None));
        Assert.Equal(["current"], currentInner.CancelledSessions);

        await current.DisposeAsync();
    }

    [Fact]
    public async Task CancelAllRequestsCancellationForEveryActiveSession()
    {
        var registry = new ActiveOmpSessionRegistry(NullLogger<ActiveOmpSessionRegistry>.Instance);
        var firstInner = new RecordingOmpClient("first");
        var secondInner = new RecordingOmpClient("second");
        var first = registry.Track(Key, firstInner);
        var second = registry.Track(Key with { IssueNumber = 8 }, secondInner);
        await first.CreateSessionAsync("task", CancellationToken.None);
        await second.CreateSessionAsync("task", CancellationToken.None);

        Assert.Equal(2, await registry.CancelAllAsync(CancellationToken.None));
        Assert.Equal(["first"], firstInner.CancelledSessions);
        Assert.Equal(["second"], secondInner.CancelledSessions);

        await first.DisposeAsync();
        await second.DisposeAsync();
    }

    private sealed class RecordingOmpClient(string sessionId) : IOmpClient
    {
        public List<string> CancelledSessions { get; } = [];

        public ValueTask<OmpSession> CreateSessionAsync(string role, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new OmpSession(sessionId, role));

        public ValueTask<OmpSession> ResumeSessionAsync(string requestedSessionId, string sessionFile, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new OmpSession(requestedSessionId, "task", sessionFile));

        public async IAsyncEnumerable<OmpEvent> RunAsync(OmpRunRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            yield break;
        }

        public ValueTask CancelAsync(string cancelledSessionId, CancellationToken cancellationToken)
        {
            CancelledSessions.Add(cancelledSessionId);
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
