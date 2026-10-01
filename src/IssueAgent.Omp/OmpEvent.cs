namespace IssueAgent.Omp;

/// <summary>Base type for structured OMP events streamed during a run. Distinguishing by pattern
/// matching on the concrete record type mirrors the wire protocol's discriminated event shapes.</summary>
public abstract record OmpEvent(string SessionId, DateTimeOffset Timestamp);

/// <summary>Free-form progress or reasoning text from OMP, for structured logging at Debug/Verbose.</summary>
public sealed record OmpMessageEvent(string SessionId, DateTimeOffset Timestamp, string Text) : OmpEvent(SessionId, Timestamp);

/// <summary>OMP invoked a tool/command. <see cref="ToolCallId"/> correlates with the matching
/// <see cref="OmpToolResultEvent"/>.</summary>
public sealed record OmpToolCallEvent(string SessionId, DateTimeOffset Timestamp, string ToolCallId, string ToolName, string ArgumentsJson)
    : OmpEvent(SessionId, Timestamp);

/// <summary>The result of a previously reported tool call.</summary>
public sealed record OmpToolResultEvent(string SessionId, DateTimeOffset Timestamp, string ToolCallId, bool IsError, string ResultJson)
    : OmpEvent(SessionId, Timestamp);

/// <summary>The run completed successfully. <see cref="ResultJson"/> carries the role-specific
/// structured result (plan, implementation summary, etc.); callers deserialize it per role.</summary>
public sealed record OmpCompletedEvent(string SessionId, DateTimeOffset Timestamp, string ResultJson) : OmpEvent(SessionId, Timestamp);

/// <summary>The run failed or was cancelled before completion. <see cref="ErrorCode"/> preserves
/// OMP's machine-readable dependency classification for workflow retry handling.</summary>
public sealed record OmpErrorEvent(
    string SessionId,
    DateTimeOffset Timestamp,
    string Message,
    bool WasCancelled,
    string? ErrorCode = null) : OmpEvent(SessionId, Timestamp);
