using Claustrum.Core.Platform;

namespace Claustrum.Tests.Testing;

/// <summary>
/// A fully in-memory <see cref="IPlatform"/>, for tests that need to control <see cref="Os"/> itself
/// (ClaudeDesktopConfigTests: Windows/macOS/Linux each take a different branch) — HomeRedirectPlatform
/// wraps the real platform for everything but HomeDirectory, so its <c>Os</c> is always the one this
/// process actually runs on and cannot exercise the other two.
/// </summary>
public sealed class FakePlatform : IPlatform
{
    public ClaustrumOs Os { get; init; } = ClaustrumOs.Linux;

    public string HomeDirectory { get; init; } = "/home/nobody";

    public Dictionary<string, string?> EnvironmentVariables { get; } = [];

    public string? GetEnvironmentVariable(string name) => EnvironmentVariables.GetValueOrDefault(name);

    public IReadOnlyDictionary<string, string> GetEnvironmentVariables() => EnvironmentVariables
        .Where(entry => entry.Value is not null)
        .ToDictionary(entry => entry.Key, entry => entry.Value!);

    public string[] GetPathEntries() => [];

    public string[] GetPathExtensions() => [];

    public bool FileExists(string path) => File.Exists(path);

    public bool PathExists(string path) => Directory.Exists(path) || File.Exists(path);

    public string ReadAllText(string path) => File.ReadAllText(path);
}
