using Claustrum.Cli;
using Claustrum.Coordination;
using Claustrum.Tests.Testing;

namespace Claustrum.Tests.Coordination;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessEnvironmentCollectionDefinition
{
    public const string Name = "process environment";
}

// #104 at coordinate's planning: nothing is created through a `.claustrum` that leaves the repo (the probe, the briefs and
// locks probes), and a symlink tracked under `.claustrum` is refused because the architect's worktree would check it out.
// HEAD decides what is tracked (the worktree is cut from it); the index is read too, since the next commit would add a link.
// A real repository perturbed one way per case (IsolatedRepo.ForCoordinate), the refusal read off the message the operator sees.
[Collection(ProcessEnvironmentCollectionDefinition.Name)]
public sealed class ArchitectWorktreeFenceTests : IDisposable
{
    private const string Outside = "resolves outside the working directory";

    private readonly IsolatedRepo repo = IsolatedRepo.ForCoordinate();
    private readonly string outside = Directory.CreateTempSubdirectory("claustrum-arch-outside-").FullName;

    public void Dispose()
    {
        repo.Dispose();
        TempTree.Delete(outside);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string At(params string[] parts) => Path.Combine([repo.Repo, .. parts]);

    private Task<string[]> ReadyAsync() => ArchitectWorktree.RequireReadyAsync(repo.Repo, repo.Repo, "default", Ct);

    private async Task<string> RefusalAsync()
    {
        CliUsageException refusal = await Assert.ThrowsAsync<CliUsageException>(ReadyAsync);
        return refusal.Message;
    }

    [Theory]
    [InlineData("worktrees")]
    [InlineData("briefs")]
    [InlineData("locks")]
    public async Task AMachineryDirectoryLinkedOutIsRefusedAtPlanningAndNothingIsCreatedThereAsync(string directory)
    {
        TestLinks.CreateDirectoryLink(At(".claustrum", directory), outside);

        string message = await RefusalAsync();

        Assert.Contains(Outside, message, StringComparison.Ordinal);
        Assert.Contains(Path.Combine(".claustrum", directory), message, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFileSystemEntries(outside));
    }

    [Fact]
    public async Task AClaustrumDirectoryLinkedOutIsRefusedAtPlanningAndNothingIsCreatedThereAsync()
    {
        Directory.Delete(At(".claustrum"), recursive: true);
        TestLinks.CreateDirectoryLink(At(".claustrum"), outside);

        string message = await RefusalAsync();

        Assert.Contains(Outside, message, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFileSystemEntries(outside));
    }

    // ---- tracked symlinks under .claustrum -----------------------------------------------------------------------

    private void AddLink(string relative, string target = "/etc/hostname")
    {
        string path = At(relative.Split('/'));
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? repo.Repo);
        TestLinks.Create(path, target);
        repo.Git("add", "-f", relative);
    }

    private void CommitLink(string relative)
    {
        AddLink(relative);
        repo.Git("commit", "-q", "-m", "link");
    }

    [Fact]
    public async Task ACommittedSymlinkUnderClaustrumIsRefusedWithTheWayOutAsync()
    {
        CommitLink(".claustrum/notes/link");

        string message = await RefusalAsync();

        Assert.Contains(".claustrum/notes/link", message, StringComparison.Ordinal);
        Assert.Contains("a symlink committed under .claustrum", message, StringComparison.Ordinal);
        Assert.Contains("`git rm` it and commit", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASymlinkOnlyInTheIndexIsRefusedAsync()
    {
        AddLink(".claustrum/notes/staged");

        Assert.Contains(".claustrum/notes/staged", await RefusalAsync(), StringComparison.Ordinal);
    }

    // The worktree is cut from HEAD, so an uncommitted `git rm` does not make it safe.
    [Fact]
    public async Task AGitRmWithoutACommitIsStillRefusedBecauseHeadHoldsTheLinkAsync()
    {
        CommitLink(".claustrum/notes/link");
        repo.Git("rm", "-q", ".claustrum/notes/link");

        Assert.Contains("`git rm` it and commit", await RefusalAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACommittedRemovalOfTheLinkPassesAsync()
    {
        CommitLink(".claustrum/notes/link");
        repo.Git("rm", "-q", ".claustrum/notes/link");
        repo.Git("commit", "-q", "-m", "remove the link");

        Assert.Empty(await ReadyAsync());
    }

    [Fact]
    public async Task ADifferentlyCasedClaustrumDirectoryIsStillFoundAsync()
    {
        CommitLink(".Claustrum/notes/link");

        Assert.Contains(".Claustrum/notes/link", await RefusalAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASymlinkUnderANameThatOnlyStartsWithClaustrumIsNotRefusedAsync()
    {
        CommitLink(".claustrumx/link");

        Assert.Empty(await ReadyAsync());
    }

    [Fact]
    public async Task ARegularFileUnderClaustrumIsNotATrackedLinkAsync()
    {
        File.WriteAllText(At(".claustrum", "notes.md"), "notes\n");
        repo.Git("add", "-f", ".claustrum/notes.md");
        repo.Git("commit", "-q", "-m", "notes");

        Assert.Empty(await ReadyAsync());
    }

    // A repository with no commit has no HEAD (rev-parse exits 1, an answer): only its index can hold a link.
    [Fact]
    public async Task AnUnbornHeadIsNotAFailureAndAStagedLinkInItIsStillRefusedAsync()
    {
        using ClaustrumCli unborn = new();
        TestGit.Init(unborn.Cwd);
        Directory.CreateDirectory(Path.Combine(unborn.Cwd, ".claustrum", "notes"));
        TestLinks.Create(Path.Combine(unborn.Cwd, ".claustrum", "notes", "link"), "/etc/hostname");
        TestGit.Run(unborn.Cwd, "add", "-f", ".claustrum/notes/link");

        CliUsageException refusal = await Assert.ThrowsAsync<CliUsageException>(
            () => ArchitectWorktree.RequireReadyAsync(unborn.Cwd, unborn.Cwd, "default", Ct));

        Assert.Contains("`git rm` it and commit", refusal.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("failed in", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnbornHeadWithNoLinkGetsPastTheLinkCheckAsync()
    {
        using ClaustrumCli unborn = new();
        TestGit.Init(unborn.Cwd);

        CliUsageException refusal = await Assert.ThrowsAsync<CliUsageException>(
            () => ArchitectWorktree.RequireReadyAsync(unborn.Cwd, unborn.Cwd, "default", Ct));

        Assert.DoesNotContain("`git rm` it and commit", refusal.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("failed in", refusal.Message, StringComparison.Ordinal);
    }

    // The calls are --literal-pathspecs or --no-literal-pathspecs as each needs; the caller's environment must not change the answer.
    [Fact]
    public async Task GitLiteralPathspecsInTheEnvironmentGivesTheSameAnswerAsync()
    {
        CommitLink(".Claustrum/notes/link");
        string? previous = Environment.GetEnvironmentVariable("GIT_LITERAL_PATHSPECS");
        Environment.SetEnvironmentVariable("GIT_LITERAL_PATHSPECS", "1");
        try
        {
            Assert.Contains(".Claustrum/notes/link", await RefusalAsync(), StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GIT_LITERAL_PATHSPECS", previous);
        }
    }
}
