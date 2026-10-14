using System.ComponentModel;
using System.Text.Json;
using Claustrum.Cli;
using Claustrum.Tests.Testing;

namespace Claustrum.Tests.Cli;

// The scenario #111 is really about, through a real pseudo-terminal: an interactive bash in a pty runs `claustrum run …` (the
// foreground, or as a `&` job); the "terminal window" closes (the pty master is closed) once the backend has written
// partial.txt. The kernel hangs up the foreground group and bash hangs up its jobs: SIGHUP twice, to the whole group, and every
// later console write throws EIO. The review of 2026-10-14 measured the first design dying here: exit 134 (SIGABRT) from the
// --stream echo, or a `failed` receipt because the backend died first. Expected: result.json `cancelled`, exit 130.
//
// The exit code of a process whose parent (bash) is gone is read through a subreaper: the python driver sets
// PR_SET_CHILD_SUBREAPER, so claustrum is reparented to it and waitpid reports how it ended. That is Linux only, like util-linux
// `setsid`, so the class is skipped elsewhere (macOS included: no prctl). The driver prints one JSON line.
public sealed class CancelPtyEndToEndTests : IDisposable
{
    private const string Driver = """
        import ctypes, glob, json, os, pty, select, sys, time

        pattern, pidfile, command, limit = sys.argv[1], sys.argv[2], sys.argv[3], float(sys.argv[4])
        ctypes.CDLL(None, use_errno=True).prctl(36, 1, 0, 0, 0)  # PR_SET_CHILD_SUBREAPER
        pid, fd = pty.fork()
        if pid == 0:
            os.execvp("bash", ["bash", "--norc", "--noprofile", "-i"])

        started = time.time()
        typed = False
        triggered = False
        while time.time() - started < limit:
            ready, _, _ = select.select([fd], [], [], 0.05)
            if ready:
                try:
                    os.read(fd, 65536)
                except OSError:
                    break
            if not typed and time.time() - started > 0.6:
                os.write(fd, (command + "\n").encode())
                typed = True
            if typed and glob.glob(pattern):
                triggered = True
                break

        if not triggered:
            print(json.dumps({"error": "the trigger file never appeared"}))
            sys.exit(0)

        os.close(fd)
        statuses = {}
        deadline = time.time() + limit
        target = None
        while time.time() < deadline:
            if target is None and os.path.exists(pidfile):
                text = open(pidfile).read().strip()
                target = int(text) if text else None
            try:
                got, status = os.waitpid(-1, os.WNOHANG)
            except ChildProcessError:
                break
            if got == 0:
                time.sleep(0.02)
                continue
            statuses[got] = status
            if target is not None and target in statuses:
                break

        if target is None or target not in statuses:
            print(json.dumps({"error": "the claustrum process was never reaped"}))
        else:
            print(json.dumps({"exit": os.waitstatus_to_exitcode(statuses[target])}))
        """;

    private readonly CancelScenario scenario = new();

    public void Dispose() => scenario.Dispose();

    private static void RequireLinux()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Skip("the pty driver reads the exit code through PR_SET_CHILD_SUBREAPER and detaches with util-linux setsid: Linux only (macOS has neither).");
    }

    // Types `<prefix> sh -c 'echo $$ > pidfile; exec claustrum …'` into bash in a pty, closes the pty master once the trigger exists,
    // and returns the exit code the driver reaped (negative: the signal that killed it).
    private async Task<int> ClosedTerminalExitAsync(string[] claustrumArgs, string glob, string prefix = "", string suffix = "")
    {
        string driver = Path.Combine(scenario.Repo.Home, "pty-driver.py");
        string pidFile = Path.Combine(scenario.Repo.Home, "claustrum.pid");
        File.WriteAllText(driver, Driver);
        string binary = Path.Combine(AppContext.BaseDirectory, "claustrum");
        string quoted = string.Join(' ', claustrumArgs.Select(arg => $"'{arg}'"));
        string command = $"{prefix} sh -c 'echo $$ > {pidFile}; exec {binary} {quoted}' {suffix}".Trim();

        try
        {
            using RunningCli driverProcess = new("python3", scenario.Repo.Repo, scenario.Repo.Home, IsolatedRepo.ChildPath(), ["-I", driver, glob, pidFile, command, "30"]);
            int exit = await driverProcess.WaitForExitAsync(TimeSpan.FromSeconds(90), TestContext.Current.CancellationToken);
            Assert.Equal(0, exit);

            using JsonDocument verdict = JsonDocument.Parse(driverProcess.Stdout.Trim().Split('\n')[^1]);
            Assert.False(verdict.RootElement.TryGetProperty("error", out JsonElement error), $"the pty driver failed: {error}\nstderr: {driverProcess.Stderr}");
            return verdict.RootElement.GetProperty("exit").GetInt32();
        }
        catch (Win32Exception ex)
        {
            Assert.Skip($"python3 is not available to drive a pty: {ex.Message}");
            return 0;
        }
    }

    private string PartialGlob => Path.Combine(scenario.Repo.Repo, ".claustrum", "worktrees", "*", "partial.txt");

    private void ScriptBackend(string sleep, bool streaming)
    {
        string[] commands = streaming
            ? ["printf 'half\\n' > partial.txt", "while true; do printf 'streamed line\\n'; sleep 0.2; done"]
            : ["printf 'half\\n' > partial.txt", $"sleep {sleep}"];
        scenario.Prepare(scenario.Repo, sleep);
        scenario.Repo.Script(new FakeClaudeScript { Commands = commands });
    }

    private void AssertCancelledReceipt(string role)
    {
        (string jobId, JsonElement onDisk) = scenario.OnlyReceipt();
        Assert.Equal("cancelled", CancelScenario.Str(onDisk, "status"));
        Assert.Equal(role, CancelScenario.Str(onDisk, "role"));
        Assert.Equal("partial.txt", scenario.Repo.Git("show", "--name-only", "--format=", CancelScenario.Str(onDisk, "commit")));
        Assert.NotEmpty(jobId);
    }

    [Theory]
    [InlineData(false, false, "32.41")]
    [InlineData(true, false, "32.42")]
    [InlineData(false, true, "32.43")]
    [InlineData(true, true, "32.44")]
    public async Task ClosingTheTerminalCancelsARunWithItsReceiptAndExit130Async(bool stream, bool background, string sleep)
    {
        RequireLinux();
        scenario.Adopt(new IsolatedRepo()).WriteCast(maxParallel: 2);
        ScriptBackend(sleep, stream);
        string[] args = stream
            ? ["run", "builder", "--brief", "x", "--json", "--stream"]
            : ["run", "builder", "--brief", "x", "--json"];

        int exit = await ClosedTerminalExitAsync(args, PartialGlob, suffix: background ? "&" : "");

        Assert.Equal(ExitCodes.Cancelled, exit);
        AssertCancelledReceipt("builder");
        Assert.Empty(CancelScenario.SleepPids(sleep, scenario.Repo.Repo));
    }

    [Fact]
    public async Task ClosingTheTerminalCancelsCoordinateWithItsReceiptAndExit130Async()
    {
        RequireLinux();
        scenario.Adopt(IsolatedRepo.ForCoordinate());
        ScriptBackend("32.45", streaming: false);

        int exit = await ClosedTerminalExitAsync(["coordinate", "--brief", "x", "--json"], PartialGlob);

        Assert.Equal(ExitCodes.Cancelled, exit);
        AssertCancelledReceipt("architect");
    }

    // The run is detached from the terminal (its own session, no hangup reaches it), only its output is gone: every console write
    // throws EIO. The verdict must be the run's own - success 0, failed 1 - not a crash or a different code.
    [Theory]
    [InlineData(false, 0, "success")]
    [InlineData(true, 1, "failed")]
    public async Task ARunWhoseTerminalIsGoneEndsWithItsOwnExitCodeNotACrashAsync(bool fail, int expectedExit, string expectedStatus)
    {
        RequireLinux();
        scenario.Adopt(new IsolatedRepo()).WriteCast(maxParallel: 2);
        scenario.Repo.Script(new FakeClaudeScript { Commands = ["printf 'half\\n' > partial.txt", "sleep 2"], Fail = fail });

        int exit = await ClosedTerminalExitAsync(["run", "builder", "--brief", "x"], PartialGlob, prefix: "setsid -w");

        Assert.Equal(expectedExit, exit);
        string jobId = Assert.Single(scenario.Repo.JobDirectories());
        Assert.Equal(expectedStatus, CancelScenario.Str(scenario.Repo.ReadResult(jobId), "status"));
    }
}
