using Claustrum.Core.Platform;

namespace Claustrum.Tests.Testing;

/// <summary>
/// A platform whose job store is the one a spawned `claustrum` child of an <see cref="IsolatedRepo"/> uses (CLAUSTRUM_HOME = its home), for the
/// in-process calls (TreeSettler, LiveJob) that must read what that child's detached host wrote. <see cref="HomeRedirectPlatform"/> hides
/// CLAUSTRUM_HOME on purpose, so this one answers it itself.
/// </summary>
internal sealed class CliHomePlatform(string home) : IPlatform
{
    private readonly HomeRedirectPlatform inner = new(home);

    public ClaustrumOs Os => inner.Os;
    public string HomeDirectory => inner.HomeDirectory;
    public string? GetEnvironmentVariable(string name) => name == "CLAUSTRUM_HOME" ? home : inner.GetEnvironmentVariable(name);
    public IReadOnlyDictionary<string, string> GetEnvironmentVariables() => inner.GetEnvironmentVariables();
    public string[] GetPathEntries() => inner.GetPathEntries();
    public string[] GetPathExtensions() => inner.GetPathExtensions();
    public bool FileExists(string path) => inner.FileExists(path);
    public bool PathExists(string path) => inner.PathExists(path);
    public string ReadAllText(string path) => inner.ReadAllText(path);
    public string? ReadRegularFileText(string path, out string? refusal) => inner.ReadRegularFileText(path, out refusal);
}
