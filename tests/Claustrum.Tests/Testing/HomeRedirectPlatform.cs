using Claustrum.Core.Platform;

namespace Claustrum.Tests.Testing;

// Wraps the real platform for everything (PATH/PATHEXT/file IO) except HomeDirectory, which is
// redirected to a per-test temp directory — the same shape as Claustrum.Core.Tests' own copy
// (RunnerTests), duplicated here rather than shared across test projects because Claustrum.Tests has
// no reference to Claustrum.Core.Tests. AppServicesHomeFixture is the only caller.
public sealed class HomeRedirectPlatform(string homeDirectory) : IPlatform
{
    private readonly RealPlatform real = new();

    public ClaustrumOs Os => real.Os;
    public string HomeDirectory { get; } = homeDirectory;
    public string? GetEnvironmentVariable(string name) => name == "CLAUSTRUM_HOME" ? null : real.GetEnvironmentVariable(name);
    public IReadOnlyDictionary<string, string> GetEnvironmentVariables() => real.GetEnvironmentVariables();
    public string[] GetPathEntries() => real.GetPathEntries();
    public string[] GetPathExtensions() => real.GetPathExtensions();
    public bool FileExists(string path) => real.FileExists(path);
    public bool PathExists(string path) => real.PathExists(path);
    public string ReadAllText(string path) => real.ReadAllText(path);
}
