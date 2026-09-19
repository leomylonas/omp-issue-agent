using System.Diagnostics;
using System.Runtime.CompilerServices;
using IssueAgent.Omp;
using Microsoft.Extensions.Logging;

namespace IssueAgent.Observability;

/// <summary>Wraps an <see cref="IOmpClient"/> with tracing, metrics, and structured logging
/// (specification §29-30: OMP plan/implement/revise spans, request/error/duration metrics, and
/// per-event structured logs tagged with <c>OmpEventType</c>).</summary>
public sealed class ObservableOmpClient(IOmpClient inner, IssueAgentMetrics metrics, ILogger<ObservableOmpClient> logger) : IOmpClient
{
    public async ValueTask<OmpSession> CreateSessionAsync(string role, CancellationToken cancellationToken)
    {
        using var activity = IssueAgentActivitySource.StartOmpOperation("session.create", sessionId: null);
        var tags = new KeyValuePair<string, object?>[] { new(LogContextFields.Operation, "session.create") };
        metrics.OmpRequests.Add(1, tags);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var session = await inner.CreateSessionAsync(role, cancellationToken).ConfigureAwait(false);
            OmpLogMessages.SessionCreated(logger, session.SessionId, role);
            return session;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Graceful shutdown/agent-cancel is normal operation (specification §27), not an OMP
            // failure; must never inflate issueagent_omp_errors_total.
            throw;
        }
        catch (Exception ex)
        {
            metrics.OmpErrors.Add(1, tags);
            activity?.SetStatus(ActivityStatusCode.Error);
            OmpLogMessages.SessionCreationFailed(logger, ex.GetType().Name, role);
            throw;
        }
        finally
        {
            metrics.OmpDuration.Record(stopwatch.Elapsed.TotalSeconds, tags);
        }
    }

    public async ValueTask<OmpSession> ResumeSessionAsync(
        string sessionId,
        string? sessionFile,
        CancellationToken cancellationToken)
    {
        using var activity = IssueAgentActivitySource.StartOmpOperation("session.resume", sessionId);
        var tags = new KeyValuePair<string, object?>[] { new(LogContextFields.Operation, "session.resume") };
        metrics.OmpRequests.Add(1, tags);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            return await inner.ResumeSessionAsync(sessionId, sessionFile, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            metrics.OmpErrors.Add(1, tags);
            activity?.SetStatus(ActivityStatusCode.Error);
            OmpLogMessages.SessionResumeFailed(logger, ex.GetType().Name, sessionId);
            throw;
        }
        finally
        {
            metrics.OmpDuration.Record(stopwatch.Elapsed.TotalSeconds, tags);
        }
    }
    public ValueTask<OmpSession> ResumeSessionAsync(string sessionId, CancellationToken cancellationToken) =>
        ResumeSessionAsync(sessionId, sessionFile: null, cancellationToken);

    public async IAsyncEnumerable<OmpEvent> RunAsync(OmpRunRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var activity = IssueAgentActivitySource.StartOmpOperation("run", request.SessionId);
        var tags = new KeyValuePair<string, object?>[] { new(LogContextFields.Operation, "run") };
        metrics.OmpRequests.Add(1, tags);
        metrics.ActiveOperations.Add(1, tags);
        var stopwatch = Stopwatch.StartNew();
        var failed = false;

        var enumerator = inner.RunAsync(request, cancellationToken).GetAsyncEnumerator(cancellationToken);
        try
        {
            while (true)
            {
                OmpEvent? domainEvent;
                try
                {
                    if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                    {
                        break;
                    }

                    domainEvent = enumerator.Current;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failed = true;
                    metrics.OmpErrors.Add(1, tags);
                    activity?.SetStatus(ActivityStatusCode.Error);
                    OmpLogMessages.RunFailed(logger, ex.GetType().Name, request.SessionId);
                    throw;
                }

                OmpLogMessages.EventObserved(logger, domainEvent.GetType().Name, request.SessionId);
                if (domainEvent is OmpErrorEvent errorEvent && !errorEvent.WasCancelled)
                {
                    failed = true;
                    metrics.OmpErrors.Add(1, tags);
                }

                yield return domainEvent;
            }
        }
        finally
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
            metrics.ActiveOperations.Add(-1, tags);
            metrics.OmpDuration.Record(stopwatch.Elapsed.TotalSeconds, tags);
            if (failed)
            {
                activity?.SetStatus(ActivityStatusCode.Error);
            }
        }
    }

    public async ValueTask CancelAsync(string sessionId, CancellationToken cancellationToken)
    {
        using var activity = IssueAgentActivitySource.StartOmpOperation("cancel", sessionId);
        try
        {
            await inner.CancelAsync(sessionId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error);
            OmpLogMessages.CancellationFailed(logger, ex.GetType().Name, sessionId);
            throw;
        }
    }

    public ValueTask DisposeAsync() => inner.DisposeAsync();
}
