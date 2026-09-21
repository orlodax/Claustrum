using Claustrum.Core.Backends;
using Claustrum.Core.Config;

namespace Claustrum.Tests.Testing;

// A minimal IBackend for CastQuestionnaireTests (only DetectAsync is exercised there, so Build/Parse
// throw the way Claustrum.Core.Tests' ScriptedBackend does for members its own tests don't reach) and
// for DoctorProbeTests, which needs Build to point at a real, short-lived OS command — the trick
// ScriptedBackend already uses for Runner, unreachable here since Claustrum.Tests has no reference to
// Claustrum.Core.Tests (HomeRedirectPlatform.cs's own doc comment). `command`/`args`/`parseResult` are
// null exactly when a caller (CastQuestionnaireTests) never reaches Build/Parse at all.
public sealed class FakeBackend(string name, bool found, string? command = null, string[]? args = null, ParsedOutput? parseResult = null) : IBackend
{
    public string Name => name;

    /// <summary>The ResolvedRun the last Build call received — DoctorProbeTests asserts on the brief/budget it carries.</summary>
    public ResolvedRun? LastRun { get; private set; }

    public Task<Doctor> DetectAsync(BackendConfig? config, CancellationToken cancellationToken) =>
        Task.FromResult(found ? new Doctor(true, "/usr/bin/fake", "1.0.0", []) : new Doctor(false, null, null, [$"'{name}' was not found on PATH"]));

    public ProcessSpec Build(ResolvedRun run)
    {
        LastRun = run;
        return command is null
            ? throw new NotSupportedException("not exercised by CastQuestionnaireTests")
            : new ProcessSpec(command, args ?? [], run.Cwd, run.Env, []);
    }

    public ParsedOutput Parse(string stdout, string stderr, int exitCode) =>
        parseResult ?? throw new NotSupportedException("not exercised by CastQuestionnaireTests");

    /// <summary>
    /// A registered, found backend whose Build spawns a real one-line shell command replying "OK" at
    /// the given reported cost — DoctorProbeTests' stand-in for a real "reply OK" backend.
    /// <paramref name="extraShellCommand"/> runs before the echo, in the same shell and cwd (the
    /// probe's own temp directory), for a test that needs the process to leave something behind there.
    /// </summary>
    public static FakeBackend RepliesOk(string name, decimal costUsd, string? extraShellCommand = null)
    {
        string script = extraShellCommand is null ? "echo OK" : $"{extraShellCommand} && echo OK";
        ParsedOutput parsed = new(FinalMessage: "OK", SessionId: null, CostUsd: costUsd, Usage: null, ReportedEdits: [], Raw: null, IsError: false);
        return OperatingSystem.IsWindows()
            ? new FakeBackend(name, found: true, "cmd", ["/c", script], parsed)
            : new FakeBackend(name, found: true, "sh", ["-c", script], parsed);
    }
}
