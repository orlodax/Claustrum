using System.ComponentModel;
using Claustrum.Core.Process;

namespace Claustrum.Core.Tests.Process;

// CommandProcess is the shared spawn-and-read helper behind GitProcess (a GitProcess call already
// works end to end through WorktreeSnapshotTests' real git invocations) and GhIssueSource — this
// file exercises it directly rather than only through those two callers.
public sealed class CommandProcessTests
{
    [Fact]
    public async Task RunAsyncReturnsExitCodeAndCapturedStdoutAsync()
    {
        (string exe, string[] args) = EchoCommand("hello");

        (int exitCode, string stdout, string stderr) = await CommandProcess.RunAsync(
            exe, Path.GetTempPath(), args, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Contains("hello", stdout, StringComparison.Ordinal);
        Assert.Equal("", stderr);
    }

    // GhIssueSource's own catch clause (`ex is Win32Exception or IOException or TimeoutException`)
    // names Win32Exception as what a missing binary looks like from Process.Start on both OSes — this
    // pins that CommandProcess itself, not just its caller's assumption, actually throws that type.
    [Fact]
    public async Task RunAsyncWithANonexistentExecutableThrowsWin32ExceptionAsync()
    {
        await Assert.ThrowsAsync<Win32Exception>(() => CommandProcess.RunAsync(
            "claustrum-test-does-not-exist-binary-xyz", Path.GetTempPath(), [], TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RunAsyncTimesOutAJobThatNeverExitsAsync()
    {
        (string exe, string[] args) = SleepCommand(30);

        await Assert.ThrowsAsync<TimeoutException>(() => CommandProcess.RunAsync(
            exe, Path.GetTempPath(), args, TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken));
    }

    private static (string Exe, string[] Args) EchoCommand(string text) => OperatingSystem.IsWindows()
        ? ("cmd", ["/c", "echo", text])
        : ("echo", [text]);

    private static (string Exe, string[] Args) SleepCommand(int seconds) => OperatingSystem.IsWindows()
        ? ("cmd", ["/c", "ping", "-n", (seconds + 1).ToString(), "127.0.0.1"])
        : ("sh", ["-c", $"sleep {seconds}"]);
}
