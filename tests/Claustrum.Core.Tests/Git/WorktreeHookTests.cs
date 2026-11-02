using System.Diagnostics;
using Claustrum.Core.Git;
using Claustrum.Core.Model;
using Claustrum.Core.Process;
using Claustrum.Core.Tests.Testing;

namespace Claustrum.Core.Tests.Git;

// #146: the user config's `projects.<glob>.worktree_hook`: an argv, never a shell string, with `{worktree}` put into every argument, run in the
// worktree under the backends' env allow-list. A hook that fails is a warning and a worktree_hook_failed issue and the job runs anyway; a cancel
// propagates. These run real processes (`sh` on Unix, `cmd` on Windows, whose forms below were not run on a Windows host in this pass).
// The hook's 120 s timeout is a private constant with no seam: the mechanism under it (CommandProcess bounding the reads too) is tested
// below with a short timeout, and the 120 s itself is not.
public sealed class WorktreeHookTests : IDisposable
{
    private readonly ScratchRoot scratch = new("claustrum-hook-");
    private readonly string worktree;
    private readonly HomeRedirectPlatform platform;

    public WorktreeHookTests()
    {
        worktree = scratch.CreateDirectory();
        platform = new HomeRedirectPlatform(scratch.CreateDirectory());
    }

    public void Dispose() => scratch.Dispose();

    private static string[] Sh(string unix, string windows, params string[] args) =>
        OperatingSystem.IsWindows() ? ["cmd", "/c", windows, .. args] : ["sh", "-c", unix, "sh", .. args];

    private Task<(string[] Warnings, EnvironmentIssue[] Issues)> Run(params string[][] hooks) =>
        WorktreeHook.RunAsync(hooks, worktree, platform, envPassthroughAll: false, CancellationToken.None);

    private string Out(string name) => File.ReadAllText(Path.Combine(worktree, name)).Trim();

    [Fact]
    public async Task EveryArgumentGetsTheWorktreePathAndTheHookRunsInsideTheWorktreeAsync()
    {
        (string[] warnings, EnvironmentIssue[] issues) = await Run(Sh("printf '%s|%s' \"$1\" \"$2\" > hook.out", "echo {worktree}+x-{worktree}-y> hook.out", "{worktree}", "x-{worktree}-y"));

        Assert.Empty(warnings);
        Assert.Empty(issues);
        if (!OperatingSystem.IsWindows())
            Assert.Equal($"{worktree}|x-{worktree}-y", Out("hook.out"));
        else
            Assert.Contains(worktree, Out("hook.out"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASuccessfulHookLeavesNothingOnTheReceiptAsync()
    {
        (string[] warnings, EnvironmentIssue[] issues) = await Run(Sh("exit 0", "exit 0"));

        Assert.Empty(warnings);
        Assert.Empty(issues);
    }

    [Fact]
    public async Task ANonZeroExitIsAWarningAndAnIssueNamingTheCommandTheCodeAndTheFirstLineOfStderrAsync()
    {
        string[] hook = Sh("echo boom >&2; echo second >&2; exit 3", "echo boom 1>&2 & exit 3");

        (string[] warnings, EnvironmentIssue[] issues) = await Run(hook);

        string command = string.Join(' ', hook);
        EnvironmentIssue issue = Assert.Single(issues);
        Assert.Equal(EnvironmentIssueKind.WorktreeHookFailed, issue.Kind);
        Assert.Equal("exited 3: boom", issue.What);
        Assert.Equal(command, issue.Where);
        Assert.Equal(1, issue.Count);
        Assert.Equal("dispatch", issue.First);
        Assert.Equal($"worktree_hook failed: {command}: exited 3: boom; the job runs anyway", Assert.Single(warnings));
    }

    [Fact]
    public async Task ANonZeroExitWithNothingOnStderrSaysOnlyTheCodeAsync()
    {
        (_, EnvironmentIssue[] issues) = await Run(Sh("exit 4", "exit 4"));

        Assert.Equal("exited 4", Assert.Single(issues).What);
    }

    [Fact]
    public async Task AProgramNotOnPathIsAnIssueAndTheNextHookStillRunsAsync()
    {
        string[] missing = ["claustrum-no-such-program-xyz", "{worktree}"];

        (string[] warnings, EnvironmentIssue[] issues) = await Run(missing, Sh("echo ran > second.out", "echo ran> second.out"));

        EnvironmentIssue issue = Assert.Single(issues);
        Assert.Equal("claustrum-no-such-program-xyz not found on PATH", issue.What);
        Assert.Contains($"claustrum-no-such-program-xyz {worktree}", issue.Where, StringComparison.Ordinal);
        Assert.Single(warnings);
        Assert.Equal("ran", Out("second.out"));
    }

    [Fact]
    public async Task EveryMatchingHookRunsInOrderEvenAfterAFailedOneAsync()
    {
        (string[] warnings, EnvironmentIssue[] issues) = await Run(
            Sh("echo one >> order.out", "echo one>> order.out"),
            Sh("echo two >> order.out; exit 9", "echo two>> order.out & exit 9"),
            Sh("echo three >> order.out", "echo three>> order.out"));

        Assert.Single(warnings);
        Assert.Equal("exited 9", Assert.Single(issues).What);
        Assert.Equal(["one", "two", "three"], File.ReadAllLines(Path.Combine(worktree, "order.out")).Select(line => line.Trim()));
    }

    // .NET looks for a bare name in the current directory before PATH on Unix, and a cloned repo matching the glob could commit an executable
    // `chcon` there. The hook's bare name is resolved on PATH only (BinaryLocator), so the planted program is never started.
    [Fact]
    public async Task ABareProgramNameIsResolvedOnPathOnlyAndNeverFromTheProcessCurrentDirectoryAsync()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("the planted-program case is written for a Unix executable bit.");

        string name = $"claustrum-hook-probe-{Guid.NewGuid():N}";
        string planted = Path.Combine(Directory.GetCurrentDirectory(), name);
        string marker = Path.Combine(worktree, "planted-ran");
        File.WriteAllText(planted, $"#!/bin/sh\n: > '{marker}'\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(planted, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            (_, EnvironmentIssue[] issues) = await Run([name]);

            Assert.Equal($"{name} not found on PATH", Assert.Single(issues).What);
            Assert.False(File.Exists(marker), "the executable planted in the process's current directory was run");
        }
        finally
        {
            File.Delete(planted);
        }
    }

    [Fact]
    public async Task AnAbsolutePathIsUsedAsIsAsync()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("written for /bin/sh.");

        (string[] warnings, EnvironmentIssue[] issues) = await Run(["/bin/sh", "-c", "echo abs > abs.out"]);

        Assert.Empty(warnings);
        Assert.Empty(issues);
        Assert.Equal("abs", Out("abs.out"));
    }

    // ---- the environment: EnvAllowList, the same as a backend's child ------------------------------------------------------------------------

    [Fact]
    public async Task TheHookSeesAllowListedVariablesAndNotTheRestUnlessPassthroughIsAllAsync()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("written with POSIX parameter expansion.");

        string secret = $"CLAUSTRUM_TEST_HOOK_SECRET_{Guid.NewGuid():N}";
        const string visible = "OPENCODE_TEST_HOOK_VISIBLE";
        Environment.SetEnvironmentVariable(secret, "s3cret");
        Environment.SetEnvironmentVariable(visible, "shown");
        try
        {
            string[] hook = ["sh", "-c", $"printf '%s|%s' \"${secret}\" \"${visible}\" > env.out"];

            await Run(hook);
            string filtered = Out("env.out");
            await WorktreeHook.RunAsync([hook], worktree, platform, envPassthroughAll: true, CancellationToken.None);
            string everything = Out("env.out");

            Assert.Equal("|shown", filtered);
            Assert.Equal("s3cret|shown", everything);
        }
        finally
        {
            Environment.SetEnvironmentVariable(secret, null);
            Environment.SetEnvironmentVariable(visible, null);
        }
    }

    // ---- a cancel propagates, never a warning ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task ACancelWhileTheHookRunsPropagatesAndKillsItAsync()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("written for sh and sleep.");

        using CancellationTokenSource cancel = new(TimeSpan.FromMilliseconds(300));
        Stopwatch clock = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => WorktreeHook.RunAsync([["sh", "-c", "sleep 30"]], worktree, platform, envPassthroughAll: false, cancel.Token));

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20), $"the hook was waited out ({clock.Elapsed})");
    }

    // ---- the mechanism under the 120 s: CommandProcess ---------------------------------------------------------------------------------------

    // A tool that exits but leaves a child holding its stdout (`sh -c "watcher &"`) used to hold the read open past every timeout; the reads are now
    // bounded by it too. The child (a 5 s sleep here) outlives the call and ends by itself: it is not ours to find once the tool has exited.
    [Fact]
    public async Task ABackgroundChildHoldingStdoutStillTimesOutAtTheTimeoutNotAtTheChildsEndAsync()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("written for sh and sleep.");

        Stopwatch clock = Stopwatch.StartNew();

        await Assert.ThrowsAsync<TimeoutException>(
            () => CommandProcess.RunAsync("sh", worktree, ["-c", "sleep 5 &"], TimeSpan.FromSeconds(1), CancellationToken.None));

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(4), $"the read waited for the background child ({clock.Elapsed})");
    }

    [Fact]
    public async Task AnExplicitEnvironmentReplacesTheInheritedOneAndNullInheritsItAsync()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("written for sh and printenv-less POSIX expansion.");

        string name = $"CLAUSTRUM_TEST_INHERIT_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(name, "inherited");
        try
        {
            string[] args = ["-c", $"printf '%s|%s' \"${name}\" \"$ONLY\""];
            Dictionary<string, string> given = new() { ["PATH"] = Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin", ["ONLY"] = "given" };

            (int _, string explicitOut, string _) = await CommandProcess.RunAsync("sh", worktree, args, TimeSpan.FromSeconds(30), CancellationToken.None, given);
            (int _, string inheritedOut, string _) = await CommandProcess.RunAsync("sh", worktree, args, TimeSpan.FromSeconds(30), CancellationToken.None);

            Assert.Equal("|given", explicitOut);
            Assert.Equal("inherited|", inheritedOut);
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    [Fact]
    public async Task AToolThatAlreadyExitedHasNoTreeToKillAndTheTimeoutStillSaysTimeoutAsync()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("written for sh and sleep.");

        // The shell exits at once; the call is still bounded. A short timeout and a child that holds both pipes, as above, with stderr too.
        await Assert.ThrowsAsync<TimeoutException>(
            () => CommandProcess.RunAsync("sh", worktree, ["-c", "sleep 3 >&2 &"], TimeSpan.FromMilliseconds(500), CancellationToken.None));
    }
}
