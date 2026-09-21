using Claustrum.Core.Platform;

namespace Claustrum.Tests.Testing;

// Wraps the real platform for everything (PATH/PATHEXT/file IO) except HomeDirectory, which is
// redirected to a per-test temp directory — the same shape as Claustrum.Core.Tests' own copy
// (RunnerTests), duplicated here rather than shared across test projects because Claustrum.Tests has
// no reference to Claustrum.Core.Tests. AppServicesHomeFixture is the only caller of the fixture-wide
// instance; DoctorProbeTests also constructs its own, ad hoc, without going through AppServices at all.
//
// CLAUSTRUM_PARENT_JOB always starts overridden to null (issue #9 tester finding): a developer's own
// shell export of it must never silently put a test's job into a budget tree nobody asked for, and a
// test that wants one in play sets Environment["CLAUSTRUM_PARENT_JOB"] itself. Any other key in
// Environment overrides the real process environment the same way; a key absent from it falls
// through to RealPlatform.
public sealed class HomeRedirectPlatform(string homeDirectory, IReadOnlyDictionary<string, string?>? extraEnvironment = null) : IPlatform
{
    private readonly RealPlatform real = new();

    public Dictionary<string, string?> Environment { get; } = BuildEnvironment(extraEnvironment);

    public ClaustrumOs Os => real.Os;
    public string HomeDirectory { get; } = homeDirectory;

    public string? GetEnvironmentVariable(string name)
    {
        if (name == "CLAUSTRUM_HOME")
            return null;

        return Environment.TryGetValue(name, out string? overridden) ? overridden : real.GetEnvironmentVariable(name);
    }

    public IReadOnlyDictionary<string, string> GetEnvironmentVariables() => real.GetEnvironmentVariables();
    public string[] GetPathEntries() => real.GetPathEntries();
    public string[] GetPathExtensions() => real.GetPathExtensions();
    public bool FileExists(string path) => real.FileExists(path);
    public bool PathExists(string path) => real.PathExists(path);
    public string ReadAllText(string path) => real.ReadAllText(path);

    private static Dictionary<string, string?> BuildEnvironment(IReadOnlyDictionary<string, string?>? extra)
    {
        Dictionary<string, string?> environment = extra is null ? [] : new Dictionary<string, string?>(extra);
        environment.TryAdd("CLAUSTRUM_PARENT_JOB", null);
        return environment;
    }
}
