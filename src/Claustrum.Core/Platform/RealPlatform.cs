namespace Claustrum.Core.Platform;

public sealed class RealPlatform : IPlatform
{
    public ClaustrumOs Os { get; } = DetectOs();

    public string HomeDirectory { get; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public string? GetEnvironmentVariable(string name) => Environment.GetEnvironmentVariable(name);

    public IReadOnlyDictionary<string, string> GetEnvironmentVariables()
    {
        Dictionary<string, string> result = new(StringComparer.Ordinal);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
            result[(string)entry.Key] = (string?)entry.Value ?? "";
        return result;
    }

    public string[] GetPathEntries() =>
        (GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

    public string[] GetPathExtensions() => Os == ClaustrumOs.Windows
        ? (GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD").Split(';', StringSplitOptions.RemoveEmptyEntries)
        : [];

    public bool FileExists(string path) => File.Exists(path);

    public bool PathExists(string path) => File.Exists(path) || Directory.Exists(path);

    public string ReadAllText(string path) => File.ReadAllText(path);

    private static ClaustrumOs DetectOs()
    {
        if (OperatingSystem.IsWindows())
            return ClaustrumOs.Windows;
        return OperatingSystem.IsMacOS() ? ClaustrumOs.MacOs : ClaustrumOs.Linux;
    }
}
