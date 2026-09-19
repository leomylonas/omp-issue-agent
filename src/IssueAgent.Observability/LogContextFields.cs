namespace IssueAgent.Observability;

/// <summary>Structured logging property names (specification §29), so every operation for one
/// issue/workflow can be queried together in Loki/Grafana regardless of which component emitted
/// the log line.</summary>
public static class LogContextFields
{
    public const string Provider = "Provider";
    public const string Repository = "Repository";
    public const string IssueNumber = "IssueNumber";
    public const string PullOrMergeRequestNumber = "PullOrMergeRequestNumber";
    public const string WorkflowId = "WorkflowId";
    public const string CorrelationId = "CorrelationId";
    public const string OmpSessionId = "OmpSessionId";
    public const string Operation = "Operation";
    public const string Component = "Component";
    public const string OmpEventType = "OmpEventType";
}
