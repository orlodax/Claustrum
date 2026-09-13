using Claustrum.Core.Platform;

namespace Claustrum.Core.Tests.Testing;

// Fully synthetic IPlatform: no real disk/env access, so Windows-only production branches
// (BinaryLocator's npm-shim unwrap, Config's %APPDATA% path) run under xunit on any OS
// (docs brief item 3, AGENTS.md "the IPlatform seam that lets ... run under xunit on either OS").
public sealed class FakePlatform : IPlatform
{
    public ClaustrumOs Os { get; init; } = ClaustrumOs.Linux;
    public string HomeDirectory { get; init; } = "/home/fake";
    public Dictionary<string, string> EnvironmentVariables { get; } = new(StringComparer.Ordinal);

    // OrdinalIgnoreCase: real Windows/macOS filesystems (and PATHEXT lookups, which conventionally
    // spell extensions in upper case) are case-insensitive, so a fake using a case-sensitive
    // comparer would fail a real lookup a real filesystem would satisfy — measured directly: a fake
    // ".cmd" key missed a BinaryLocator search built from PATHEXT's ".CMD" and threw
    // BackendNotFoundException even though the "file" was "there".
    public Dictionary<string, string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> ExtraExistingPaths { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string[] PathEntries { get; set; } = [];
    public string[] PathExtensions { get; set; } = [];

    public string? GetEnvironmentVariable(string name) =>
        EnvironmentVariables.TryGetValue(name, out string? value) ? value : null;

    public IReadOnlyDictionary<string, string> GetEnvironmentVariables() => EnvironmentVariables;

    public string[] GetPathEntries() => PathEntries;

    public string[] GetPathExtensions() => PathExtensions;

    public bool FileExists(string path) => Files.ContainsKey(path);

    public bool PathExists(string path) => Files.ContainsKey(path) || ExtraExistingPaths.Contains(path);

    public string ReadAllText(string path) =>
        Files.TryGetValue(path, out string? content) ? content : throw new FileNotFoundException(path);
}
