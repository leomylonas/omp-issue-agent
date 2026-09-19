using IssueAgent.Context;
using IssueAgent.Providers;

namespace IssueAgent.Workflow.Tests;

public sealed class CanonicalCommentLocatorTests
{
    [Fact]
    public async Task FindAsyncThrowsCorruptionWhenMultipleCommentsContainLocatorMarker()
    {
        var provider = new FakeGitProvider();
        var repository = new RepositoryRef("repo-id", "owner", "repo");
        provider.AddComment(repository, 1, "issue-agent", CanonicalCommentMarkdown.StateLocatorMarker, DateTimeOffset.UnixEpoch);
        provider.AddComment(repository, 1, "issue-agent", CanonicalCommentMarkdown.StateLocatorMarker, DateTimeOffset.UnixEpoch.AddMinutes(1));

        await Assert.ThrowsAsync<CanonicalCommentCorruptException>(() =>
            CanonicalCommentLocator.FindAsync(provider, repository, 1, CancellationToken.None));
    }
}
