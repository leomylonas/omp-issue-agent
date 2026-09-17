using System.Runtime.CompilerServices;

namespace IssueAgent.Omp;

/// <summary>
/// Deterministic in-memory <see cref="IOmpClient"/> for workflow tests. Test authors enqueue the
/// session ids and event sequences OMP should "return" for each call, then assert against the
/// recorded call log. No process, network, or paid model usage.
/// </summary>
public sealed class FakeOmpClient : IOmpClient
{
    private readonly Queue<string> sessionIdsToCreate = new();
    private readonly Queue<(Action? OnStart, IReadOnlyList<OmpEvent> Events)> scriptedRuns = new();

    public List<string> CreatedRoles { get; } = [];

    public List<string> ResumedSessionIds { get; } = [];

    public List<OmpRunRequest> RunRequests { get; } = [];

    public List<string> CancelledSessionIds { get; } = [];

    public bool Disposed { get; private set; }

    /// <summary>Queues the session id returned by the next <see cref="CreateSessionAsync"/> call.
    /// If the queue is empty when called, a new <see cref="Guid"/>-based id is generated.</summary>
    public FakeOmpClient EnqueueSessionId(string sessionId)
    {
        sessionIdsToCreate.Enqueue(sessionId);
        return this;
    }

    /// <summary>Queues the event sequence returned by the next <see cref="RunAsync"/> call. Must end
    /// with an <see cref="OmpCompletedEvent"/> or <see cref="OmpErrorEvent"/>, matching the real
    /// contract.</summary>
    public FakeOmpClient EnqueueRun(params OmpEvent[] events) => EnqueueRun(onStart: null, events);

    /// <summary>Same as <see cref="EnqueueRun(OmpEvent[])"/>, but invokes <paramref name="onStart"/>
    /// synchronously when this scripted run begins, before any event is yielded. Use this to inject
    /// state changes (for example, a new human comment arriving) that must happen strictly between
    /// the previous run and this one.</summary>
    public FakeOmpClient EnqueueRun(Action? onStart, params OmpEvent[] events)
    {
        if (events.Length == 0 || events[^1] is not (OmpCompletedEvent or OmpErrorEvent))
        {
            throw new ArgumentException("A scripted run must end with a completed or error event.", nameof(events));
        }

        scriptedRuns.Enqueue((onStart, events));
        return this;
    }

    public ValueTask<OmpSession> CreateSessionAsync(string role, CancellationToken cancellationToken)
    {
        CreatedRoles.Add(role);
        var sessionId = sessionIdsToCreate.Count > 0 ? sessionIdsToCreate.Dequeue() : Guid.NewGuid().ToString("N");
        return ValueTask.FromResult(new OmpSession(sessionId, role));
    }

    public ValueTask<OmpSession> ResumeSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        ResumedSessionIds.Add(sessionId);
        return ValueTask.FromResult(new OmpSession(sessionId, "resumed"));
    }

    public async IAsyncEnumerable<OmpEvent> RunAsync(OmpRunRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        RunRequests.Add(request);
        if (scriptedRuns.Count == 0)
        {
            throw new InvalidOperationException("FakeOmpClient.RunAsync was called with no scripted run queued.");
        }

        var (onStart, events) = scriptedRuns.Dequeue();
        onStart?.Invoke();
        foreach (var domainEvent in events)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return domainEvent;
        }
    }

    public ValueTask CancelAsync(string sessionId, CancellationToken cancellationToken)
    {
        CancelledSessionIds.Add(sessionId);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}
