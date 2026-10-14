namespace Claustrum.Core.Tests.Testing;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class FakeGitCollectionDefinition
{
    public const string Name = "fake git on PATH";
}

/// <summary>
/// A script named `git` first on PATH that answers one call with a chosen exit code and hands every other call to the real git.
/// PATH is process-wide, so the tests that use it share a collection that runs alone; Dispose puts PATH back. Unix only: the
/// script is `sh`, and the interrupted-git handling it exercises is Unix only too.
/// </summary>
public sealed class FakeGit : IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("claustrum-fake-git-").FullName;
    private readonly string originalPath = Environment.GetEnvironmentVariable("PATH") ?? "";

    /// <param name="subcommand">The first argument that does not start with `-`.</param>
    /// <param name="exitCode">What the intercepted call exits with.</param>
    /// <param name="alsoContains">A further argument the call must carry (`--quiet`, `refs/heads`), or null.</param>
    /// <param name="echoArgs">Instead of exiting with a code, prints the arguments one per line and exits 0: what a git too old to know a flag does with it.</param>
    public FakeGit(string subcommand, int exitCode, string? alsoContains = null, bool echoArgs = false)
    {
        string also = alsoContains is null ? "" : $" && case \"$*\" in *'{alsoContains}'*) true;; *) false;; esac";
        Install($$"""
            if [ "$sub" = "{{subcommand}}" ]{{also}}; then {{(echoArgs ? "printf '%s\\n' \"$@\"; exit 0" : $"exit {exitCode}")}}; fi
            """);
    }

    private FakeGit(string interception) => Install(interception);

    /// <summary>
    /// A fake git running <paramref name="interception"/>, a POSIX sh fragment, before it hands the call to the real one: `$sub` is
    /// the first argument that does not start with `-`, `"$@"` the whole command line (`worktree add -q &lt;path&gt; ...`: the path is
    /// `$4`). A branch that does not `exit` falls through to the real git.
    /// </summary>
    public static FakeGit Scripted(string interception) => new(interception);

    private void Install(string interception)
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("the fake git is an sh script and exit codes 129/130/131/143 are read on Unix only.");

        string real = originalPath.Split(Path.PathSeparator).Select(entry => Path.Combine(entry, "git")).FirstOrDefault(File.Exists)
            ?? throw new InvalidOperationException("no git on PATH");
        string script = $$"""
            #!/bin/sh
            sub=""
            for a in "$@"; do
              case "$a" in -*) ;; *) sub="$a"; break;; esac
            done
            {{interception}}
            exec "{{real}}" "$@"
            """;
        string path = Path.Combine(directory, "git");
        File.WriteAllText(path, script.Replace("\r\n", "\n", StringComparison.Ordinal));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        Environment.SetEnvironmentVariable("PATH", directory + Path.PathSeparator + originalPath);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("PATH", originalPath);
        Directory.Delete(directory, recursive: true);
    }
}

/// <summary>PATH replaced by an empty directory for the scope: no `git` can be started, which is a Win32Exception from Process.Start.</summary>
public sealed class NoGitOnPath : IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("claustrum-no-git-").FullName;
    private readonly string originalPath = Environment.GetEnvironmentVariable("PATH") ?? "";

    public NoGitOnPath() => Environment.SetEnvironmentVariable("PATH", directory);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("PATH", originalPath);
        Directory.Delete(directory);
    }
}
