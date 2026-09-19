using Microsoft.Extensions.Logging;

namespace IssueAgent.Observability;

internal static partial class GitLogMessages
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Git operation {Operation} succeeded for {Repository} in {ElapsedMs}ms")]
    public static partial void GitOperationSucceeded(ILogger logger, string operation, string? repository, double elapsedMs);

    [LoggerMessage(EventId = 2, Level = LogLevel.Error, Message = "Git operation {Operation} failed for {Repository} with {ExceptionType}")]
    public static partial void GitOperationFailed(ILogger logger, string exceptionType, string operation, string? repository);

    [LoggerMessage(EventId = 3, Level = LogLevel.Error, Message = "LFS operation {Operation} failed with {ExceptionType}")]
    public static partial void LfsOperationFailed(ILogger logger, string exceptionType, string operation);
}

internal static partial class OmpLogMessages
{
    [LoggerMessage(EventId = 10, Level = LogLevel.Information, Message = "OMP session {OmpSessionId} created for role {Role}")]
    public static partial void SessionCreated(ILogger logger, string ompSessionId, string role);

    [LoggerMessage(EventId = 11, Level = LogLevel.Error, Message = "OMP session creation failed for role {Role} with {ExceptionType}")]
    public static partial void SessionCreationFailed(ILogger logger, string exceptionType, string role);

    [LoggerMessage(EventId = 12, Level = LogLevel.Error, Message = "OMP session {OmpSessionId} resume failed with {ExceptionType}")]
    public static partial void SessionResumeFailed(ILogger logger, string exceptionType, string ompSessionId);

    [LoggerMessage(EventId = 13, Level = LogLevel.Error, Message = "OMP run failed for session {OmpSessionId} with {ExceptionType}")]
    public static partial void RunFailed(ILogger logger, string exceptionType, string ompSessionId);

    [LoggerMessage(EventId = 14, Level = LogLevel.Debug, Message = "OMP event {OmpEventType} for session {OmpSessionId}")]
    public static partial void EventObserved(ILogger logger, string ompEventType, string ompSessionId);

    [LoggerMessage(EventId = 15, Level = LogLevel.Error, Message = "OMP cancellation failed for session {OmpSessionId} with {ExceptionType}")]
    public static partial void CancellationFailed(ILogger logger, string exceptionType, string ompSessionId);

    [LoggerMessage(EventId = 16, Level = LogLevel.Debug, Message = "OMP prompt for session {OmpSessionId}: {Prompt}")]
    public static partial void PromptDispatched(ILogger logger, string ompSessionId, string prompt);

    [LoggerMessage(EventId = 17, Level = LogLevel.Debug, Message = "OMP event payload for session {OmpSessionId}: {Payload}")]
    public static partial void EventPayload(ILogger logger, string ompSessionId, string payload);
}

internal static partial class NotifierLogMessages
{
    [LoggerMessage(EventId = 20, Level = LogLevel.Information, Message = "Notification {NotificationKind} sent for {Repository} #{IssueNumber}")]
    public static partial void NotificationSent(ILogger logger, IssueAgent.Workflow.WorkflowNotificationKind notificationKind, string repository, long issueNumber);

    [LoggerMessage(EventId = 21, Level = LogLevel.Error, Message = "Notification {NotificationKind} failed for {Repository} #{IssueNumber} with {ExceptionType}")]
    public static partial void NotificationFailed(ILogger logger, string exceptionType, IssueAgent.Workflow.WorkflowNotificationKind notificationKind, string repository, long issueNumber);
}

internal static partial class ProviderLogMessages
{
    [LoggerMessage(EventId = 30, Level = LogLevel.Warning, Message = "Provider operation {Operation} failed for {Provider} with {ExceptionType}")]
    public static partial void ProviderOperationFailed(ILogger logger, string exceptionType, string operation, string provider);
}
