[assembly: AssemblyFixture(typeof(Claustrum.Tests.Testing.HermeticHome))]

namespace Claustrum.Tests.Testing;

// Every test in this assembly runs against a home with no user config and no job store: a developer's
// real ~/.config/claustrum/config.json (#122) once made eleven tests pass locally that failed on all
// three CI runners (PR #127, 2026-10-15). A test that needs a user config writes it with UserConfig.Write.
// Redirected once, process-wide, so spawned children inherit it; the .NET SDK's own home is left alone.
public sealed class HermeticHome : IDisposable
{
    private static readonly string[] variables = ["HOME", "USERPROFILE", "APPDATA", "LOCALAPPDATA", "XDG_CONFIG_HOME", "CLAUSTRUM_HOME", "CLAUSTRUM_PARENT_JOB"];

    private readonly Dictionary<string, string?> saved = variables.ToDictionary(name => name, Environment.GetEnvironmentVariable);

    public string Home { get; } = Directory.CreateTempSubdirectory("claustrum-hermetic-home-").FullName;

    public HermeticHome()
    {
        Environment.SetEnvironmentVariable("HOME", Home);
        Environment.SetEnvironmentVariable("USERPROFILE", Home);
        Environment.SetEnvironmentVariable("APPDATA", Home);
        Environment.SetEnvironmentVariable("LOCALAPPDATA", Home);
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(Home, ".config"));
        Environment.SetEnvironmentVariable("CLAUSTRUM_HOME", Path.Combine(Home, ".claustrum"));
        Environment.SetEnvironmentVariable("CLAUSTRUM_PARENT_JOB", null);
    }

    public void Dispose()
    {
        foreach ((string name, string? value) in saved)
            Environment.SetEnvironmentVariable(name, value);

        Directory.Delete(Home, recursive: true);
    }
}
