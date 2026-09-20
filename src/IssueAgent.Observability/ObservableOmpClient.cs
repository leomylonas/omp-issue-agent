using System.Text.Json;
using System.Text.Json.Nodes;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using IssueAgent.Omp;
using Microsoft.Extensions.Logging;

namespace IssueAgent.Observability;

/// <summary>Wraps an <see cref="IOmpClient"/> with tracing, metrics, and structured logging
/// (specification §29-30: OMP plan/implement/revise spans, request/error/duration metrics, and
/// per-event structured logs tagged with <c>OmpEventType</c>).</summary>
public sealed class ObservableOmpClient : IOmpClient
{
    private readonly IOmpClient inner;
    private readonly IssueAgentMetrics metrics;
    private readonly ILogger<ObservableOmpClient> logger;
    private readonly string[] executionSecretValues;

    public ObservableOmpClient(
        IOmpClient inner,
        IssueAgentMetrics metrics,
        ILogger<ObservableOmpClient> logger,
        IEnumerable<string>? executionSecretValues = null)
    {
        this.inner = inner;
        this.metrics = metrics;
        this.logger = logger;
        this.executionSecretValues = executionSecretValues?
            .Where(secret => !string.IsNullOrEmpty(secret))
            .Distinct(StringComparer.Ordinal)
            .ToArray() ?? [];
    }
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
        string sessionFile,
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

    public async ValueTask SelectRoleAsync(string role, CancellationToken cancellationToken)
    {
        using var activity = IssueAgentActivitySource.StartOmpOperation("role.select", sessionId: null);
        var tags = new KeyValuePair<string, object?>[] { new(LogContextFields.Operation, "role.select") };
        metrics.OmpRequests.Add(1, tags);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await inner.SelectRoleAsync(role, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Agent cancellation is an expected control path, not an OMP failure.
            throw;
        }
        catch (Exception ex)
        {
            metrics.OmpErrors.Add(1, tags);
            activity?.SetStatus(ActivityStatusCode.Error);
            OmpLogMessages.RoleSelectionFailed(logger, ex.GetType().Name, role);
            throw;
        }
        finally
        {
            metrics.OmpDuration.Record(stopwatch.Elapsed.TotalSeconds, tags);
        }
    }

    public async IAsyncEnumerable<OmpEvent> RunAsync(OmpRunRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var activity = IssueAgentActivitySource.StartOmpOperation("run", request.SessionId);
        var tags = new KeyValuePair<string, object?>[] { new(LogContextFields.Operation, "run") };
        metrics.OmpRequests.Add(1, tags);
        metrics.ActiveOperations.Add(1, tags);
        var stopwatch = Stopwatch.StartNew();
        var failed = false;


        if (logger.IsEnabled(LogLevel.Debug))
        {
            var redactedPrompt = RedactExecutionSecrets(request.Prompt, executionSecretValues);
            OmpLogMessages.PromptDispatched(logger, request.SessionId, redactedPrompt);
        }
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
                    var redactedMessage = RedactExecutionSecrets(ex.Message, executionSecretValues);
                    if (!string.Equals(redactedMessage, ex.Message, StringComparison.Ordinal))
                    {
                        throw new OmpRpcException(redactedMessage);
                    }
                    throw;
                }

                domainEvent = RedactEvent(domainEvent, executionSecretValues);
                OmpLogMessages.EventObserved(logger, domainEvent.GetType().Name, request.SessionId);
                if (logger.IsEnabled(LogLevel.Debug))
                {
                    var payload = SerializeRedactedEvent(domainEvent, executionSecretValues);
                    OmpLogMessages.EventPayload(logger, request.SessionId, payload);
                }
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

    private static OmpEvent RedactEvent(OmpEvent domainEvent, IEnumerable<string> executionSecretValues) =>
        domainEvent switch
        {
            OmpMessageEvent message => message with { Text = RedactExecutionSecrets(message.Text, executionSecretValues) },
            OmpToolCallEvent toolCall => toolCall with { ArgumentsJson = RedactJson(toolCall.ArgumentsJson, executionSecretValues) },
            OmpToolResultEvent toolResult => toolResult with { ResultJson = RedactJson(toolResult.ResultJson, executionSecretValues) },
            OmpCompletedEvent completed => completed with { ResultJson = RedactJson(completed.ResultJson, executionSecretValues) },
            OmpErrorEvent error => error with { Message = RedactExecutionSecrets(error.Message, executionSecretValues) },
            _ => throw new ArgumentOutOfRangeException(nameof(domainEvent)),
        };

    private static string SerializeRedactedEvent(OmpEvent domainEvent, IEnumerable<string> executionSecretValues) =>
        domainEvent switch
        {
            OmpMessageEvent message => JsonSerializer.Serialize(new
            {
                Type = "message",
                Text = RedactExecutionSecrets(message.Text, executionSecretValues),
            }),
            OmpToolCallEvent toolCall => JsonSerializer.Serialize(new
            {
                Type = "tool_call",
                toolCall.ToolCallId,
                toolCall.ToolName,
                Arguments = RedactJson(toolCall.ArgumentsJson, executionSecretValues),
            }),
            OmpToolResultEvent toolResult => JsonSerializer.Serialize(new
            {
                Type = "tool_result",
                toolResult.ToolCallId,
                toolResult.IsError,
                Result = RedactJson(toolResult.ResultJson, executionSecretValues),
            }),
            OmpCompletedEvent completed => JsonSerializer.Serialize(new
            {
                Type = "completed",
                Result = RedactJson(completed.ResultJson, executionSecretValues),
            }),
            OmpErrorEvent error => JsonSerializer.Serialize(new
            {
                Type = "error",
                Message = RedactExecutionSecrets(error.Message, executionSecretValues),
                error.WasCancelled,
            }),
            _ => throw new ArgumentOutOfRangeException(nameof(domainEvent)),
        };

    private static string RedactJson(string value, IEnumerable<string> executionSecretValues)
    {
        try
        {
            var valueNode = JsonNode.Parse(value);
            if (valueNode is JsonValue rootValue && rootValue.TryGetValue<string>(out var rootText))
            {
                return JsonSerializer.Serialize(RedactExecutionSecrets(rootText, executionSecretValues));
            }

            RedactJsonNode(valueNode, executionSecretValues);
            return valueNode?.ToJsonString() ?? "null";
        }
        catch (JsonException)
        {
            return RedactExecutionSecrets(value, executionSecretValues);
        }
    }

    private static void RedactJsonNode(JsonNode? node, IEnumerable<string> executionSecretValues)
    {
        if (node is JsonObject obj)
        {
            foreach (var (key, child) in obj.ToArray())
            {
                RedactJsonNode(child, executionSecretValues);
                var redactedKey = RedactExecutionSecrets(key, executionSecretValues);
                if (!string.Equals(redactedKey, key, StringComparison.Ordinal))
                {
                    obj.Remove(key);
                    obj[redactedKey] = child;
                }
            }
        }
        else if (node is JsonArray array)
        {
            for (var index = 0; index < array.Count; index++)
            {
                if (array[index] is JsonValue value && value.TryGetValue<string>(out var text))
                {
                    array[index] = RedactExecutionSecrets(text, executionSecretValues);
                }
                else
                {
                    RedactJsonNode(array[index], executionSecretValues);
                }
            }
        }
        else if (node is JsonValue value && value.TryGetValue<string>(out var text))
        {
            value.ReplaceWith(RedactExecutionSecrets(text, executionSecretValues));
        }
    }

    private static string RedactExecutionSecrets(
        string value,
        IEnumerable<string> executionSecretValues)
    {
        foreach (var secret in executionSecretValues)
        {
            value = value.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
        }

        return value;
    }
}
