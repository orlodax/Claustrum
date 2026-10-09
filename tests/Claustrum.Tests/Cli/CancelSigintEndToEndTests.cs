using System.Diagnostics;
using System.Text.Json;
using Claustrum.Cli;
using Claustrum.Tests.Testing;

namespace Claustrum.Tests.Cli;

// #88 through the real built binary: a SIGINT (what a terminal's Ctrl-C delivers) reaches Runner, which kills the
// backend's tree and writes a `cancelled` receipt, instead of System.CommandLine's own handler ending the process
// after its 2 s ProcessTerminationTimeout with no result.json and the child still spending (NOTES.md "Ctrl-C reaches
// the runner"). The fake backend writes partial.txt and then sleeps; nothing here reaches a real backend: the child's
// PATH is git's directory plus the system's, and `claustrum.json` committed at the git root points `claude` at the script.
// Each case sleeps a different, odd length so a leftover `sleep` can be told from another case's, and from the machine's.
public sealed class CancelSigintEndToEndTests : IDisposable
{
    private static readonly TimeSpan startLimit = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan exitLimit = TimeSpan.FromSeconds(10);
    private const string CancellingLine = "cancelling… press Ctrl-C again to exit without a receipt";

    private readonly List<string> sleeps = [];
    private IsolatedRepo? repo;

    public void Dispose()
    {
        foreach (string sleep in sleeps)
        {
            foreach (string pid in SleepPids(sleep))
                KillQuietly(pid);
        }

        repo?.Dispose();
    }

    private static void RequireSigint()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("SIGINT is a POSIX signal; a Windows console's Ctrl-C cannot be sent to a redirected child from here.");
    }

    private static string Str(JsonElement element, string name) => element.GetProperty(name).GetString() ?? "";

    private IsolatedRepo Prepare(IsolatedRepo fresh, string sleep)
    {
        repo = fresh;
        sleeps.Add(sleep);
        fresh.Script(new FakeClaudeScript { Commands = ["printf 'half\\n' > partial.txt", $"sleep {sleep}"] });
        return fresh;
    }

    private static string? FindPartial(string root, bool isolated)
    {
        if (!isolated)
        {
            string path = Path.Combine(root, "partial.txt");
            return File.Exists(path) ? path : null;
        }

        string worktrees = Path.Combine(root, ".claustrum", "worktrees");
        return Directory.Exists(worktrees)
            ? Directory.GetDirectories(worktrees).Select(dir => Path.Combine(dir, "partial.txt")).FirstOrDefault(File.Exists)
            : null;
    }

    private static string[] SleepPids(string duration)
    {
        ProcessStartInfo startInfo = new() { FileName = "ps", UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
        startInfo.ArgumentList.Add("-A");
        startInfo.ArgumentList.Add("-o");
        startInfo.ArgumentList.Add("pid=,args=");
        using Process ps = Process.Start(startInfo) ?? throw new InvalidOperationException("ps failed to start");
        string output = ps.StandardOutput.ReadToEnd();
        ps.WaitForExit();
        return [.. output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.EndsWith($" sleep {duration}", StringComparison.Ordinal))
            .Select(line => line[..line.IndexOf(' ', StringComparison.Ordinal)])];
    }

    private static void KillQuietly(string pid)
    {
        try
        {
            using Process process = Process.GetProcessById(int.Parse(pid, System.Globalization.CultureInfo.InvariantCulture));
            process.Kill();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            // Gone already.
        }
    }

    private async Task<RunningCli> StartAndWaitForPartialAsync(string[] args, bool isolated)
    {
        RunningCli cli = repo!.StartCli(args);
        if (cli.IgnoresSigint())
        {
            cli.Kill();
            Assert.Skip("this test host was started with SIGINT ignored (a background job of a non-interactive shell), so no SIGINT can reach the child.");
        }

        await cli.WaitUntilAsync(() => FindPartial(repo.Repo, isolated) is not null, "partial.txt never appeared", startLimit, TestContext.Current.CancellationToken);
        return cli;
    }

    private static int Count(string text, string needle) => text.Split(needle).Length - 1;

    /// <summary>The one job directory under the scratch home and its result.json.</summary>
    private (string JobId, JsonElement OnDisk) OnlyReceipt()
    {
        string jobId = Assert.Single(repo!.JobDirectories());
        return (jobId, repo.ReadResult(jobId));
    }

    private async Task AssertCancelledAsync(RunningCli cli, string role, string sleep, bool isolated)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        await cli.SendSigintAsync(TestContext.Current.CancellationToken);
        int exit = await cli.WaitForExitAsync(exitLimit, TestContext.Current.CancellationToken);
        stopwatch.Stop();

        // Under the 2 s the old handler waited, with room for a slow machine: the receipts below are what tell them apart.
        Assert.True(stopwatch.ElapsedMilliseconds < 2000, $"exit took {stopwatch.ElapsedMilliseconds} ms\nstderr: {cli.Stderr}");
        Assert.Equal(ExitCodes.Cancelled, exit);
        Assert.Equal(1, Count(cli.Stderr, CancellingLine));

        using JsonDocument stdout = JsonDocument.Parse(cli.Stdout);
        JsonElement printed = stdout.RootElement;
        (string jobId, JsonElement onDisk) = OnlyReceipt();
        Assert.Equal("cancelled", Str(printed, "status"));
        Assert.Equal("cancelled", Str(onDisk, "status"));
        Assert.Equal(jobId, Str(printed, "job_id"));
        Assert.Equal(role, Str(onDisk, "role"));

        if (isolated)
        {
            string branch = $"claustrum/{jobId}";
            Assert.Equal(branch, Str(printed, "branch"));
            Assert.Equal(branch, Str(onDisk, "branch"));
            Assert.EndsWith(IsolatedRepo.WorktreeTail(jobId), Str(onDisk, "worktree"), StringComparison.OrdinalIgnoreCase);
            string commit = Str(onDisk, "commit");
            Assert.Equal(commit, Str(printed, "commit"));
            Assert.Equal(commit, repo!.Git("rev-parse", $"refs/heads/{branch}"));
            Assert.Equal("partial.txt", repo.Git("show", "--name-only", "--format=", commit));
        }
        else
        {
            Assert.False(onDisk.TryGetProperty("branch", out JsonElement branch) && branch.ValueKind == JsonValueKind.String, "an in-place run has no branch");
        }

        // The backend's own tree is gone, not orphaned under init still spending.
        Assert.Empty(SleepPids(sleep));
    }

    [Fact]
    public async Task SigintOnAnIsolatedRunEndsItCancelledWithACommitHoldingTheHalfWrittenFileAsync()
    {
        RequireSigint();
        const string sleep = "31.71";
        Prepare(new IsolatedRepo(), sleep).WriteCast(maxParallel: 2);
        using RunningCli cli = await StartAndWaitForPartialAsync(["run", "builder", "--brief", "do the task", "--json"], isolated: true);

        await AssertCancelledAsync(cli, "builder", sleep, isolated: true);
    }

    [Fact]
    public async Task SigintOnAnInPlaceRunEndsItCancelledWithItsReceiptOnDiskAsync()
    {
        RequireSigint();
        const string sleep = "31.72";
        Prepare(new IsolatedRepo(), sleep);
        using RunningCli cli = await StartAndWaitForPartialAsync(["run", "builder", "--brief", "do the task", "--json"], isolated: false);

        await AssertCancelledAsync(cli, "builder", sleep, isolated: false);

        // In place there is no worktree and no branch: the half-written file is where the backend left it.
        Assert.True(File.Exists(Path.Combine(repo!.Repo, "partial.txt")));
        Assert.Equal("main", repo.Git("branch", "--show-current"));
    }

    [Fact]
    public async Task SigintOnCoordinateEndsTheArchitectCancelledWithACommitOnItsOwnBranchAsync()
    {
        RequireSigint();
        const string sleep = "31.73";
        Prepare(IsolatedRepo.ForCoordinate(), sleep);
        using RunningCli cli = await StartAndWaitForPartialAsync(["coordinate", "--brief", "do the task", "--json"], isolated: true);

        await AssertCancelledAsync(cli, "architect", sleep, isolated: true);
    }

    // The escape hatch: with ProcessTerminationTimeout null nothing else ends a cancel path that hangs, and the post-run
    // commit runs the repository's hooks. A pre-commit hook that sleeps is the cheapest slow cancel path; the second SIGINT
    // must end the process at once, with no receipt (the job stays `pending`, as it would after any hard kill).
    [Fact]
    public async Task ASecondSigintEndsARunWhoseCancelPathHangsWithoutAReceiptAsync()
    {
        RequireSigint();
        const string sleep = "31.74";
        const string hookSleep = "31.75";
        Prepare(new IsolatedRepo(), sleep).WriteCast(maxParallel: 2);
        sleeps.Add(hookSleep);
        string hook = Path.Combine(repo!.Repo, ".git", "hooks", "pre-commit");
        Directory.CreateDirectory(Path.GetDirectoryName(hook)!);
        File.WriteAllText(hook, $"#!/bin/sh\nsleep {hookSleep}\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        using RunningCli cli = await StartAndWaitForPartialAsync(["run", "builder", "--brief", "do the task", "--json"], isolated: true);

        await cli.SendSigintAsync(TestContext.Current.CancellationToken);
        await cli.WaitUntilAsync(() => cli.Stderr.Contains(CancellingLine, StringComparison.Ordinal), "no cancelling line", exitLimit, TestContext.Current.CancellationToken);
        await cli.WaitUntilAsync(() => SleepPids(hookSleep).Length > 0, "the pre-commit hook never started", startLimit, TestContext.Current.CancellationToken);
        Assert.False(cli.HasExited, "the first SIGINT alone must wait for the hanging commit");

        Stopwatch stopwatch = Stopwatch.StartNew();
        await cli.SendSigintAsync(TestContext.Current.CancellationToken);
        int exit = await cli.WaitForExitAsync(exitLimit, TestContext.Current.CancellationToken);
        stopwatch.Stop();

        Assert.True(stopwatch.ElapsedMilliseconds < 2000, $"the forced exit took {stopwatch.ElapsedMilliseconds} ms");
        Assert.NotEqual(0, exit);
        Assert.Equal(1, Count(cli.Stderr, CancellingLine));
        string jobId = Assert.Single(repo.JobDirectories());
        Assert.False(File.Exists(Path.Combine(repo.JobsRoot, jobId, "result.json")), "a forced exit writes no receipt");
        Assert.Equal("", cli.Stdout.Trim());
    }
}
