namespace Claustrum.Core.Platform;

// The one seam that lets BinaryLocator's Windows npm-shim branch and Config's OS-specific config
// path run under xunit on either OS (docs brief item 3). Never read Environment/File directly
// outside RealPlatform — that is what would make those branches untestable cross-platform.
public interface IPlatform
{
    ClaustrumOs Os { get; }
    string HomeDirectory { get; }
    string? GetEnvironmentVariable(string name);
    IReadOnlyDictionary<string, string> GetEnvironmentVariables();
    string[] GetPathEntries();
    string[] GetPathExtensions();
    bool FileExists(string path);
    bool PathExists(string path);
    string ReadAllText(string path);

    // The text of a path inside the repo, or null with the reason when it is not a regular file: a FIFO there blocks
    // ReadAllText forever (NOTES.md "A FIFO in place of a tracked file"). The default is for fakes that hold no real files.
    string? ReadRegularFileText(string path, out string? refusal)
    {
        refusal = null;
        return ReadAllText(path);
    }
}
