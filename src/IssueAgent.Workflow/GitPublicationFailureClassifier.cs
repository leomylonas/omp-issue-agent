using IssueAgent.Domain;

namespace IssueAgent.Workflow;

/// <summary>Classifies known remote publication rejections without exposing transport diagnostics to users.</summary>
internal static class GitPublicationFailureClassifier
{
    public static WaitingReason? TryClassify(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var message = exception.Message;
        if (ContainsAny(message, "protected branch", "protected ref", "protected branches", "GH006", "pre-receive hook declined"))
        {
            return WaitingReason.ProtectedBranch;
        }

        if (ContainsAny(message, "authentication failed", "authentication required", "could not read username", "terminal prompts disabled", "permission denied (publickey)", "credentials", "access denied"))
        {
            return WaitingReason.MissingCredentials;
        }

        return null;
    }

    public static string Explanation(WaitingReason reason) => reason switch
    {
        WaitingReason.MissingCredentials => "Git publication could not authenticate to the remote. Configure the required Git credentials, then continue.",
        WaitingReason.ProtectedBranch => "Git publication was rejected by remote branch protection. Adjust the branch policy or use an authorized credential, then continue.",
        _ => throw new ArgumentOutOfRangeException(nameof(reason)),
    };

    private static bool ContainsAny(string value, params string[] candidates) =>
        candidates.Any(candidate => value.Contains(candidate, StringComparison.OrdinalIgnoreCase));
}
