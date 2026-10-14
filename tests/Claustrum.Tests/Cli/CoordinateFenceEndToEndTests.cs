using System.Text.Json;
using Claustrum.Cli;
using Claustrum.Tests.Testing;

namespace Claustrum.Tests.Cli;

// #104 in the architect's own run, through the built binary. Planning already refuses a `.claustrum` that leaves the repo, so
// the only way to meet the run-time fence is a link that appears after planning: a fake `gh` on the child's PATH plants it
// while `--issues` is being imported, which planning does last. The refusal is then exit 2 with exactly one failed RunResult
// document (--json) or `status: Failed (exit 2)` (human), and nothing is created outside. POSIX only: the fake gh is sh.
public sealed class CoordinateFenceEndToEndTests : IDisposable
{
    private readonly IsolatedRepo repo = IsolatedRepo.ForCoordinate();
    private readonly string outside = Directory.CreateTempSubdirectory("claustrum-coord-outside-").FullName;
    private readonly string fakeBin;

    public CoordinateFenceEndToEndTests()
    {
        fakeBin = Directory.CreateDirectory(Path.Combine(repo.Home, "fake-bin")).FullName;
    }

    public void Dispose()
    {
        repo.Dispose();
        TempTree.Delete(outside);
    }

    private string ChildPathWithGhPlanting(string linkName)
    {
        FakeClaude.RequirePosixShell();
        string gh = Path.Combine(fakeBin, "gh");
        File.WriteAllText(gh, $$"""
            #!/bin/sh
            ln -s '{{outside}}' '.claustrum/{{linkName}}'
            echo '{"number":1,"title":"t","body":"b","labels":[],"url":"u"}'
            """.Replace("\r\n", "\n", StringComparison.Ordinal));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(gh, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return fakeBin + Path.PathSeparator + IsolatedRepo.ChildPath();
    }

    [Fact]
    public async Task AWorktreesLinkPlantedAfterPlanningIsExitTwoWithExactlyOneFailedDocumentAsync()
    {
        string path = ChildPathWithGhPlanting("worktrees");
        repo.Script(new FakeClaudeScript());

        CliResult process = await repo.RunWithEnvAsync(["coordinate", "--issues", "1", "--json"], new Dictionary<string, string?>(), path);

        Assert.Equal(ExitCodes.Usage, process.ExitCode);
        // Parse rejects trailing content: a second document on stdout would fail here, so this is "exactly one".
        using JsonDocument document = JsonDocument.Parse(process.Stdout);
        Assert.Equal("failed", document.RootElement.GetProperty("status").GetString());
        Assert.Contains("resolves outside the working directory", document.RootElement.GetProperty("error").GetString(), StringComparison.Ordinal);
        Assert.Empty(Directory.GetFileSystemEntries(outside));
    }

    [Fact]
    public async Task TheHumanFormPrintsStatusFailedExitTwoAsync()
    {
        string path = ChildPathWithGhPlanting("worktrees");
        repo.Script(new FakeClaudeScript());

        CliResult process = await repo.RunWithEnvAsync(["coordinate", "--issues", "1"], new Dictionary<string, string?>(), path);

        Assert.Equal(ExitCodes.Usage, process.ExitCode);
        Assert.Contains("status: Failed (exit 2)", process.Stdout.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        Assert.Contains("resolves outside the working directory", process.Stderr, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFileSystemEntries(outside));
    }

    // The architect itself takes no slot, so a locks link planted late stops nothing in its own run; what matters is that
    // nothing is created through it. (Its children's slot files are fenced by the same engine, DelegateEngineFenceTests.)
    [Fact]
    public async Task ALocksLinkPlantedAfterPlanningCreatesNothingOutsideAsync()
    {
        string path = ChildPathWithGhPlanting("locks");
        repo.Script(new FakeClaudeScript());

        CliResult process = await repo.RunWithEnvAsync(["coordinate", "--issues", "1", "--json"], new Dictionary<string, string?>(), path);

        using JsonDocument document = JsonDocument.Parse(process.Stdout);
        Assert.Equal("success", document.RootElement.GetProperty("status").GetString());
        Assert.Empty(Directory.GetFileSystemEntries(outside));
    }
}
