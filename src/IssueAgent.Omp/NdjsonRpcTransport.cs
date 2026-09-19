using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace IssueAgent.Omp;

/// <summary>
/// Newline-delimited JSON transport for OMP's typed RPC mode. OMP does not speak JSON-RPC:
/// commands have a <c>type</c> discriminator and responses are <c>type=response</c> frames
/// correlated by an optional string id.
/// </summary>
public sealed class NdjsonRpcTransport : IAsyncDisposable
{
    private readonly Process process;
    private readonly Channel<JsonObject> frames = Channel.CreateUnbounded<JsonObject>();
    private readonly Dictionary<string, TaskCompletionSource<JsonObject>> pendingRequests = [];
    private readonly Lock pendingRequestsLock = new();
    private readonly TaskCompletionSource<JsonObject> ready =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task readLoopTask;
    private long nextRequestId;
    private int disposed;

    private NdjsonRpcTransport(Process process)
    {
        this.process = process;
        readLoopTask = Task.Run(ReadLoopAsync);
    }

    /// <summary>Starts OMP with an explicit allow-listed environment and waits for its startup
    /// <c>ready</c> frame before returning.</summary>
    public static NdjsonRpcTransport Start(
        string executablePath,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string> allowedEnvironment)
    {
        var startInfo = new ProcessStartInfo(executablePath)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.EnvironmentVariables.Clear();
        foreach (var (key, value) in allowedEnvironment)
        {
            startInfo.EnvironmentVariables[key] = value;
        }

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start OMP process '{executablePath}'.");
        var transport = new NdjsonRpcTransport(process);
        try
        {
            transport.ready.Task.Wait(TimeSpan.FromSeconds(10));
            if (!transport.ready.Task.IsCompletedSuccessfully)
            {
                throw new InvalidOperationException("OMP process exited without sending its ready frame.");
            }
        }
        catch
        {
            transport.DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw;
        }

        return transport;
    }

    /// <summary>Non-response OMP frames (agent events, UI requests, and metadata updates) in
    /// arrival order.</summary>
    public IAsyncEnumerable<JsonObject> Frames => frames.Reader.ReadAllAsync();

    /// <summary>Sends a typed OMP command and awaits its correlated response.</summary>
    public async Task<JsonObject> SendCommandAsync(
        string type,
        JsonObject? fields,
        CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref nextRequestId).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var command = fields ?? new JsonObject();
        command["id"] = id;
        command["type"] = type;
        lock (pendingRequestsLock)
        {
            pendingRequests[id] = new TaskCompletionSource<JsonObject>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }

        TaskCompletionSource<JsonObject> completion;
        lock (pendingRequestsLock)
        {
            completion = pendingRequests[id];
        }

        try
        {
            await WriteLineAsync(command, cancellationToken).ConfigureAwait(false);
            using (cancellationToken.Register(() =>
            {
                lock (pendingRequestsLock)
                {
                    pendingRequests.Remove(id);
                }
                completion.TrySetCanceled(cancellationToken);
            }))
            {
                return await completion.Task.ConfigureAwait(false);
            }
        }
        catch
        {
            lock (pendingRequestsLock)
            {
                pendingRequests.Remove(id);
            }
            throw;
        }
    }

    private async Task WriteLineAsync(JsonObject payload, CancellationToken cancellationToken)
    {
        var line = payload.ToJsonString();
        await process.StandardInput.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
        await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            string? line;
            while ((line = await process.StandardOutput.ReadLineAsync().ConfigureAwait(false)) is not null)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                JsonObject? parsed;
                try
                {
                    parsed = JsonNode.Parse(line) as JsonObject;
                }
                catch (JsonException)
                {
                    continue;
                }

                if (parsed is null)
                {
                    continue;
                }

                if (string.Equals(parsed["type"]?.GetValue<string>(), "ready", StringComparison.Ordinal))
                {
                    ready.TrySetResult(parsed);
                    continue;
                }

                if (string.Equals(parsed["type"]?.GetValue<string>(), "response", StringComparison.Ordinal) &&
                    parsed["id"]?.GetValue<string>() is { } id)
                {
                    TaskCompletionSource<JsonObject>? completion;
                    lock (pendingRequestsLock)
                    {
                        pendingRequests.Remove(id, out completion);
                    }
                    completion?.TrySetResult(parsed);
                    continue;
                }

                await frames.Writer.WriteAsync(parsed).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            ready.TrySetException(exception);
            lock (pendingRequestsLock)
            {
                foreach (var completion in pendingRequests.Values)
                {
                    completion.TrySetException(exception);
                }
                pendingRequests.Clear();
            }
        }
        finally
        {
            ready.TrySetException(new InvalidOperationException("OMP process exited before sending its ready frame."));
            frames.Writer.TryComplete();
            lock (pendingRequestsLock)
            {
                foreach (var completion in pendingRequests.Values)
                {
                    completion.TrySetException(new InvalidOperationException("OMP process exited before responding."));
                }
                pendingRequests.Clear();
            }
        }
    }

    /// <summary>Requests bounded graceful process shutdown: closes stdin, waits up to
    /// <paramref name="gracePeriod"/>, then kills the process tree if still running.</summary>
    public async Task ShutdownAsync(TimeSpan gracePeriod, CancellationToken cancellationToken)
    {
        if (process.HasExited)
        {
            return;
        }

        process.StandardInput.Close();
        try
        {
            await process.WaitForExitAsync(cancellationToken).WaitAsync(gracePeriod, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            process.Kill(entireProcessTree: true);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        if (!process.HasExited)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Process already exited between the check and the kill attempt.
            }
        }

        try
        {
            await readLoopTask.ConfigureAwait(false);
        }
        catch
        {
            // Read loop failures are surfaced to pending commands.
        }

        process.Dispose();
    }
}
