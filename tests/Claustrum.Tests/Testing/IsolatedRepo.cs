using System.Text.Json;

namespace Claustrum.Tests.Testing;

/// <summary>
/// A git repository with claustrum's `backends.claude.path` pointed at a <see cref="FakeClaude"/> script,
/// and the real built `claustrum` binary to run in it: the fixture every isolated-run e2e test stands on. The
/// repository, CLAUSTRUM_HOME and the script's own recordings are all under temp directories that
/// <see cref="Dispose"/> deletes. PATH for the child is git's directory plus the system's standard ones, so
/// the script's `cat` and `sleep` resolve while a `claude` installed under a user's home can never be found
/// — and the configured script path is what runs in any case.
/// </summary>
public sealed class IsolatedRepo : IDisposable
{
    private readonly ClaustrumCli cli = new();
    private readonly string fakeDirectory;

    public IsolatedRepo()
    {
        fakeDirectory = Directory.CreateDirectory(Path.Combine(cli.Home, "fake")).FullName;
        TestGit.Init(Repo);
        File.WriteAllText(Path.Combine(Repo, ".gitignore"), ".claustrum/\n");
        File.WriteAllText(Path.Combine(Repo, "seed.txt"), "seed\n");
        File.WriteAllText(Path.Combine(Repo, "claustrum.json"), FakeClaude.ConfigJson(ScriptPath));
        TestGit.CommitAll(Repo, "seed");
        Script(new FakeClaudeScript());
    }

    public string Repo => cli.Cwd;

    public string Home => cli.Home;

    public string JobsRoot => cli.JobsDirectory;

    public string ScriptPath => Path.Combine(fakeDirectory, FakeClaude.ScriptName);

    public string ArgvLog => Path.Combine(fakeDirectory, "argv.txt");

    public string StdinLog => Path.Combine(fakeDirectory, "stdin.txt");

    public string MarkerLog => Path.Combine(fakeDirectory, "markers.txt");

    public string StampDirectory => Directory.CreateDirectory(Path.Combine(fakeDirectory, "stamps")).FullName;

    public string WorktreePath(string jobId) => Path.Combine(Repo, ".claustrum", "worktrees", jobId);

    /// <summary>
    /// The tail `.claustrum/worktrees/&lt;id&gt;` of a worktree path. The child may be handed the repository
    /// through a Windows 8.3 %TEMP% and print it in another spelling, so a receipt's `worktree` is compared by
    /// this tail rather than by the whole path.
    /// </summary>
    public static string WorktreeTail(string jobId) => Path.Combine(".claustrum", "worktrees", jobId);

    public void Dispose() => cli.Dispose();

    public void Script(FakeClaudeScript script) => FakeClaude.Write(ScriptPath, script);

    /// <summary>`.claustrum/casts/default.json` with a builder on the fake claude and this `max_parallel`.</summary>
    public void WriteCast(int? maxParallel, string role = "builder")
    {
        Directory.CreateDirectory(cli.CastsDirectory);
        string parallel = maxParallel is { } cap ? $",\"max_parallel\":{cap}" : "";
        string entry = "{\"backend\":\"claude\"" + parallel + "}";
        File.WriteAllText(Path.Combine(cli.CastsDirectory, "default.json"),
            "{\"name\":\"default\",\"library\":\"1.0.0\",\"architect\":{\"mode\":\"host\"},\"roles\":{\"" + role + "\":" + entry + "},\"budget_usd\":null}");
    }

    public Task<CliResult> RunAsync(params string[] args) => cli.RunAsync(args, "", ChildPath());

    /// <summary>`claustrum run builder --brief x --json` plus <paramref name="extraArgs"/>, with stdout parsed.</summary>
    public async Task<(CliResult Process, JsonElement Result)> RunBuilderAsync(params string[] extraArgs)
    {
        CliResult process = await RunAsync(["run", "builder", "--brief", "do the task", "--json", .. extraArgs]);
        using JsonDocument document = JsonDocument.Parse(process.Stdout);
        return (process, document.RootElement.Clone());
    }

    public JsonElement ReadResult(string jobId)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(Path.Combine(JobsRoot, jobId, "result.json")));
        return document.RootElement.Clone();
    }

    private static string ChildPath()
    {
        string git = GitDirectory();
        return OperatingSystem.IsWindows()
            ? string.Join(Path.PathSeparator, git, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32"))
            : string.Join(Path.PathSeparator, git, "/usr/bin", "/bin");
    }

    private static string GitDirectory()
    {
        string executable = OperatingSystem.IsWindows() ? "git.exe" : "git";
        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (File.Exists(Path.Combine(directory, executable)))
                return directory;
        }

        throw new InvalidOperationException("git was not found on PATH; these tests need it");
    }
}
