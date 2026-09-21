using Claustrum.Core.Platform;

namespace Claustrum.Core.Tests.Testing;

// Wraps the real platform for everything (PATH/PATHEXT/file IO) except HomeDirectory, which is
// redirected to a per-test temp directory. Runner/ProcessRunner integration tests need a real
// spawnable process resolved from the real PATH, but must never write ~/.claustrum/jobs into the
// developer's actual home (JobDirectory.Create and its Config.Load prune both read
// IPlatform.HomeDirectory, never Environment directly, exactly so this redirection works).
public sealed class HomeRedirectPlatform(string homeDirectory) : IPlatform
{
    private readonly RealPlatform real = new();

    public ClaustrumOs Os => real.Os;
    public string HomeDirectory { get; } = homeDirectory;

    // CLAUSTRUM_PARENT_JOB is also filtered: a developer shell that exports it (real tree work) would
    // otherwise flip every non-tree RunnerTests case onto BudgetLedger's path by accident.
    public string? GetEnvironmentVariable(string name) =>
        name is "CLAUSTRUM_HOME" or "CLAUSTRUM_PARENT_JOB" ? null : real.GetEnvironmentVariable(name);
    public IReadOnlyDictionary<string, string> GetEnvironmentVariables() => real.GetEnvironmentVariables();
    public string[] GetPathEntries() => real.GetPathEntries();
    public string[] GetPathExtensions() => real.GetPathExtensions();
    public bool FileExists(string path) => real.FileExists(path);
    public bool PathExists(string path) => real.PathExists(path);
    public string ReadAllText(string path) => real.ReadAllText(path);
}
