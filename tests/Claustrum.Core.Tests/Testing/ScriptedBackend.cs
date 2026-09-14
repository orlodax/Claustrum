using Claustrum.Core.Backends;
using Claustrum.Core.Config;

namespace Claustrum.Core.Tests.Testing;

// A fake IBackend for Runner tests: Build points ProcessRunner at a real, short-lived OS command
// (so Runner's real ProcessRunner/BinaryLocator pipeline still runs end-to-end) instead of an
// actual `claude` install. Records the ResolvedRun it was given so a test can assert on the brief
// Runner built (attachments, etc.).
public sealed class ScriptedBackend(string exe, string[] args, ParsedOutput? parseResult = null) : IBackend
{
    public string Name => "scripted";

    public ResolvedRun? LastRun { get; private set; }

    public Task<Doctor> DetectAsync(BackendConfig? config, CancellationToken cancellationToken) =>
        throw new NotSupportedException("not exercised by Runner tests");

    public ProcessSpec Build(ResolvedRun run)
    {
        LastRun = run;
        return new ProcessSpec(exe, args, run.Cwd, run.Env, []);
    }

    public ParsedOutput Parse(string stdout, string stderr, int exitCode) =>
        parseResult ?? new ParsedOutput(stdout, null, null, null, [], null, exitCode != 0);

    public static ScriptedBackend Success() => OperatingSystem.IsWindows()
        ? new ScriptedBackend("cmd", ["/c", "exit 0"])
        : new ScriptedBackend("sh", ["-c", "exit 0"]);

    public static ScriptedBackend Sleep(int seconds) => OperatingSystem.IsWindows()
        ? new ScriptedBackend("cmd", ["/c", "ping", "-n", (seconds + 1).ToString(), "127.0.0.1"])
        : new ScriptedBackend("sh", ["-c", $"sleep {seconds}"]);

    // A registered backend whose executable BinaryLocator will never resolve on PATH — distinct
    // from the unregistered-backend-name case (BackendRegistry.TryGet returning false), this instead
    // exercises ProcessRunner.RunAsync throwing BackendNotFoundException.
    public static ScriptedBackend NotOnPath() => new("claustrum-test-does-not-exist-binary", []);
}
