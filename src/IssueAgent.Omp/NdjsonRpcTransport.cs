using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace IssueAgent.Omp;

/// <summary>
/// Generic newline-delimited-JSON RPC transport over a child process's stdio. Frames a
/// JSON-RPC 2.0-shaped request/response/notification protocol: each line is one JSON object with
/// an optional <c>id</c> (present on requests/responses, absent on notifications). Owns the child
/// process lifecycle and exposes unsolicited notifications as an async stream.
///
/// This transport is protocol-agnostic; it knows nothing about OMP's specific method names or
/// payload shapes. See <see cref="OmpProcessClient"/> for the OMP-specific layer built on top.
/// </summary>
public sealed class NdjsonRpcTransport : IAsyncDisposable
{
    private readonly Process process;
    private readonly Channel<JsonObject> notifications = Channel.CreateUnbounded<JsonObject>();
    private readonly Dictionary<long, TaskCompletionSource<JsonObject>> pendingRequests = [];
    private readonly Lock pendingRequestsLock = new();
    private readonly Task readLoopTask;
    private long nextRequestId;
    private int disposed;

    private NdjsonRpcTransport(Process process)
    {
        this.process = process;
        readLoopTask = Task.Run(ReadLoopAsync);
    }

    /// <summary>Starts <paramref name="executablePath"/> with an explicit allow-listed environment
    /// (never the full ambient process environment) and returns a transport bound to its stdio.</summary>
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

        var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Failed to start OMP process '{executablePath}'.");
        return new NdjsonRpcTransport(process);
    }

    /// <summary>Notifications (lines without a matching pending request id) in arrival order.</summary>
    public IAsyncEnumerable<JsonObject> Notifications => notifications.Reader.ReadAllAsync();

    /// <summary>Sends a JSON-RPC request and awaits its correlated response line.</summary>
    public async Task<JsonObject> SendRequestAsync(string method, JsonNode? parameters, CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref nextRequestId);
        var completion = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (pendingRequestsLock)
        {
            pendingRequests[id] = completion;
        }

        var request = new JsonObject
        {
            ["id"] = id,
            ["method"] = method,
            ["params"] = parameters,
        };

        await WriteLineAsync(request, cancellationToken).ConfigureAwait(false);

        await using (cancellationToken.Register(() =>
        {
            lock (pendingRequestsLock)
            {
                pendingRequests.Remove(id);
            }

            completion.TrySetCanceled(cancellationToken);
        }).ConfigureAwait(false))
        {
            return await completion.Task.ConfigureAwait(false);
        }
    }

    /// <summary>Sends a fire-and-forget notification (no <c>id</c>, no response expected).</summary>
    public Task SendNotificationAsync(string method, JsonNode? parameters, CancellationToken cancellationToken)
    {
        var notification = new JsonObject
        {
            ["method"] = method,
            ["params"] = parameters,
        };
        return WriteLineAsync(notification, cancellationToken);
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

                if (parsed.TryGetPropertyValue("id", out var idNode) && idNode is not null && idNode.GetValueKind() != JsonValueKind.Null)
                {
                    var id = idNode.GetValue<long>();
                    TaskCompletionSource<JsonObject>? completion;
                    lock (pendingRequestsLock)
                    {
                        if (pendingRequests.Remove(id, out completion))
                        {
                        }
                    }

                    completion?.TrySetResult(parsed);
                }
                else
                {
                    await notifications.Writer.WriteAsync(parsed).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            notifications.Writer.TryComplete();
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

    /// <summary>Requests bounded-graceful process shutdown: closes stdin, waits up to
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
        catch (Exception)
        {
            // Read loop failures are already surfaced to pending requests/notifications.
        }

        process.Dispose();
    }
}
