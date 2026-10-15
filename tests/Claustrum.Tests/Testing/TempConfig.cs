using Claustrum.Core.Config;

namespace Claustrum.Tests.Testing;

/// <summary>
/// A real <see cref="Config"/> loaded from a scratch home (and, when asked, a scratch git repo) with exactly the user and repo
/// layers a test writes - for the code that takes a Config and cares which layer an alias came from (#121). The user config is
/// <see cref="UserConfig.PathFor"/> under the home, the same file the built binary reads.
/// </summary>
internal sealed class TempConfig : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("claustrum-tempconfig-").FullName;

    /// <param name="userJson">The user config's text, or null for no such file.</param>
    /// <param name="repoJson">The repo's claustrum.json, or null for none; a git root is only made when this is given.</param>
    public TempConfig(string? userJson = null, string? repoJson = null)
    {
        Home = Directory.CreateDirectory(Path.Combine(root, "home")).FullName;
        Cwd = Directory.CreateDirectory(Path.Combine(root, "repo")).FullName;
        if (userJson is not null)
            UserConfig.Write(Home, userJson);

        if (repoJson is not null)
        {
            Directory.CreateDirectory(Path.Combine(Cwd, ".git"));
            File.WriteAllText(Path.Combine(Cwd, "claustrum.json"), repoJson);
        }

        Platform = new HomeRedirectPlatform(Home);
        Config = Config.Load(Platform, Cwd);
    }

    public string Home { get; }

    public string Cwd { get; }

    public HomeRedirectPlatform Platform { get; }

    public Config Config { get; }

    public string UserPath => UserConfig.PathFor(Home);

    public string RepoPath => Path.Combine(Cwd, "claustrum.json");

    public void Dispose() => TempTree.Delete(root);
}
