using System.Runtime.CompilerServices;

namespace IssueAgent.Omp;

/// <summary>
/// Selects OMP's configured model aliases at process startup. The RPC protocol only accepts a
/// provider/model pair, so semantic roles such as <c>plan</c> and <c>task</c> must never be sent
/// over RPC. When restoring a durable session, OMP restores that session's previous model; capture
/// the startup-resolved model and apply it with the supported typed control after restoration.
/// </summary>
internal sealed class OmpRoleProcessClient(
    string executablePath,
    IReadOnlyList<string> arguments,
    string workingDirectory,
    IReadOnlyDictionary<string, string> allowedEnvironment,
    TimeSpan shutdownGracePeriod,
    string sessionDirectory) : IOmpClient
{
    private OmpProcessClient? inner;
    private OmpSession? session;
    private string? activeRole;

    public async ValueTask<OmpSession> CreateSessionAsync(string role, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(role);
        await StartForRoleAsync(role).ConfigureAwait(false);
        session = await inner!.CreateSessionAsync(role, cancellationToken).ConfigureAwait(false);
        activeRole = role;
        return session;
    }

    public async ValueTask<OmpSession> ResumeSessionAsync(string sessionId, string sessionFile, CancellationToken cancellationToken)
    {
        await StartForRoleAsync(role: null).ConfigureAwait(false);
        session = await inner!.ResumeSessionAsync(sessionId, sessionFile, cancellationToken).ConfigureAwait(false);
        activeRole = null;
        return session;
    }

    public async ValueTask SelectRoleAsync(string role, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(role);
        if (string.Equals(activeRole, role, StringComparison.Ordinal))
        {
            return;
        }

        if (session is null)
        {
            await StartForRoleAsync(role).ConfigureAwait(false);
            activeRole = role;
            return;
        }

        var persistedSession = session.SessionFile
            ?? throw new OmpRpcException($"OMP session '{session.SessionId}' did not provide a durable session file.");
        await StartForRoleAsync(role).ConfigureAwait(false);
        var selectedModel = await inner!.GetSelectedModelAsync(cancellationToken).ConfigureAwait(false);
        session = await inner.ResumeSessionAsync(session.SessionId, persistedSession, cancellationToken).ConfigureAwait(false);
        await inner.SelectModelAsync(selectedModel, cancellationToken).ConfigureAwait(false);
        activeRole = role;
    }

    public async IAsyncEnumerable<OmpEvent> RunAsync(
        OmpRunRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (inner is null)
        {
            throw new InvalidOperationException("OMP session has not been created or resumed.");
        }

        await foreach (var item in inner.RunAsync(request, cancellationToken).ConfigureAwait(false))
        {
            yield return item;
        }
    }

    public ValueTask CancelAsync(string sessionId, CancellationToken cancellationToken) =>
        inner?.CancelAsync(sessionId, cancellationToken) ?? ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (inner is not null)
        {
            await inner.DisposeAsync().ConfigureAwait(false);
            inner = null;
        }
    }

    private async ValueTask StartForRoleAsync(string? role)
    {
        if (inner is not null)
        {
            await inner.DisposeAsync().ConfigureAwait(false);
        }

        var startupArguments = role is null
            ? arguments
            : arguments.Concat(["--model", role]).ToArray();
        var transport = NdjsonRpcTransport.Start(executablePath, startupArguments, workingDirectory, allowedEnvironment);
        inner = new OmpProcessClient(transport, shutdownGracePeriod, sessionDirectory);
        activeRole = null;
    }
}
