namespace Claustrum.Tests.Testing;

/// <summary>
/// The per-developer config (#122) in a fake or temp home: `~/.config/claustrum/config.json`, or
/// `%APPDATA%\claustrum\config.json` on Windows, where the suite's children and redirected platforms
/// both point APPDATA at that same home. A run that needs a model with no `claustrum.json` around finds
/// it here, the way a developer's own machine would — never through an environment bypass.
/// </summary>
internal static class UserConfig
{
    /// <summary>Every built-in alias answered with the model Claustrum would default it to, so no run falls through to a built-in.</summary>
    public const string Complete = /*lang=json,strict*/ """
        {
          "models": {
            "frontier-reasoning": "claude:opus",
            "frontier-coding": "claude:opus",
            "standard-coding": "claude:sonnet",
            "cheap-coding": "claude:haiku",
            "fast": "claude:haiku"
          }
        }
        """;

    public static string PathFor(string home) => OperatingSystem.IsWindows()
        ? Path.Combine(home, "claustrum", "config.json")
        : Path.Combine(home, ".config", "claustrum", "config.json");

    /// <summary>Writes the user config under <paramref name="home"/> and returns its path.</summary>
    public static string Write(string home, string json = Complete)
    {
        string path = PathFor(home);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
        return path;
    }
}
