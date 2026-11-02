using Claustrum.Core.Backends;
using Claustrum.Core.Config;
using Claustrum.Core.Tracing;

namespace Claustrum.Core.Tests.Testing;

// A ScriptedBackend that also answers IBackend.Trace (#145) and says what it does with read dirs (#144): the trace is whatever the test hands in
// (or throws), so Runner's use of it is tested without a backend's log format in the way. Records the ResolvedRun it was built with.
public sealed class TracedBackend(ScriptedBackend inner, Func<string, RunTrace?> trace, bool honoursReadDirs = true) : IBackend
{
    public string Name => inner.Name;

    public ResolvedRun? LastRun => inner.LastRun;

    public bool HonoursReadDirs => honoursReadDirs;

    public Task<Doctor> DetectAsync(BackendConfig? config, CancellationToken cancellationToken) => inner.DetectAsync(config, cancellationToken);

    public ProcessSpec Build(ResolvedRun run) => inner.Build(run);

    public ParsedOutput Parse(string stdout, string stderr, int exitCode) => inner.Parse(stdout, stderr, exitCode);

    public RunTrace? Trace(string stdout) => trace(stdout);
}
