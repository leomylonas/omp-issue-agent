using System.Globalization;
using System.Text.RegularExpressions;
using IssueAgent.Domain;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace IssueAgent.Context;

/// <summary>The exact YAML shape written into the canonical comment's machine-state section
/// (specification §26). Field names and casing are part of the durable on-disk/remote contract;
/// do not rename without a version bump.</summary>
public sealed record CanonicalStateDocument
{
    public int Version { get; init; } = 1;
    public required string WorkflowId { get; init; }
    public required string Phase { get; init; }
    public required string State { get; init; }
    public string? WaitingReason { get; init; }
    public required int PlanRevision { get; init; }
    public int? ApprovedPlanRevision { get; init; }
    public string? PlanInputHash { get; init; }
    public required string OmpSessionId { get; init; }
    public string? OmpSessionFile { get; init; }
    public required string Branch { get; init; }
    public required string TargetBranch { get; init; }
    public required string BaseCommit { get; init; }
    public string? PullOrMergeRequest { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>Serializes <see cref="DateTimeOffset"/> as a round-trippable ISO-8601 scalar instead of
/// YamlDotNet's default reflected-struct representation.</summary>
internal sealed class DateTimeOffsetYamlConverter : IYamlTypeConverter
{
    public bool Accepts(Type type) => type == typeof(DateTimeOffset);

    public object ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
    {
        var scalar = parser.Consume<Scalar>();
        return DateTimeOffset.Parse(scalar.Value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    }

    public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
    {
        var dateTimeOffset = (DateTimeOffset)value!;
        emitter.Emit(new Scalar(dateTimeOffset.ToString("O", CultureInfo.InvariantCulture)));
    }
}

/// <summary>Converts between the domain <see cref="WorkflowState"/> and the durable YAML document,
/// and serializes/deserializes that document to/from the exact text stored in the canonical comment.</summary>
public static partial class CanonicalStateSerializer
{
    private static readonly ISerializer Serializer = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .WithTypeConverter(new DateTimeOffsetYamlConverter())
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
        .Build();

    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .WithTypeConverter(new DateTimeOffsetYamlConverter())
        .IgnoreUnmatchedProperties()
        .Build();

    public static CanonicalStateDocument ToDocument(WorkflowState state, string? pullOrMergeRequest) => new()
    {
        Version = 1,
        WorkflowId = state.WorkflowId.ToString(),
        Phase = ToKebabCase(state.Phase.ToString()),
        State = ToKebabCase(state.OperationalState.ToString()),
        WaitingReason = state.WaitingReason is { } reason ? ToKebabCase(reason.ToString()) : null,
        PlanRevision = state.PlanRevision,
        ApprovedPlanRevision = state.ApprovedPlanRevision,
        PlanInputHash = state.PlanInputHash,
        OmpSessionId = state.OmpSessionId,
        OmpSessionFile = state.OmpSessionFile,
        Branch = state.Branch,
        TargetBranch = state.TargetBranch,
        BaseCommit = state.BaseCommit,
        PullOrMergeRequest = pullOrMergeRequest,
        UpdatedAt = state.UpdatedAt,
    };

    public static WorkflowState ToWorkflowState(CanonicalStateDocument document)
    {
        if (document.Version != 1)
        {
            throw new CanonicalStateException($"Unsupported canonical state version {document.Version}.");
        }

        if (!Guid.TryParse(document.WorkflowId, out var workflowIdValue))
        {
            throw new CanonicalStateException($"Canonical state workflowId '{document.WorkflowId}' is not a valid identifier.");
        }

        var branch = ValidateBranchName(document.Branch, "branch");
        var targetBranch = ValidateBranchName(document.TargetBranch, "targetBranch");
        var baseCommit = ValidateCommitSha(document.BaseCommit);

        return new WorkflowState(
            new WorkflowId(workflowIdValue),
            ParseEnum<WorkflowPhase>(document.Phase, "phase"),
            ParseEnum<WorkflowOperationalState>(document.State, "state"),
            document.WaitingReason is { } reason ? ParseEnum<WaitingReason>(reason, "waitingReason") : null,
            document.PlanRevision,
            document.ApprovedPlanRevision,
            document.OmpSessionId,
            branch,
            targetBranch,
            baseCommit,
            document.UpdatedAt,
            document.PlanInputHash,
            document.OmpSessionFile);
    }

    [GeneratedRegex(@"^[A-Za-z0-9]([A-Za-z0-9._/-]*[A-Za-z0-9])?$")]
    private static partial Regex SafeBranchNamePattern();

    [GeneratedRegex(@"^[0-9a-fA-F]{4,64}$")]
    private static partial Regex CommitShaPattern();

    /// <summary>Rejects a branch name that does not match a safe shape before it can ever reach a
    /// git/git-lfs command line (specification §10, §11): a maintainer-editable canonical comment
    /// must never be able to inject an option into a remote-touching git invocation.</summary>
    private static string ValidateBranchName(string value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.StartsWith('-') ||
            value.Contains("..", StringComparison.Ordinal) ||
            value.Any(char.IsWhiteSpace) ||
            !SafeBranchNamePattern().IsMatch(value))
        {
            throw new CanonicalStateException($"Canonical state field '{fieldName}' has an unsafe value.");
        }

        return value;
    }

    private static string ValidateCommitSha(string value)
    {
        if (!CommitShaPattern().IsMatch(value))
        {
            throw new CanonicalStateException("Canonical state field 'baseCommit' is not a valid commit SHA.");
        }

        return value;
    }

    public static string Serialize(CanonicalStateDocument document) => Serializer.Serialize(document).TrimEnd();

    public static CanonicalStateDocument Deserialize(string yaml)
    {
        try
        {
            return Deserializer.Deserialize<CanonicalStateDocument>(yaml)
                ?? throw new CanonicalStateException("Canonical state YAML deserialized to an empty document.");
        }
        catch (YamlDotNet.Core.YamlException ex)
        {
            throw new CanonicalStateException($"Canonical state YAML is malformed: {ex.Message}", ex);
        }
    }

    private static string ToKebabCase(string pascalCase)
    {
        if (pascalCase.Length == 0)
        {
            return pascalCase;
        }

        var builder = new System.Text.StringBuilder();
        for (var i = 0; i < pascalCase.Length; i++)
        {
            var c = pascalCase[i];
            if (i > 0 && char.IsUpper(c))
            {
                builder.Append('-');
            }

            builder.Append(char.ToLowerInvariant(c));
        }

        return builder.ToString();
    }

    private static TEnum ParseEnum<TEnum>(string kebabCaseValue, string fieldName) where TEnum : struct, Enum
    {
        var pascalCase = string.Concat(kebabCaseValue.Split('-').Select(part =>
            part.Length == 0 ? part : char.ToUpperInvariant(part[0]) + part[1..]));

        if (!Enum.TryParse<TEnum>(pascalCase, out var value))
        {
            throw new CanonicalStateException($"Canonical state field '{fieldName}' has unrecognized value '{kebabCaseValue}'.");
        }

        return value;
    }
}

public sealed class CanonicalStateException : Exception
{
    public CanonicalStateException(string message) : base(message)
    {
    }

    public CanonicalStateException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
