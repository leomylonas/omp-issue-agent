using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

namespace IssueAgent.Omp;

/// <summary>OMP client over the pinned executable's typed NDJSON RPC protocol.</summary>
public sealed class OmpProcessClient(
    NdjsonRpcTransport transport,
    TimeSpan shutdownGracePeriod,
    string sessionDirectory,
    TimeSpan? abortGracePeriod = null) : IOmpClient
{
    private readonly string sessionDirectory = NormalizeSessionDirectory(sessionDirectory);
    private readonly TimeSpan abortGracePeriod = abortGracePeriod ?? TimeSpan.FromSeconds(5);
    private readonly SemaphoreSlim dispatchGate = new(1, 1);
    private int cancellationRequested;

    public async ValueTask<OmpSession> CreateSessionAsync(string role, CancellationToken cancellationToken)
    {
        await RequireSuccessAsync(
            await transport.SendCommandAsync(
                "new_session",
                null,
                cancellationToken).ConfigureAwait(false))
            .ConfigureAwait(false);
        if (OmpModel.TryParse(role, out var model))
        {
            await RequireSuccessAsync(
                await transport.SendCommandAsync(
                    "set_model",
                    new JsonObject { ["provider"] = model.Provider, ["modelId"] = model.ModelId },
                    cancellationToken).ConfigureAwait(false))
                .ConfigureAwait(false);
        }
        var state = RequireData(
            await transport.SendCommandAsync("get_state", null, cancellationToken).ConfigureAwait(false));
        var sessionId = RequireString(state, "sessionId");
        return new OmpSession(sessionId, role, RequireSessionFile(ExtractSessionFile(state, sessionId)));
    }

    public async ValueTask<OmpSession> ResumeSessionAsync(
        string sessionId,
        string sessionFile,
        CancellationToken cancellationToken)
    {
        var persistedSessionFile = RequireSessionFile(sessionFile);
        var response = await transport.SendCommandAsync(
            "switch_session",
            new JsonObject { ["sessionPath"] = persistedSessionFile },
            cancellationToken).ConfigureAwait(false);
        await RequireSuccessAsync(response).ConfigureAwait(false);
        var state = RequireData(
            await transport.SendCommandAsync("get_state", null, cancellationToken).ConfigureAwait(false));
        var activeSessionId = RequireString(state, "sessionId");
        if (!string.Equals(activeSessionId, sessionId, StringComparison.Ordinal))
        {
            throw new OmpRpcException(
                $"OMP resumed session '{activeSessionId}', but durable state requires '{sessionId}'.");
        }

        return new OmpSession(activeSessionId, string.Empty, RequireSessionFile(ExtractSessionFile(state, persistedSessionFile)));
    }


    public async IAsyncEnumerable<OmpEvent> RunAsync(
        OmpRunRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var events = await RunToListAsync(request, cancellationToken).ConfigureAwait(false);

        foreach (var domainEvent in events)
        {
            yield return domainEvent;
        }
    }

    private async Task<IReadOnlyList<OmpEvent>> RunToListAsync(
        OmpRunRequest request,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = request.Timeout is { } timeout && timeout > TimeSpan.Zero
            ? new CancellationTokenSource(timeout)
            : null;
        using var runCts = timeoutCts is null
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        var runToken = runCts.Token;
        var events = new List<OmpEvent>();
        var prompt = $"Work exclusively in the repository at '{request.WorkingDirectory}'.\n\n{request.Prompt}";
        try
        {
            await dispatchGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                if (Interlocked.CompareExchange(ref cancellationRequested, 0, 0) != 0)
                {
                    Interlocked.Exchange(ref cancellationRequested, 0);
                    events.Add(new OmpErrorEvent(
                        request.SessionId,
                        DateTimeOffset.UtcNow,
                        "OMP run cancelled before prompt dispatch.",
                        true));
                    return events;
                }

                var response = await transport.SendCommandAsync(
                    "prompt",
                    new JsonObject { ["message"] = prompt },
                    runToken).ConfigureAwait(false);
                await RequireSuccessAsync(response).ConfigureAwait(false);
            }
            finally
            {
                dispatchGate.Release();
            }

            var assistantText = new System.Text.StringBuilder();

            await foreach (var frame in transport.Frames.WithCancellation(runToken).ConfigureAwait(false))
            {
                var domainEvent = BuildEvent(request.SessionId, frame);
                if (domainEvent is OmpCompletedEvent && Volatile.Read(ref cancellationRequested) != 0)
                {
                    domainEvent = new OmpErrorEvent(request.SessionId, DateTimeOffset.UtcNow, "OMP run cancelled.", true);
                }
                if (domainEvent is OmpMessageEvent message)
                {
                    assistantText.Append(message.Text);
                }

                if (domainEvent is OmpCompletedEvent completed &&
                    string.Equals(completed.ResultJson, "null", StringComparison.Ordinal) &&
                    assistantText.Length > 0)
                {
                    domainEvent = completed with { ResultJson = assistantText.ToString() };
                }

                if (domainEvent is null)
                {
                    continue;
                }

                events.Add(domainEvent);
                if (domainEvent is OmpCompletedEvent or OmpErrorEvent)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (
            timeoutCts?.IsCancellationRequested == true && !cancellationToken.IsCancellationRequested)
        {
            Interlocked.Exchange(ref cancellationRequested, 1);
            await RequestAbortAsync(suppressErrors: true).ConfigureAwait(false);
            Interlocked.Exchange(ref cancellationRequested, 0);
            events.Add(new OmpErrorEvent(request.SessionId, DateTimeOffset.UtcNow, "OMP run timed out.", false));
        }

        return events;
    }

    public async ValueTask CancelAsync(string sessionId, CancellationToken cancellationToken)
    {
        Interlocked.Exchange(ref cancellationRequested, 1);
        await dispatchGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await RequestAbortAsync(suppressErrors: false).ConfigureAwait(false);
        }
        finally
        {
            dispatchGate.Release();
        }
    }

    private async Task RequestAbortAsync(bool suppressErrors)
    {
        // An already-cancelled caller token must not prevent the abort command from reaching OMP.
        // Its independent deadline also prevents shutdown/cancel paths from hanging on a dead child.
        using var abortCts = new CancellationTokenSource(abortGracePeriod);
        try
        {
            await RequireSuccessAsync(
                await transport.SendCommandAsync("abort", null, abortCts.Token).ConfigureAwait(false))
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (abortCts.IsCancellationRequested && suppressErrors)
        {
            // The timeout path must report the original timeout even when OMP is already gone.
        }
        catch (Exception) when (suppressErrors)
        {
            // The timeout path must report the original timeout even when OMP is already gone.
        }
    }

    public async ValueTask DisposeAsync()
    {
        await transport.ShutdownAsync(shutdownGracePeriod, CancellationToken.None).ConfigureAwait(false);
        await transport.DisposeAsync().ConfigureAwait(false);
        dispatchGate.Dispose();
    }

    private static string NormalizeSessionDirectory(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
    }

    private string RequireSessionFile(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var path = Path.GetFullPath(value);
        var relativePath = Path.GetRelativePath(sessionDirectory, path);
        if (Path.IsPathRooted(relativePath) ||
            string.Equals(relativePath, ".", StringComparison.Ordinal) ||
            relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
            string.Equals(relativePath, "..", StringComparison.Ordinal))
        {
            throw new OmpRpcException($"OMP session file '{value}' is outside the configured session directory.");
        }

        return path;
    }
    private static OmpEvent? BuildEvent(string sessionId, JsonObject frame)
    {
        var type = frame["type"]?.GetValue<string>();
        var timestamp = DateTimeOffset.UtcNow;
        return type switch
        {
            "response" when frame["success"]?.GetValue<bool>() == false =>
                new OmpErrorEvent(
                    sessionId,
                    timestamp,
                    frame["error"]?.GetValue<string>() ?? "OMP command failed.",
                    frame["cancelled"]?.GetValue<bool>() ?? false),
            "message_update" => BuildMessage(sessionId, timestamp, frame),
            "tool_execution_start" => new OmpToolCallEvent(
                sessionId,
                timestamp,
                frame["toolCallId"]?.GetValue<string>() ?? frame["callId"]?.GetValue<string>() ?? string.Empty,
                frame["toolName"]?.GetValue<string>() ?? frame["tool"]?.GetValue<string>() ?? "tool",
                frame["arguments"]?.ToJsonString() ?? frame["args"]?.ToJsonString() ?? "{}"),
            "tool_execution_end" => new OmpToolResultEvent(
                sessionId,
                timestamp,
                frame["toolCallId"]?.GetValue<string>() ?? frame["callId"]?.GetValue<string>() ?? string.Empty,
                frame["isError"]?.GetValue<bool>() ?? false,
                frame["result"]?.ToJsonString() ?? frame["resultJson"]?.ToJsonString() ?? "null"),
            "agent_end" when frame["isTerminal"]?.GetValue<bool>() != false =>
                new OmpCompletedEvent(sessionId, timestamp, ExtractAssistantResult(frame)),
            "extension_error" => new OmpErrorEvent(
                sessionId,
                timestamp,
                frame["error"]?.GetValue<string>() ?? "OMP extension failed.",
                false),
            _ => null,
        };
    }

    private static string ExtractSessionFile(JsonObject state, string fallback) =>
        state["sessionFile"]?.GetValue<string>()
        ?? state["sessionPath"]?.GetValue<string>()
        ?? fallback;

    private static OmpMessageEvent? BuildMessage(string sessionId, DateTimeOffset timestamp, JsonObject frame)
    {
        var eventObject = frame["assistantMessageEvent"] as JsonObject;
        if (eventObject?["type"]?.GetValue<string>() != "text_delta")
        {
            return null;
        }

        var text = eventObject["delta"]?.GetValue<string>();
        return text is null ? null : new OmpMessageEvent(sessionId, timestamp, text);
    }

    private static string ExtractAssistantResult(JsonObject frame)
    {
        if (frame["messages"] is not JsonArray messages)
        {
            return "null";
        }

        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i] is not JsonObject message ||
                !string.Equals(message["role"]?.GetValue<string>(), "assistant", StringComparison.Ordinal))
            {
                continue;
            }

            if (message["content"] is JsonArray content)
            {
                var text = string.Concat(content
                    .OfType<JsonObject>()
                    .Where(item => item["type"]?.GetValue<string>() == "text")
                    .Select(item => item["text"]?.GetValue<string>() ?? string.Empty));
                return text;
            }

            if (message["content"] is JsonValue contentValue &&
                contentValue.TryGetValue<string>(out var contentText))
            {
                return contentText;
            }
        }

        return "null";
    }

    private static Task RequireSuccessAsync(JsonObject response)
    {
        if (response["success"]?.GetValue<bool>() == true)
        {
            return Task.CompletedTask;
        }

        var message = response["error"]?.GetValue<string>() ?? "OMP command failed.";
        throw new OmpRpcException(message);
    }

    private static JsonObject RequireData(JsonObject response) =>
        response["data"] as JsonObject
        ?? throw new InvalidOperationException("OMP response was missing its data object.");

    private static string RequireString(JsonObject obj, string property) =>
        obj[property]?.GetValue<string>()
        ?? throw new InvalidOperationException($"OMP payload was missing required property '{property}'.");
}

/// <summary>OMP's supported model-selection command requires an explicit provider and model id.</summary>
public sealed record OmpModel(string Provider, string ModelId)
{
    public static bool TryParse(string value, out OmpModel model)
    {
        var separator = value.IndexOf('/');
        if (separator <= 0 || separator == value.Length - 1)
        {
            model = default!;
            return false;
        }

        model = new OmpModel(value[..separator], value[(separator + 1)..]);
        return true;
    }

}

public sealed class OmpRpcException(string message) : Exception(message);
