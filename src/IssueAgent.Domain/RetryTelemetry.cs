using System.Diagnostics.Metrics;

namespace IssueAgent.Domain;

/// <summary>Process-wide retry instruments shared by all retry boundaries.</summary>
public static class RetryTelemetry
{
    private static readonly Meter Meter = new("IssueAgent", "1.0.0");
    private static readonly Counter<long> RetryAttempts = Meter.CreateCounter<long>(
        "issueagent.retry.attempts",
        description: "Retry attempts after a transient boundary failure.");
    private static readonly Counter<long> RetryExhausted = Meter.CreateCounter<long>(
        "issueagent.retry.exhausted",
        description: "Operations whose transient retry budget was exhausted.");

    public static void RecordAttempt() => RetryAttempts.Add(1);

    public static void RecordExhausted() => RetryExhausted.Add(1);
}
