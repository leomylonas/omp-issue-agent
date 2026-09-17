using LibGit2Sharp;

namespace IssueAgent.Git.Tests;

/// <summary>Creates and cleans up throwaway temporary directories and Git repositories for
/// integration tests. Never touches any shared or real repository.</summary>
internal static class TempGitFixtures
{
    public static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "issueagent-git-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Creates a non-bare "remote" repository with one commit on <c>main</c>, suitable as a
    /// clone source via a local file path.</summary>
    public static string CreateRemoteRepositoryWithCommit(out string firstCommitSha)
    {
        var path = CreateTempDirectory();
        Repository.Init(path);
        using var repo = new Repository(path);
        File.WriteAllText(Path.Combine(path, "README.md"), "# Test repo\n");
        Commands.Stage(repo, "README.md");
        var signature = new Signature("Test", "test@example.com", DateTimeOffset.UtcNow);
        var commit = repo.Commit("Initial commit", signature, signature);
        firstCommitSha = commit.Sha;

        if (repo.Head.FriendlyName != "main")
        {
            repo.Refs.Rename(repo.Head.Reference, "refs/heads/main");
        }

        return path;
    }

    /// <summary>Creates a bare "remote" repository (able to receive pushes) seeded with one commit
    /// on <c>main</c>, by cloning a scratch non-bare repository.</summary>
    public static string CreateBareRemoteRepository(out string firstCommitSha)
    {
        var workingPath = CreateRemoteRepositoryWithCommit(out firstCommitSha);
        var barePath = CreateTempDirectory();
        Directory.Delete(barePath);
        Repository.Clone(workingPath, barePath, new CloneOptions { IsBare = true });
        return barePath;
    }

    public static GitAuthentication AnonymousAuthentication() => GitAuthentication.Anonymous(TlsTrust.System);

    public static readonly GitIdentity TestIdentity = new("Test", "test@example.com");
}
