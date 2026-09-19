namespace IssueAgent.Omp;

/// <summary>Owns OMP process lifecycle, RPC framing, role selection, structured events, session
/// create/resume, cancellation, and errors. One session follows an issue through planning,
/// replanning, implementation, revision, and conflict resolution; callers capture the server
/// session id and session file immediately and persist both remotely.
///
/// This interface is provider-neutral: no provider, Git, or transport types appear here. It never
/// receives provider/Git/SSH/Kubernetes credentials; only the explicit allow-listed execution
/// environment configured by the caller.
/// </summary>
public interface IOmpClient : IAsyncDisposable
{
    /// <summary>Starts a new OMP session for the given semantic role (for example <c>plan</c> or
    /// <c>task</c>, as configured under <c>omp.roles</c>).</summary>
    ValueTask<OmpSession> CreateSessionAsync(string role, CancellationToken cancellationToken);

    /// <summary>Resumes a previously created session after a restart using its durable session
    /// identifier and persisted OMP session file. Both values are required: session identifiers
    /// are server metadata, while <paramref name="sessionFile"/> is the recovery handle accepted
    /// by OMP's <c>switch_session</c> command.</summary>
    ValueTask<OmpSession> ResumeSessionAsync(string sessionId, string sessionFile, CancellationToken cancellationToken);

    /// <summary>Selects the configured semantic role for the next turn of this durable session.</summary>
    ValueTask SelectRoleAsync(string role, CancellationToken cancellationToken) => ValueTask.CompletedTask;

    /// <summary>Runs one turn of the session against <paramref name="request"/> and streams
    /// structured events as they occur, ending in exactly one <see cref="OmpCompletedEvent"/> or
    /// <see cref="OmpErrorEvent"/>. The working directory, allow-listed environment, and prompt are
    /// caller-controlled; this method performs no business-result parsing.</summary>
    IAsyncEnumerable<OmpEvent> RunAsync(OmpRunRequest request, CancellationToken cancellationToken);

    /// <summary>Requests cancellation of the session's in-flight run, if any. Cancellation is
    /// bounded and cooperative, matching the process's graceful-shutdown contract.</summary>
    ValueTask CancelAsync(string sessionId, CancellationToken cancellationToken);
}

/// <summary>An OMP session handle. The id is the server-assigned durable identifier while
/// <paramref name="SessionFile"/> is the path used by <c>switch_session</c>.</summary>
public sealed record OmpSession(string SessionId, string Role, string? SessionFile = null);

/// <summary>One turn of work for an existing session.</summary>
public sealed record OmpRunRequest(
    string SessionId,
    string WorkingDirectory,
    string Prompt,
    IReadOnlyDictionary<string, string> ExecutionEnvironment,
    TimeSpan? Timeout = null);
