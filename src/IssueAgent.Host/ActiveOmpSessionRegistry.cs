using System.Collections.Concurrent;
using IssueAgent.Omp;

namespace IssueAgent.Host;

/// <summary>Tracks the OMP client and durable session id currently owned by each admitted workflow.
/// Entries exist only after session create/resume succeeds and are removed when the client is
/// disposed, preventing stale cancellation from reaching a later attempt.</summary>
public sealed partial class ActiveOmpSessionRegistry(ILogger<ActiveOmpSessionRegistry> logger)
{
    private readonly ConcurrentDictionary<WorkflowWorkKey, Entry> active = new();

    public int Count => active.Count;

    public IOmpClient Track(WorkflowWorkKey key, IOmpClient client) =>
        new TrackedOmpClient(this, key, client);

    public async ValueTask<bool> TryCancelAsync(WorkflowWorkKey key, CancellationToken cancellationToken)
    {
        if (!active.TryGetValue(key, out var entry)) return false;
        await entry.Client.CancelAsync(entry.SessionId, cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<int> CancelAllAsync(CancellationToken cancellationToken)
    {
        var snapshot = active.ToArray();
        await Task.WhenAll(snapshot.Select(async pair =>
        {
            try
            {
                await pair.Value.Client.CancelAsync(pair.Value.SessionId, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                LogCancellationFailure(logger, exception, pair.Key.Provider, pair.Key.RepositoryId, pair.Key.IssueNumber);
            }
        })).ConfigureAwait(false);
        return snapshot.Length;
    }

    private void Register(WorkflowWorkKey key, Guid owner, IOmpClient client, string sessionId) =>
        active[key] = new Entry(owner, client, sessionId);

    private void Unregister(WorkflowWorkKey key, Guid owner)
    {
        if (active.TryGetValue(key, out var entry) && entry.Owner == owner)
        {
            active.TryRemove(new KeyValuePair<WorkflowWorkKey, Entry>(key, entry));
        }
    }

    private sealed record Entry(Guid Owner, IOmpClient Client, string SessionId);

    private sealed class TrackedOmpClient(
        ActiveOmpSessionRegistry registry,
        WorkflowWorkKey key,
        IOmpClient inner) : IOmpClient
    {
        private readonly Guid owner = Guid.NewGuid();

        public async ValueTask<OmpSession> CreateSessionAsync(string role, CancellationToken cancellationToken)
        {
            var session = await inner.CreateSessionAsync(role, cancellationToken).ConfigureAwait(false);
            registry.Register(key, owner, inner, session.SessionId);
            return session;
        }

        public async ValueTask<OmpSession> ResumeSessionAsync(
            string sessionId,
            string sessionFile,
            CancellationToken cancellationToken)
        {
            var session = await inner.ResumeSessionAsync(sessionId, sessionFile, cancellationToken).ConfigureAwait(false);
            registry.Register(key, owner, inner, session.SessionId);
            return session;
        }

        public IAsyncEnumerable<OmpEvent> RunAsync(OmpRunRequest request, CancellationToken cancellationToken) =>
            inner.RunAsync(request, cancellationToken);

        public ValueTask CancelAsync(string sessionId, CancellationToken cancellationToken) =>
            inner.CancelAsync(sessionId, cancellationToken);

        public async ValueTask DisposeAsync()
        {
            registry.Unregister(key, owner);
            await inner.DisposeAsync().ConfigureAwait(false);
        }
    }

    [LoggerMessage(EventId = 30, Level = LogLevel.Warning,
        Message = "Failed to cancel active OMP session for {Provider}/{Repository} issue {IssueNumber}")]
    private static partial void LogCancellationFailure(
        ILogger logger,
        Exception exception,
        string provider,
        string repository,
        long issueNumber);
}
