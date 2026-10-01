using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

using IssueAgent.Domain;

namespace IssueAgent.Omp;

/// <summary>OMP client over the pinned executable's typed NDJSON RPC protocol.</summary>
public sealed class OmpProcessClient(
    NdjsonRpcTransport transport,
    TimeSpan shutdownGracePeriod,
    string sessionDirectory,
    TimeSpan? abortGracePeriod = null,
    RetryPolicy? configuredRetryPolicy = null,
    TimeSpan? configuredTimeout = null) : IOmpClient
{
    private readonly string sessionDirectory = NormalizeSessionDirectory(sessionDirectory);
    private readonly TimeSpan abortGracePeriod = abortGracePeriod ?? TimeSpan.FromSeconds(5);
    private readonly SemaphoreSlim dispatchGate = new(1, 1);
    private readonly RetryPolicy retryPolicy = configuredRetryPolicy ?? RetryPolicy.Default;
    private readonly TimeSpan? configuredTimeout = configuredTimeout;
    private int cancellationRequested;

    public async ValueTask<OmpSession> CreateSessionAsync(string role, CancellationToken cancellationToken)
    {
        using var deadline = CreateDeadline(cancellationToken);
        await SendTransientCommandAsync("new_session", null, deadline.Token).ConfigureAwait(false);
        var state = RequireData(await GetStateAsync(deadline.Token).ConfigureAwait(false));
        var sessionId = RequireString(state, "sessionId");
        return new OmpSession(sessionId, role, RequireSessionFile(ExtractSessionFile(state, sessionId)));
    }


    public async ValueTask<OmpSession> ResumeSessionAsync(
        string sessionId,
        string sessionFile,
        CancellationToken cancellationToken)
    {
        using var deadline = CreateDeadline(cancellationToken);
        var persistedSessionFile = RequireSessionFile(sessionFile);
        await SendTransientCommandAsync(
            "switch_session",
            new JsonObject { ["sessionPath"] = persistedSessionFile },
            deadline.Token).ConfigureAwait(false);
        var state = RequireData(await GetStateAsync(deadline.Token).ConfigureAwait(false));
        var activeSessionId = RequireString(state, "sessionId");
        if (!string.Equals(activeSessionId, sessionId, StringComparison.Ordinal))
        {
            throw new OmpRpcException(
                $"OMP resumed session '{activeSessionId}', but durable state requires '{sessionId}'.");
        }

        return new OmpSession(activeSessionId, string.Empty, RequireSessionFile(ExtractSessionFile(state, persistedSessionFile)));
    }


    internal async ValueTask<OmpModel> GetSelectedModelAsync(CancellationToken cancellationToken)
    {
        using var deadline = CreateDeadline(cancellationToken);
        var state = RequireData(await GetStateAsync(deadline.Token).ConfigureAwait(false));
        var model = state["model"] as JsonObject
            ?? throw new OmpRpcException("OMP state was missing the selected model.");
        return new OmpModel(
            RequireString(model, "provider"),
            model["id"]?.GetValue<string>()
                ?? RequireString(model, "modelId"));
    }

    internal async ValueTask SelectModelAsync(OmpModel model, CancellationToken cancellationToken)
    {
        using var deadline = CreateDeadline(cancellationToken);
        await SendTransientCommandAsync(
            "set_model",
            new JsonObject
            {
                ["provider"] = model.Provider,
                ["modelId"] = model.Id,
            },
            deadline.Token).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<OmpEvent> RunAsync(
        OmpRunRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var timeoutCts = request.Timeout is { } timeout && timeout > TimeSpan.Zero
            ? new CancellationTokenSource(timeout)
            : null;
        using var runCts = timeoutCts is null
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        var runToken = runCts.Token;

        var prompt = $"Work exclusively in the repository at '{request.WorkingDirectory}'.\n\n{request.Prompt}";
        Exception? dispatchFailure = null;
        Task<JsonObject>? promptResponse = null;
        var cancelledBeforeDispatch = false;
        var promptDispatched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await dispatchGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref cancellationRequested) != 0)
            {
                Interlocked.Exchange(ref cancellationRequested, 0);
                cancelledBeforeDispatch = true;
            }
            else
            {
                promptResponse = transport.SendCommandAsync(
                    "prompt",
                    new JsonObject { ["message"] = prompt },
                    promptDispatched,
                    runToken);
                await promptDispatched.Task.ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            dispatchFailure = exception;
        }
        finally
        {
            dispatchGate.Release();
        }

        if (cancelledBeforeDispatch)
        {
            yield return new OmpErrorEvent(
                request.SessionId,
                DateTimeOffset.UtcNow,
                "OMP run cancelled before prompt dispatch.",
                true);
            yield break;
        }

        if (dispatchFailure is null)
        {
            try
            {
                await RequireSuccessAsync(await promptResponse!.ConfigureAwait(false)).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                dispatchFailure = exception;
            }
        }

        if (dispatchFailure is OperationCanceledException && cancellationToken.IsCancellationRequested)
        {
            throw dispatchFailure;
        }

        if (timeoutCts?.IsCancellationRequested == true && !cancellationToken.IsCancellationRequested)
        {
            Interlocked.Exchange(ref cancellationRequested, 1);
            await RequestAbortAsync(suppressErrors: true, cancellationToken: CancellationToken.None).ConfigureAwait(false);
            Interlocked.Exchange(ref cancellationRequested, 0);
            yield return new OmpErrorEvent(request.SessionId, DateTimeOffset.UtcNow, "OMP run timed out.", false);
            yield break;
        }

        if (dispatchFailure is not null)
        {
            yield return new OmpErrorEvent(
                request.SessionId,
                DateTimeOffset.UtcNow,
                dispatchFailure.Message,
                false,
                GetErrorCode(dispatchFailure));
            yield break;
        }

        var assistantText = new System.Text.StringBuilder();
        await using var frames = transport.Frames
            .WithCancellation(runToken)
            .ConfigureAwait(false)
            .GetAsyncEnumerator();
        while (true)
        {
            var hasFrame = false;
            JsonObject? frame = null;
            Exception? frameReadFailure = null;
            try
            {
                hasFrame = await frames.MoveNextAsync();
                if (hasFrame)
                {
                    frame = frames.Current;
                }
            }
            catch (Exception exception)
            {
                frameReadFailure = exception;
            }

            if (frameReadFailure is not null)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    throw frameReadFailure;
                }

                if (timeoutCts?.IsCancellationRequested == true)
                {
                    Interlocked.Exchange(ref cancellationRequested, 1);
                    await RequestAbortAsync(suppressErrors: true, cancellationToken: CancellationToken.None).ConfigureAwait(false);
                    Interlocked.Exchange(ref cancellationRequested, 0);
                    yield return new OmpErrorEvent(request.SessionId, DateTimeOffset.UtcNow, "OMP run timed out.", false);
                }
                else
                {
                    yield return new OmpErrorEvent(request.SessionId, DateTimeOffset.UtcNow, frameReadFailure.Message, false);
                }
                yield break;
            }

            if (!hasFrame)
            {
                yield break;
            }

            var domainEvent = BuildEvent(request.SessionId, frame!);
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

            yield return domainEvent;
            if (domainEvent is OmpCompletedEvent or OmpErrorEvent)
            {
                yield break;
            }
        }
    }


    public async ValueTask CancelAsync(string sessionId, CancellationToken cancellationToken)
    {
        await dispatchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Interlocked.Exchange(ref cancellationRequested, 1);
        }
        finally
        {
            dispatchGate.Release();
        }

        // Once the prompt frame is dispatched, abort must bypass dispatch serialization so it can
        // interrupt that active prompt instead of waiting for its acknowledgement.
        await RequestAbortAsync(suppressErrors: false, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private async Task RequestAbortAsync(bool suppressErrors, CancellationToken cancellationToken)
    {
        // An abort control must never inherit the normal OMP command timeout: shutdown and explicit
        // cancellation need a short, independent grace while still honoring the caller's deadline.
        using var abortCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        abortCts.CancelAfter(abortGracePeriod);
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

    private async Task<JsonObject> GetStateAsync(CancellationToken cancellationToken) =>
        await SendTransientCommandAsync("get_state", null, cancellationToken).ConfigureAwait(false);

    private async Task<JsonObject> SendTransientCommandAsync(
        string command,
        JsonObject? fields,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var response = await transport.SendCommandAsync(command, fields, cancellationToken).ConfigureAwait(false);
                await RequireSuccessAsync(response).ConfigureAwait(false);
                return response;
            }
            catch (Exception exception) when (exception is OmpBrokerUnavailableException or OmpModelUnavailableException)
            {
                if (attempt >= retryPolicy.MaxAttempts)
                {
                    RetryTelemetry.RecordExhausted();
                    throw;
                }

                RetryTelemetry.RecordAttempt();
                await Task.Delay(retryPolicy.GetDelay(attempt), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private CancellationTokenSource CreateDeadline(
        CancellationToken cancellationToken,
        TimeSpan? fallbackTimeout = null)
    {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if ((configuredTimeout ?? fallbackTimeout) is { } timeout)
        {
            deadline.CancelAfter(timeout);
        }

        return deadline;
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
                    frame["cancelled"]?.GetValue<bool>() ?? false,
                    frame["errorCode"]?.GetValue<string>()),
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
        throw response["errorCode"]?.GetValue<string>() switch
        {
            "broker_unavailable" => new OmpBrokerUnavailableException(message),
            "model_unavailable" => new OmpModelUnavailableException(message),
            _ => new OmpRemoteException(message),
        };
    }

    private static string? GetErrorCode(Exception exception) => exception switch
    {
        OmpBrokerUnavailableException => "broker_unavailable",
        OmpModelUnavailableException => "model_unavailable",
        _ => null,
    };

    private static JsonObject RequireData(JsonObject response) =>
        response["data"] as JsonObject
        ?? throw new InvalidOperationException("OMP response was missing its data object.");

    private static string RequireString(JsonObject obj, string property) =>
        obj[property]?.GetValue<string>()
        ?? throw new InvalidOperationException($"OMP payload was missing required property '{property}'.");

}
internal sealed record OmpModel(string Provider, string Id);


public class OmpRpcException(string message) : Exception(message);

/// <summary>An OMP command was rejected. This is fatal unless a typed dependency code identifies it as retryable.</summary>
public sealed class OmpRemoteException(string message) : OmpRpcException(message);

/// <summary>The configured OMP authentication broker was temporarily unavailable.</summary>
public sealed class OmpBrokerUnavailableException(string message) : OmpRpcException(message);

/// <summary>The configured model dependency was temporarily unavailable.</summary>
public sealed class OmpModelUnavailableException(string message) : OmpRpcException(message);
