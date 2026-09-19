using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

namespace IssueAgent.Omp;

/// <summary>OMP client over the pinned executable's typed NDJSON RPC protocol.</summary>
public sealed class OmpProcessClient(NdjsonRpcTransport transport, TimeSpan shutdownGracePeriod) : IOmpClient
{
    private int cancellationRequested;

    public async ValueTask<OmpSession> CreateSessionAsync(string role, CancellationToken cancellationToken)
    {
        await RequireSuccessAsync(
            await transport.SendCommandAsync("new_session", null, cancellationToken).ConfigureAwait(false))
            .ConfigureAwait(false);
        var state = RequireData(
            await transport.SendCommandAsync("get_state", null, cancellationToken).ConfigureAwait(false));
        var sessionId = RequireString(state, "sessionId");
        // OMP sessions do not carry IssueAgent's semantic planning/implementation role. The role
        // remains a host concern and is retained on the domain handle for logging and fakes.
        return new OmpSession(sessionId, role);
    }

    public async ValueTask<OmpSession> ResumeSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        var response = await transport.SendCommandAsync(
            "switch_session",
            new JsonObject { ["sessionPath"] = sessionId },
            cancellationToken).ConfigureAwait(false);
        await RequireSuccessAsync(response).ConfigureAwait(false);
        var state = RequireData(
            await transport.SendCommandAsync("get_state", null, cancellationToken).ConfigureAwait(false));
        var activeSessionId = state["sessionId"]?.GetValue<string>() ?? sessionId;
        return new OmpSession(activeSessionId, string.Empty);
    }

    public async IAsyncEnumerable<OmpEvent> RunAsync(
        OmpRunRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // OMP's RPC protocol has no per-prompt working-directory or environment fields. The
        // process is launched with the allow-listed environment; make the retained worktree
        // explicit in the prompt so built-in tools operate on the workflow worktree.
        var prompt = $"Work exclusively in the repository at '{request.WorkingDirectory}'.\n\n{request.Prompt}";
        Interlocked.Exchange(ref cancellationRequested, 0);
        var response = await transport.SendCommandAsync(
            "prompt",
            new JsonObject { ["message"] = prompt },
            cancellationToken).ConfigureAwait(false);
        await RequireSuccessAsync(response).ConfigureAwait(false);
        var assistantText = new System.Text.StringBuilder();

        await foreach (var frame in transport.Frames.WithCancellation(cancellationToken).ConfigureAwait(false))
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

            yield return domainEvent;
            if (domainEvent is OmpCompletedEvent or OmpErrorEvent)
            {
                yield break;
            }
        }
    }

    public async ValueTask CancelAsync(string sessionId, CancellationToken cancellationToken)
    {
        Interlocked.Exchange(ref cancellationRequested, 1);
        await RequireSuccessAsync(
            await transport.SendCommandAsync("abort", null, cancellationToken).ConfigureAwait(false))
            .ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await transport.ShutdownAsync(shutdownGracePeriod, CancellationToken.None).ConfigureAwait(false);
        await transport.DisposeAsync().ConfigureAwait(false);
    }

    private static OmpEvent? BuildEvent(string sessionId, JsonObject frame)
    {
        var type = frame["type"]?.GetValue<string>();
        var timestamp = DateTimeOffset.UtcNow;
        return type switch
        {
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

public sealed class OmpRpcException(string message) : Exception(message);
