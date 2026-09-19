using System.Security.Cryptography;
using System.Text;

namespace IssueAgent.Workflow;

/// <summary>Computes a content hash over the issue title and description the currently published
/// plan was written against (specification §22 "Title/description edits after planning invalidate
/// the plan"). Comparing this hash instead of a generic timestamp avoids false staleness from
/// IssueAgent's own label/comment writes, which bump the provider's issue <c>updated_at</c> without
/// changing title or description.</summary>
public static class PlanInputHasher
{
    public static string Compute(string title, string description)
    {
        var payload = Encoding.UTF8.GetBytes(title + "\u0000" + description);
        return Convert.ToHexStringLower(SHA256.HashData(payload));
    }
}
