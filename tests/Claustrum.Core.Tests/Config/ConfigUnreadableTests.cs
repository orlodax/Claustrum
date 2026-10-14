using Claustrum.Core.Config;
using Claustrum.Core.Platform;
using Claustrum.Core.Tests.Testing;
using CoreConfig = Claustrum.Core.Config.Config;

namespace Claustrum.Core.Tests;

// #112 (2026-10-14): a claustrum.json the process may not read (chmod 000, a share that errors) used to escape Config.Load as a raw
// UnauthorizedAccessException, which the CLI reports as an unhandled error. It is now a ConfigException naming the path ("cannot be
// read"), so the exit is 2 like every other refusal of the file. The unit cases make the platform throw; the last one uses the mode.
public sealed class ConfigUnreadableTests : IDisposable
{
    private readonly ScratchRoot scratch = new("claustrum-config-unreadable-");

    public void Dispose() => scratch.Dispose();

    // The in-memory platform, except that reading one path throws what the OS would.
    private sealed class ThrowingPlatform(FakePlatform inner, string unreadable, Exception failure) : IPlatform
    {
        public ClaustrumOs Os => inner.Os;
        public string HomeDirectory => inner.HomeDirectory;
        public string? GetEnvironmentVariable(string name) => inner.GetEnvironmentVariable(name);
        public IReadOnlyDictionary<string, string> GetEnvironmentVariables() => inner.GetEnvironmentVariables();
        public string[] GetPathEntries() => inner.GetPathEntries();
        public string[] GetPathExtensions() => inner.GetPathExtensions();
        public bool FileExists(string path) => inner.FileExists(path);
        public bool PathExists(string path) => inner.PathExists(path);
        public string ReadAllText(string path) => inner.ReadAllText(path);

        public string? ReadRegularFileText(string path, out string? refusal) =>
            path == unreadable ? throw failure : inner.ReadRegularFileText(path, out refusal);
    }

    private (ThrowingPlatform Platform, string Repo, string ConfigPath) Setup(Exception failure, bool userConfig = false)
    {
        FakePlatform fake = new() { HomeDirectory = Path.Combine(scratch.CreateDirectory(), "home") };
        string repo = scratch.CreateDirectory();
        fake.ExtraExistingPaths.Add(Path.Combine(repo, ".git"));
        string repoConfig = Path.Combine(repo, "claustrum.json");
        string userPath = Path.Combine(fake.HomeDirectory, ".config", "claustrum", "config.json");
        fake.Files[repoConfig] = /*lang=json,strict*/ """{"models":{"mine":"claude:sonnet"}}""";
        fake.Files[userPath] = /*lang=json,strict*/ """{"models":{"theirs":"claude:opus"}}""";
        string unreadable = userConfig ? userPath : repoConfig;
        return (new ThrowingPlatform(fake, unreadable, failure), repo, unreadable);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnUnauthorizedReadOfAConfigFileIsAConfigExceptionNamingThePath(bool userConfig)
    {
        UnauthorizedAccessException denied = new("Access to the path is denied.");
        (ThrowingPlatform platform, string repo, string path) = Setup(denied, userConfig);
        if (OperatingSystem.IsWindows() && userConfig)
            Assert.Skip("the fake platform spells the user config path the Linux way; on Windows it is under %APPDATA%.");

        ConfigException ex = Assert.Throws<ConfigException>(() => CoreConfig.Load(platform, repo));

        Assert.Contains($"'{path}'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("cannot be read", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Access to the path is denied.", ex.Message, StringComparison.Ordinal);
        Assert.Same(denied, ex.InnerException);
    }

    [Fact]
    public void AnIoErrorReadingTheRepoConfigIsAConfigExceptionToo()
    {
        IOException broken = new("Input/output error");
        (ThrowingPlatform platform, string repo, string path) = Setup(broken);

        ConfigException ex = Assert.Throws<ConfigException>(() => CoreConfig.Load(platform, repo));

        Assert.Contains($"'{path}': cannot be read: Input/output error", ex.Message, StringComparison.Ordinal);
    }

    // Only the two failures a path can cause are renamed: a bug elsewhere in the read must still surface as itself.
    [Fact]
    public void AnUnrelatedFailureWhileReadingIsNotDisguisedAsAnUnreadableFile()
    {
        InvalidOperationException bug = new("not a file problem");
        (ThrowingPlatform platform, string repo, _) = Setup(bug);

        Assert.Same(bug, Assert.Throws<InvalidOperationException>(() => CoreConfig.Load(platform, repo)));
    }

    [Fact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void ARealClaustrumJsonWithMode000IsAConfigExceptionNamingItNotARawAccessError()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("POSIX file modes do not exist on Windows.");

        string repo = scratch.CreateRepo();
        string path = Path.Combine(repo, "claustrum.json");
        File.WriteAllText(path, /*lang=json,strict*/ """{"models":{"mine":"claude:opus"}}""");
        File.SetUnixFileMode(path, UnixFileMode.None);
        try
        {
            if (CanStillRead(path))
                Assert.Skip("this account reads a mode-000 file (root, or a capability that bypasses modes).");

            ConfigException ex = Assert.Throws<ConfigException>(() => CoreConfig.Load(new RealPlatform(), repo));

            Assert.Contains(path, ex.Message, StringComparison.Ordinal);
            Assert.Contains("cannot be read", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private static bool CanStillRead(string path)
    {
        try
        {
            using FileStream stream = File.OpenRead(path);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
