using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

namespace IssueAgent.Omp;

/// <summary>
/// <see cref="IOmpClient"/> implementation over <see cref="NdjsonRpcTransport"/>.
///
/// [INFERENCE] The pinned OMP executable's exact RPC method names and payload shapes are not
/// available as a published specification; the wire schema below (<c>session.create</c>,
/// <c>session.resume</c>, <c>run</c>, <c>cancel</c>, and <c>event</c> notifications keyed by
/// <c>sessionId</c>/<c>type</c>) is this project's own documented, best-effort JSON-RPC-over-NDJSON
/// design built from the specification's described capabilities (process lifecycle, RPC framing,
/// role selection, structured events, session create/resume, cancellation, errors). It has not been
/// verified against the real OMP binary's protocol. Adjust <see cref="BuildEvent"/> and the request
/// payload builders here first if the real protocol differs; the transport and session/event
/// contracts in <see cref="IOmpClient"/> should not need to change.
/// </summary>
public sealed class OmpProcessClient(NdjsonRpcTransport transport, TimeSpan shutdownGracePeriod) : IOmpClient
{
    public async ValueTask<OmpSession> CreateSessionAsync(string role, CancellationToken cancellationToken)
    {
        var response = await transport.SendRequestAsync("session.create", new JsonObject { ["role"] = role }, cancellationToken).ConfigureAwait(false);
        var result = RequireResult(response);
        var sessionId = RequireString(result, "sessionId");
        return new OmpSession(sessionId, role);
    }

    public async ValueTask<OmpSession> ResumeSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        var response = await transport.SendRequestAsync("session.resume", new JsonObject { ["sessionId"] = sessionId }, cancellationToken).ConfigureAwait(false);
        var result = RequireResult(response);
        var role = RequireString(result, "role");
        return new OmpSession(sessionId, role);
    }

    public async IAsyncEnumerable<OmpEvent> RunAsync(OmpRunRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var environment = new JsonObject();
        foreach (var (key, value) in request.ExecutionEnvironment)
        {
            environment[key] = value;
        }

        var runParams = new JsonObject
        {
            ["sessionId"] = request.SessionId,
            ["workingDirectory"] = request.WorkingDirectory,
            ["prompt"] = request.Prompt,
            ["environment"] = environment,
            ["timeoutMs"] = request.Timeout?.TotalMilliseconds,
        };

        await transport.SendRequestAsync("run", runParams, cancellationToken).ConfigureAwait(false);

        await foreach (var notification in transport.Notifications.WithCancellation(cancellationToken))
        {
            if (notification["method"]?.GetValue<string>() != "event")
            {
                continue;
            }

            var eventParams = notification["params"] as JsonObject
                ?? throw new InvalidOperationException("OMP event notification was missing its params object.");

            if (RequireString(eventParams, "sessionId") != request.SessionId)
            {
                continue;
            }

            var domainEvent = BuildEvent(eventParams);
            yield return domainEvent;

            if (domainEvent is OmpCompletedEvent or OmpErrorEvent)
            {
                yield break;
            }
        }
    }

    public async ValueTask CancelAsync(string sessionId, CancellationToken cancellationToken)
    {
        await transport.SendRequestAsync("cancel", new JsonObject { ["sessionId"] = sessionId }, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await transport.ShutdownAsync(shutdownGracePeriod, CancellationToken.None).ConfigureAwait(false);
        await transport.DisposeAsync().ConfigureAwait(false);
    }

    private static OmpEvent BuildEvent(JsonObject eventParams)
    {
        var sessionId = RequireString(eventParams, "sessionId");
        var timestamp = eventParams["timestamp"] is { } timestampNode
            ? DateTimeOffset.Parse(timestampNode.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture)
            : DateTimeOffset.UtcNow;
        var type = RequireString(eventParams, "type");

        return type switch
        {
            "message" => new OmpMessageEvent(sessionId, timestamp, RequireString(eventParams, "text")),
            "toolCall" => new OmpToolCallEvent(
                sessionId, timestamp, RequireString(eventParams, "toolCallId"), RequireString(eventParams, "toolName"),
                (eventParams["arguments"] as JsonObject)?.ToJsonString() ?? "{}"),
            "toolResult" => new OmpToolResultEvent(
                sessionId, timestamp, RequireString(eventParams, "toolCallId"), eventParams["isError"]?.GetValue<bool>() ?? false,
                eventParams["result"]?.ToJsonString() ?? "null"),
            "completed" => new OmpCompletedEvent(sessionId, timestamp, eventParams["result"]?.ToJsonString() ?? "null"),
            "error" => new OmpErrorEvent(sessionId, timestamp, RequireString(eventParams, "message"), eventParams["cancelled"]?.GetValue<bool>() ?? false),
            _ => throw new InvalidOperationException($"Unrecognized OMP event type '{type}'."),
        };
    }

    private static JsonObject RequireResult(JsonObject response)
    {
        if (response["error"] is JsonObject errorObject)
        {
            var message = errorObject["message"]?.GetValue<string>() ?? "OMP returned an unspecified error.";
            throw new OmpRpcException(message);
        }

        return response["result"] as JsonObject ?? throw new InvalidOperationException("OMP response was missing a result object.");
    }

    private static string RequireString(JsonObject obj, string property) =>
        obj[property]?.GetValue<string>() ?? throw new InvalidOperationException($"OMP payload was missing required property '{property}'.");
}

public sealed class OmpRpcException(string message) : Exception(message);
