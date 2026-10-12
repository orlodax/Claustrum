using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Claustrum.Cli;

namespace Claustrum.Tests.Testing;

/// <summary>
/// What the signal end-to-end classes share (SIGINT #88, SIGTERM/SIGQUIT #92): a repository whose fake backend writes
/// `partial.txt` and then sleeps a length no other case uses, the wait for that file, and the receipt checks a cancelled
/// run owes. A leftover `sleep &lt;length&gt;` is told from another case's, and from the machine's, by that length; every
/// length a case registers is killed again on <see cref="Dispose"/>, because a forced exit may orphan the backend.
/// </summary>
public sealed class CancelScenario : IDisposable
{
    public const string CtrlCLine = "cancelling… press Ctrl-C again to exit without a receipt";
    public const string SigtermLine = "cancelling… send SIGTERM again to exit without a receipt";

    public static readonly TimeSpan StartLimit = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan ExitLimit = TimeSpan.FromSeconds(10);

    private readonly List<string> sleeps = [];
    private IsolatedRepo? repo;

    public IsolatedRepo Repo => repo ?? throw new InvalidOperationException("Prepare the scenario first");

    public void Dispose()
    {
        foreach (string sleep in sleeps)
        {
            foreach (string pid in SleepPids(sleep))
                KillQuietly(pid);
        }

        repo?.Dispose();
    }

    public static string Str(JsonElement element, string name) => element.GetProperty(name).GetString() ?? "";

    public static int Count(string text, string needle) => text.Split(needle).Length - 1;

    /// <summary>Takes ownership of <paramref name="fresh"/> without scripting it, for a case that never reaches the backend.</summary>
    public IsolatedRepo Adopt(IsolatedRepo fresh)
    {
        repo = fresh;
        return fresh;
    }

    /// <summary>Takes ownership of <paramref name="fresh"/> and scripts its fake claude: write `partial.txt`, then `sleep <paramref name="sleep"/>`.</summary>
    public IsolatedRepo Prepare(IsolatedRepo fresh, string sleep)
    {
        Adopt(fresh);
        sleeps.Add(sleep);
        fresh.Script(new FakeClaudeScript { Commands = ["printf 'half\\n' > partial.txt", $"sleep {sleep}"] });
        return fresh;
    }

    /// <summary>A pre-commit hook that sleeps: the cheapest slow cancel path, since the post-run commit runs the repository's hooks.</summary>
    public void PlantHangingPreCommitHook(string hookSleep)
    {
        sleeps.Add(hookSleep);
        string hooks = Directory.CreateDirectory(Path.Combine(Repo.Repo, ".git", "hooks")).FullName;
        string hook = Path.Combine(hooks, "pre-commit");
        File.WriteAllText(hook, $"#!/bin/sh\nsleep {hookSleep}\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    public static string? FindPartial(string root, bool isolated)
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

    public static string[] SleepPids(string duration)
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
            using Process process = Process.GetProcessById(int.Parse(pid, CultureInfo.InvariantCulture));
            process.Kill();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            // Gone already.
        }
    }

    /// <summary>
    /// Starts the binary and waits until the fake backend has written `partial.txt`, so the signal lands on a running backend.
    /// A host that started with the signal ignored (<see cref="RunningCli.IgnoresSignal"/>) could not deliver it: skip.
    /// </summary>
    public async Task<RunningCli> StartAndWaitForPartialAsync(string[] args, bool isolated, int signalNumber, string signalName)
    {
        RunningCli cli = Repo.StartCli(args);
        if (cli.IgnoresSignal(signalNumber))
        {
            cli.Kill();
            Assert.Skip($"this test host was started with SIG{signalName} ignored (a background job of a non-interactive shell), so no SIG{signalName} can reach the child.");
        }

        await cli.WaitUntilAsync(() => FindPartial(Repo.Repo, isolated) is not null, "partial.txt never appeared", StartLimit, TestContext.Current.CancellationToken);
        return cli;
    }

    /// <summary>The one job directory under the scratch home and its result.json.</summary>
    public (string JobId, JsonElement OnDisk) OnlyReceipt()
    {
        string jobId = Assert.Single(Repo.JobDirectories());
        return (jobId, Repo.ReadResult(jobId));
    }

    /// <summary>
    /// Sends <paramref name="signal"/> once and asserts the first-signal outcome: exit 130 within <paramref name="withinMilliseconds"/>
    /// (by default under the 2 s the old handler waited), the stderr <paramref name="cancellingLine"/> exactly once, a `cancelled`
    /// receipt on stdout and on disk, the isolated run's commit holding `partial.txt`, and the backend's own `sleep` gone.
    /// </summary>
    public async Task AssertCancelledAsync(RunningCli cli, string signal, string cancellingLine, string role, string sleep, bool isolated, int withinMilliseconds = 2000)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        await cli.SendSignalAsync(signal, TestContext.Current.CancellationToken);
        int exit = await cli.WaitForExitAsync(ExitLimit, TestContext.Current.CancellationToken);
        stopwatch.Stop();

        // The receipts below are what tell the fixed path from the old one; the clock only says the exit was not a hang.
        Assert.True(stopwatch.ElapsedMilliseconds < withinMilliseconds, $"exit took {stopwatch.ElapsedMilliseconds} ms\nstderr: {cli.Stderr}");
        Assert.Equal(ExitCodes.Cancelled, exit);
        Assert.Equal(1, Count(cli.Stderr, cancellingLine));

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
            Assert.Equal(commit, Repo.Git("rev-parse", $"refs/heads/{branch}"));
            Assert.Equal("partial.txt", Repo.Git("show", "--name-only", "--format=", commit));
        }
        else
        {
            Assert.False(onDisk.TryGetProperty("branch", out JsonElement branch) && branch.ValueKind == JsonValueKind.String, "an in-place run has no branch");
        }

        // The backend's own tree is gone, not orphaned under init still spending.
        Assert.Empty(SleepPids(sleep));
    }
}
