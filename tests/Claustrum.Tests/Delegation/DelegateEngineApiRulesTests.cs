using Claustrum.Core.Config;
using Claustrum.Core.Jobs;
using Claustrum.Core.Model;
using Claustrum.Delegation;
using Claustrum.Tests.Testing;

namespace Claustrum.Tests.Delegation;

// #55 through the engine: an isolated api run inlines the AGENTS.md of the branch it runs (read from its own worktree, fenced
// there), not the dirty copy in the main checkout; an in-place run inlines the main checkout's. No backend is reached: the
// run is a code-reviewer on `api` with no key, and what is read back is the system.md the job directory holds.
[Collection(AppServicesHomeCollectionDefinition.Name)]
public sealed class DelegateEngineApiRulesTests(AppServicesHomeFixture fixture) : IDisposable
{
    private readonly IsolatedRepo repo = new();
    private readonly string outside = Directory.CreateTempSubdirectory("claustrum-api-rules-outside-").FullName;

    public void Dispose()
    {
        repo.Dispose();
        TempTree.Delete(outside);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private DelegateRequest Request(string? branch) => new(
        Role: "code-reviewer",
        Brief: "Review the diff in the working directory.",
        Cwd: repo.Repo,
        Tier: "high",
        Overrides: new ConfigOverrides(Backend: "api", Model: "api:some-model"),
        ResumeSession: null,
        AttachFiles: [],
        Env: [],
        Stream: false,
        DiffCapBytes: 64 * 1024,
        Branch: branch);

    private string SystemPromptOf(RunResult result)
    {
        string job = Path.Combine(JobDirectory.ResolveRoot(fixture.Platform), result.JobId);
        string path = Path.Combine(job, "system.md");
        Assert.True(File.Exists(path), $"no system.md in {job}: {string.Join(", ", Directory.GetFileSystemEntries(job).Select(Path.GetFileName))}\n{result.Error}");
        return File.ReadAllText(path).ReplaceLineEndings("\n");
    }

    private void CommitOnBranch(string branch, string agents)
    {
        repo.Git("checkout", "-q", "-b", branch);
        File.WriteAllText(Path.Combine(repo.Repo, "AGENTS.md"), agents);
        repo.CommitAll("rules on the branch");
        repo.Git("checkout", "-q", "main");
    }

    [Fact]
    public async Task ABranchRunInlinesTheBranchesRulesNotTheDirtyMainCopyAsync()
    {
        CommitOnBranch("feature/x", "BRANCH-RULES\n");
        File.WriteAllText(Path.Combine(repo.Repo, "AGENTS.md"), "MAIN-DIRTY-RULES\n");

        RunResult result = await DelegateEngine.RunAsync(Request("feature/x"), Ct);

        string prompt = SystemPromptOf(result);
        Assert.Contains("BRANCH-RULES", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("MAIN-DIRTY-RULES", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnInPlaceRunInlinesTheMainCheckoutsRulesAsync()
    {
        File.WriteAllText(Path.Combine(repo.Repo, "AGENTS.md"), "MAIN-RULES\n");

        RunResult result = await DelegateEngine.RunAsync(Request(branch: null), Ct);

        Assert.Contains("MAIN-RULES", SystemPromptOf(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACommittedAgentsMdLinkOutIsNotInlinedAndTheReceiptWarnsAsync()
    {
        string secret = Path.Combine(outside, "secret.md");
        File.WriteAllText(secret, "OUTSIDE-SECRET");
        repo.Git("checkout", "-q", "-b", "feature/x");
        TestLinks.Create(Path.Combine(repo.Repo, "AGENTS.md"), secret);
        repo.CommitAll("a link out");
        repo.Git("checkout", "-q", "main");

        RunResult result = await DelegateEngine.RunAsync(Request("feature/x"), Ct);

        Assert.DoesNotContain("OUTSIDE-SECRET", SystemPromptOf(result), StringComparison.Ordinal);
        Assert.Contains(result.Warnings, warning => warning.Contains("AGENTS.md not inlined", StringComparison.Ordinal));
    }
}
