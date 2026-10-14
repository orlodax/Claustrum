using System.Runtime.Versioning;
using System.Text.Json;
using Claustrum.Cli;
using Claustrum.Tests.Testing;

namespace Claustrum.Tests.Cli;

// `run --branch` through the built binary: #104 (a `.claustrum` that leaves the repo is exit 2 with one failed document and
// nothing created outside) and #106 (a free whose admin directory could not be deleted is a warning on the receipt, and the run
// goes on).
public sealed class RunBranchFreeEndToEndTests : IDisposable
{
    private readonly IsolatedRepo repo = new();
    private readonly string outside = Directory.CreateTempSubdirectory("claustrum-branch-outside-").FullName;
    private readonly List<string> lockedDirectories = [];

    public void Dispose()
    {
        if (!OperatingSystem.IsWindows())
        {
            foreach (string directory in lockedDirectories.Where(Directory.Exists))
                File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        repo.Dispose();
        TempTree.Delete(outside);
    }

    [Fact]
    public async Task ABranchRunThroughAClaustrumLinkOutIsExitTwoWithOneFailedDocumentAndNothingCreatedOutsideAsync()
    {
        repo.Git("branch", "feature/x");
        TestLinks.CreateDirectoryLink(Path.Combine(repo.Repo, ".claustrum"), outside);

        CliResult process = await repo.RunAsync("run", "builder", "--brief", "do the task", "--json", "--branch", "feature/x");

        Assert.Equal(ExitCodes.Usage, process.ExitCode);
        using JsonDocument document = JsonDocument.Parse(process.Stdout);
        Assert.Equal("failed", document.RootElement.GetProperty("status").GetString());
        Assert.Contains("resolves outside the working directory", document.RootElement.GetProperty("error").GetString(), StringComparison.Ordinal);
        Assert.Empty(Directory.GetFileSystemEntries(outside));
    }

    [Fact]
    public async Task TheHumanFormOfThatRefusalPrintsStatusFailedExitTwoAsync()
    {
        repo.Git("branch", "feature/x");
        TestLinks.CreateDirectoryLink(Path.Combine(repo.Repo, ".claustrum"), outside);

        CliResult process = await repo.RunAsync("run", "builder", "--brief", "do the task", "--branch", "feature/x");

        Assert.Equal(ExitCodes.Usage, process.ExitCode);
        Assert.Contains("status: Failed (exit 2)", process.Stdout.ReplaceLineEndings("\n"), StringComparison.Ordinal);
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public async Task AFreeWhoseAdminDirectoryCouldNotBeDeletedReachesTheReceiptWarningsAndTheRunGoesOnAsync()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("POSIX file modes do not exist on Windows.");

        repo.WriteCast(maxParallel: 2);
        repo.Script(new FakeClaudeScript { Writes = [("first.txt", "first")], Summary = "added first.txt" });
        (CliResult firstProcess, JsonElement first) = await repo.RunBuilderAsync();
        Assert.True(firstProcess.ExitCode == ExitCodes.Ok, firstProcess.Stderr);
        string firstId = first.GetProperty("job_id").GetString() ?? "";
        string branch = first.GetProperty("branch").GetString() ?? "";

        string stuck = Directory.CreateDirectory(Path.Combine(repo.Repo, ".git", "worktrees", firstId, "stuck")).FullName;
        File.WriteAllText(Path.Combine(stuck, "file"), "x");
        lockedDirectories.Add(stuck);
        File.SetUnixFileMode(stuck, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            File.WriteAllText(Path.Combine(stuck, "probe"), "x");
            Assert.Skip("this account is not stopped by a read-only directory (root, or a capability that bypasses modes).");
        }
        catch (UnauthorizedAccessException)
        {
            // Enforced: run the case.
        }

        repo.Script(new FakeClaudeScript { Writes = [("second.txt", "second")], Summary = "added second.txt" });
        (CliResult secondProcess, JsonElement second) = await repo.RunBuilderAsync("--branch", branch);

        Assert.True(secondProcess.ExitCode == ExitCodes.Ok, secondProcess.Stderr);
        Assert.Equal("success", second.GetProperty("status").GetString());
        string[] warnings = [.. second.GetProperty("warnings").EnumerateArray().Select(w => w.GetString() ?? "")];
        Assert.Contains(warnings, warning => warning.Contains("freed, but", StringComparison.Ordinal) && warning.Contains("trash at", StringComparison.Ordinal));
    }
}
