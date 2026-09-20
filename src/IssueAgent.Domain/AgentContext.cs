namespace IssueAgent.Domain;

/// <summary>Provider-neutral context passed to OMP for one workflow operation.</summary>
public sealed record AgentContext(
    IssueContext PrimaryIssue,
    IReadOnlyList<RelatedIssueContext> RelatedIssues,
    PlanContext? CurrentPlan,
    MergeRequestContext? PullOrMergeRequest,
    WorkflowState WorkflowState);

/// <summary>Issue content normalized from a provider without provider SDK types.</summary>
public sealed record IssueContext(
    string RepositoryId,
    long Number,
    string Title,
    string Description,
    IReadOnlyList<string> Labels,
    IReadOnlyList<HumanComment> HumanComments,
    IReadOnlyList<AttachmentReference> Attachments);

/// <summary>Relationship path and read-only context for an issue related to the primary issue.</summary>
public sealed record RelatedIssueContext(
    IReadOnlyList<string> RelationshipPath,
    IssueContext Issue);

/// <summary>A human-authored issue, merge-request, or review comment.</summary>
public sealed record HumanComment(
    string Author,
    DateTimeOffset CreatedAt,
    string Body,
    string? ThreadId = null,
    bool IsResolved = false,
    DateTimeOffset? UpdatedAt = null,
    long? CommentId = null);

/// <summary>Downloaded attachment metadata and its safe local location outside the worktree.</summary>
public sealed record AttachmentReference(
    string SourceUrl,
    string SafeFileName,
    string LocalPath,
    string Provenance,
    long SizeBytes,
    bool IsOmitted = false,
    string? OmissionReason = null);

/// <summary>Current authoritative implementation plan and its decision record.</summary>
public sealed record PlanContext(
    int Revision,
    string Text,
    IReadOnlyList<string> DecisionsAndRationale);

/// <summary>Provider-neutral pull or merge request context.</summary>
public sealed record MergeRequestContext(
    long Number,
    string Description,
    IReadOnlyList<HumanComment> Comments,
    IReadOnlyList<HumanComment> ReviewThreads,
    IReadOnlyList<AttachmentReference> Attachments);
