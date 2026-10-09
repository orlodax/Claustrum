using System.Diagnostics;
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
    /// <summary>
    /// What `claustrum init` writes for Claustrum's machinery and what `coordinate` requires a committed rule for (#74):
    /// not the whole of `.claustrum/`, which would also ignore the cast and make `coordinate` refuse it as `(ignored)`.
    /// </summary>
    public const string MachineryIgnoreRules = ".claustrum/worktrees/\n.claustrum/briefs/\n.claustrum/locks/\n";

    private readonly ClaustrumCli cli;
    private readonly string fakeDirectory;

    /// <param name="gitignore">The committed `.gitignore`: the whole of `.claustrum/` unless a `coordinate` test needs its cast tracked.</param>
    /// <param name="directoryPrefix">What the repository directory is named after — a space or non-ASCII characters on purpose, for the paths that get quoted.</param>
    public IsolatedRepo(string gitignore = ".claustrum/\n", string directoryPrefix = "claustrum-cli-")
    {
        cli = new ClaustrumCli(directoryPrefix);
        fakeDirectory = Directory.CreateDirectory(Path.Combine(cli.Home, "fake")).FullName;
        TestGit.Init(Repo);
        // A developer's global excludes file must not decide what these repositories ignore.
        TestGit.Run(Repo, "config", "core.excludesFile", EmptyExcludes());
        File.WriteAllText(Path.Combine(Repo, ".gitignore"), gitignore);
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

    /// <summary>The repository, set up for `coordinate`: the machinery rules ignored, the cast and config committed.</summary>
    public static IsolatedRepo ForCoordinate(int? builderMaxParallel = null, decimal? budgetUsd = null, string directoryPrefix = "claustrum-cli-")
    {
        IsolatedRepo repo = new(MachineryIgnoreRules, directoryPrefix);
        repo.WriteCoordinateCast(builderMaxParallel, budgetUsd);
        repo.CommitAll("cast");
        return repo;
    }

    /// <summary>`git` in the repository; throws with git's words on a non-zero exit.</summary>
    public string Git(params string[] args) => TestGit.Run(Repo, args);

    public string CommitAll(string message) => TestGit.CommitAll(Repo, message);

    /// <summary>
    /// `.claustrum/casts/default.json` with a spawned architect on the fake claude, a builder with this `max_parallel`
    /// and this tree budget — never written to a ledger a test does not read (a budget alone makes no cost).
    /// </summary>
    public void WriteCoordinateCast(int? maxParallel = null, decimal? budgetUsd = null)
    {
        Directory.CreateDirectory(cli.CastsDirectory);
        string parallel = maxParallel is { } cap ? $",\"max_parallel\":{cap}" : "";
        string budget = budgetUsd is { } usd ? usd.ToString(System.Globalization.CultureInfo.InvariantCulture) : "null";
        File.WriteAllText(Path.Combine(cli.CastsDirectory, "default.json"),
            "{\"name\":\"default\",\"library\":\"1.0.0\",\"architect\":{\"mode\":\"spawned\",\"model\":\"claude:opus\"},"
            + "\"roles\":{\"builder\":{\"backend\":\"claude\"" + parallel + "}},\"budget_usd\":" + budget + "}");
    }

    /// <summary>`claustrum coordinate --brief x --json` plus <paramref name="extraArgs"/>, with stdout parsed when it is one document.</summary>
    public async Task<(CliResult Process, JsonElement? Result)> CoordinateAsync(params string[] extraArgs)
    {
        CliResult process = await RunAsync(["coordinate", "--brief", "do the task", "--json", .. extraArgs]);
        if (process.Stdout.Trim().Length == 0)
            return (process, null);

        using JsonDocument document = JsonDocument.Parse(process.Stdout);
        return (process, document.RootElement.Clone());
    }

    /// <summary>The job directories under this home: what a refused `coordinate` must leave empty.</summary>
    public string[] JobDirectories() =>
        Directory.Exists(JobsRoot) ? [.. Directory.GetDirectories(JobsRoot).Select(Path.GetFileName).OfType<string>()] : [];

    /// <summary>A `claustrum mcp` server over real stdio in this repository, with the same environment the CLI runs get.</summary>
    internal McpStdioClient StartMcp()
    {
        string binary = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "claustrum.exe" : "claustrum");
        Assert.True(File.Exists(binary), $"built claustrum binary not found at '{binary}'");

        ProcessStartInfo startInfo = new()
        {
            FileName = binary,
            WorkingDirectory = Repo,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("mcp");
        startInfo.ArgumentList.Add("--cwd");
        startInfo.ArgumentList.Add(Repo);
        startInfo.Environment["CLAUSTRUM_HOME"] = Home;
        startInfo.Environment["HOME"] = Home;
        startInfo.Environment["APPDATA"] = Home;
        startInfo.Environment["CLAUSTRUM_SKIP_PROBE"] = "1";
        startInfo.Environment.Remove("CLAUSTRUM_PARENT_JOB"); // #66
        startInfo.Environment["PATH"] = ChildPath();

        return new McpStdioClient(Process.Start(startInfo) ?? throw new InvalidOperationException("claustrum mcp failed to start"));
    }

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

    /// <summary>A run whose child sees these variables too (a null value removes one), and optionally another PATH.</summary>
    public Task<CliResult> RunWithEnvAsync(string[] args, IReadOnlyDictionary<string, string?> env, string? path = null) =>
        cli.RunAsync(args, "", path ?? ChildPath(), env);

    /// <summary>`claustrum run builder --brief x --json` plus <paramref name="extraArgs"/>, with stdout parsed.</summary>
    public async Task<(CliResult Process, JsonElement Result)> RunBuilderAsync(params string[] extraArgs)
    {
        CliResult process = await RunAsync(["run", "builder", "--brief", "do the task", "--json", .. extraArgs]);
        using JsonDocument document = JsonDocument.Parse(process.Stdout);
        return (process, document.RootElement.Clone());
    }

    /// <summary>`run builder --brief x --json` plus <paramref name="extraArgs"/> in a child with these variables set.</summary>
    public async Task<(CliResult Process, JsonElement Result)> RunBuilderWithEnvAsync(IReadOnlyDictionary<string, string?> env, string? path = null, params string[] extraArgs)
    {
        CliResult process = await RunWithEnvAsync(["run", "builder", "--brief", "do the task", "--json", .. extraArgs], env, path);
        using JsonDocument document = JsonDocument.Parse(process.Stdout);
        return (process, document.RootElement.Clone());
    }

    public JsonElement ReadResult(string jobId)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(Path.Combine(JobsRoot, jobId, "result.json")));
        return document.RootElement.Clone();
    }

    private string EmptyExcludes()
    {
        string path = Path.Combine(fakeDirectory, "no-excludes");
        File.WriteAllText(path, "");
        return path;
    }

    public static string ChildPath()
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
