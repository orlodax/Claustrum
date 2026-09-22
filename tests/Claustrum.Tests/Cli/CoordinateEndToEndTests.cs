using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Claustrum.Cli;
using Claustrum.Tests.Testing;

namespace Claustrum.Tests.Cli;

// The exit-code/process-door half of docs/PLAN.md §D3/§D5's `claustrum coordinate`, mirroring
// CliEndToEndTests' own pattern (real built binary, CLAUSTRUM_HOME isolated) but split into its own
// file since `coordinate` is a whole new verb rather than an addition to an existing one.
public sealed class CoordinateEndToEndTests : IDisposable
{
    private readonly string cwd = Directory.CreateTempSubdirectory("claustrum-coordinate-e2e-").FullName;
    private readonly string home = Directory.CreateTempSubdirectory("claustrum-coordinate-e2e-home-").FullName;

    public void Dispose()
    {
        TempTree.Delete(cwd);
        TempTree.Delete(home);
    }

    [Fact]
    public async Task NoCastExitsTwoAndStartsNoJobAsync()
    {
        (int exitCode, _, string stderr) = await RunAsync("coordinate", "--brief", "x");

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Contains("cast", stderr, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(Path.Combine(home, "jobs")));
    }

    [Fact]
    public async Task IssuesAndBriefTogetherExitsTwoAsync()
    {
        WriteDefaultCast();

        (int exitCode, _, string stderr) = await RunAsync("coordinate", "--issues", "12", "--brief", "x");

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Contains("not both", stderr, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(home, "jobs")));
    }

    [Fact]
    public async Task UnparsableIssuesExitsTwoAsync()
    {
        WriteDefaultCast();

        (int exitCode, _, string stderr) = await RunAsync("coordinate", "--issues", "abc");

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Contains("--issues", stderr, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(home, "jobs")));
    }

    [Fact]
    public async Task NonPositiveTimeoutExitsTwoAndStartsNoJobAsync()
    {
        WriteDefaultCast();

        (int exitCode, _, string stderr) = await RunAsync("coordinate", "--brief", "x", "--timeout", "0");

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Contains("--timeout", stderr, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(home, "jobs")));
    }

    // issue #23: CoordinatePlan.Prepare (config, role, model) runs before JobDirectory.Create on both
    // doors — a syntactically broken claustrum.json is this invocation's own exit-2 error, with
    // nothing minted for it. claustrum.json is only read from the git root (Config.Load), so this
    // needs a real repo, unlike every other usage-error test in this file.
    [Fact]
    public async Task BrokenClaustrumJsonExitsTwoAndStartsNoJobAsync()
    {
        InitGitRepo();
        WriteDefaultCast();
        File.WriteAllText(Path.Combine(cwd, "claustrum.json"), "{ not json");

        (int exitCode, _, string stderr) = await RunAsync(["coordinate", "--brief", "x"], PathStrippedToGit());

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Contains("claustrum.json", stderr, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(home, "jobs")));
    }

    // model "nonexistent:x" (Config.ResolveModel's "[backend:]model-id" grammar) resolves to backend
    // "nonexistent", so Runner rejects it and exits BackendMissing before spawning anything real — the
    // same trick DelegateEngineTests/CliEndToEndTests use throughout, here reached via the cast's own
    // architect.model rather than a CLI flag.
    [Fact]
    public async Task WithACastAndBriefJsonModeExitsBackendMissingWithARunResultForTheArchitectAsync()
    {
        WriteDefaultCast();

        (int exitCode, string stdout, string stderr) = await RunAsync("coordinate", "--brief", "x", "--json");

        Assert.Equal(ExitCodes.BackendMissing, exitCode);
        JsonElement root = JsonDocument.Parse(stdout).RootElement;
        Assert.Equal("architect", root.GetProperty("role").GetString());
        Assert.Equal("backend_missing", root.GetProperty("status").GetString());
        string jobId = root.GetProperty("job_id").GetString()!;

        string systemMd = File.ReadAllText(Path.Combine(home, "jobs", jobId, "system.md"));
        int coordination = systemMd.IndexOf("## Coordination", StringComparison.Ordinal);
        int houseRules = systemMd.IndexOf("\n## House rules\n", StringComparison.Ordinal);
        Assert.True(coordination >= 0, stderr);
        Assert.True(houseRules >= 0, stderr);
        Assert.True(coordination < houseRules);

        // The real job id, not DelegateRequest.JobIdToken: the three lines the appendix names it on
        // (Job tree/Inspect/Work branch — CoordinationBrief.RenderSystemAppendix) all substituted by
        // PreparedDelegation.ForJob once the directory was minted (issue #23), and no `{{job_id}}`
        // survives anywhere in the two files this job wrote.
        Assert.Contains($"Job tree: {jobId}", systemMd, StringComparison.Ordinal);
        Assert.Contains($"Inspect: claustrum jobs budget {jobId}", systemMd, StringComparison.Ordinal);
        Assert.Contains($"Work branch: claustrum/{jobId}", systemMd, StringComparison.Ordinal);
        Assert.DoesNotContain("{{job_id}}", systemMd, StringComparison.Ordinal);

        string requestJsonText = File.ReadAllText(Path.Combine(home, "jobs", jobId, "request.json"));
        JsonElement request = JsonDocument.Parse(requestJsonText).RootElement;
        Assert.Equal(jobId, request.GetProperty("env").GetProperty("CLAUSTRUM_PARENT_JOB").GetString());
        Assert.DoesNotContain("{{job_id}}", requestJsonText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HumanModePrintsUnlimitedTreeLineForAnUncappedCastAsync()
    {
        WriteDefaultCast(budgetUsd: null);

        (int exitCode, string stdout, _) = await RunAsync("coordinate", "--brief", "x");

        Assert.Equal(ExitCodes.BackendMissing, exitCode);
        Assert.Contains("tree: unlimited (children not accounted)", stdout, StringComparison.Ordinal);
    }

    // CoordinateEngine.NeverRan (2026-09-22 follow-up review): a backend_missing architect never
    // spawned a process, so RecordFinishedAsync must not write a row for it — the exact clause
    // (architect $X included)" only appears once there is a row to name, and a $0 row would have
    // materialised a ledger directory `jobs budget` and this shortcut would then both read as "some
    // child ran" forever after.
    [Fact]
    public async Task HumanModePrintsTreeSpentLineForACappedCastAsync()
    {
        WriteDefaultCast(budgetUsd: 5.00m);

        (int exitCode, string stdout, _) = await RunAsync("coordinate", "--brief", "x");

        Assert.Equal(ExitCodes.BackendMissing, exitCode);
        Assert.Contains("tree spent $0.00, reserved $0.00", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("architect $", stdout, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(home, "budget")));
    }

    // issue #21: the architect's own run is deliberately not a member of its own tree (nothing
    // reserved its cap), so CoordinateEngine.RunAsync records it as a *finished* entry once it
    // returns — `jobs budget <tree>` then totals the whole tree, not just the children, and the
    // human line names the architect's share of it. "claude" here is `backends.claude.path` pointed
    // at a tiny script that prints a claude-shaped JSON result (NOTES.md's own measured example), a
    // real backend name (Config.ResolveBackend needs one to reach BinaryLocator at all) resolved to a
    // fake executable rather than the real one — never a real backend invocation: `Config.Load` only
    // reads `claustrum.json` from the git root, so `cwd` is a real repo with the override committed
    // right there, and PATH is stripped to `git`'s own directory (McpStdioServerTests' own trick:
    // "no real backend can be invoked from a test") as a second, independent guard in case the first
    // one is ever wrong.
    [Fact]
    public async Task ArchitectsOwnCostIsRecordedInTheTreeLedgerAsync()
    {
        InitGitRepo();
        WriteConfigWithFakeClaudeBackend(costUsd: 0.25m);
        WriteDefaultCast(budgetUsd: 10.00m, architectModel: "claude:opus");

        (int exitCode, string stdout, string stderr) = await RunAsync(["coordinate", "--brief", "x"], PathStrippedToGit());

        Assert.Equal(ExitCodes.Ok, exitCode);
        // These two must be checked before the "done" substring below: a parse failure that falls
        // back to raw text still contains "done" (it's inside the raw JSON), so asserting "done"
        // alone cannot catch ClaudeBackend.Parse silently not firing (2026-09-22, PR #27's
        // windows-latest leg passed this way with cost_usd null and no "architect cost" line).
        Assert.Contains("architect cost $0.25", stdout, StringComparison.Ordinal);
        Assert.Contains("tree spent $0.25 (architect $0.25 included), reserved $0.00", stdout, StringComparison.Ordinal);
        // Exactly the fake script's own output, not a real model's reply — the strongest proof this
        // never reached a real backend.
        Assert.Contains("done", stdout, StringComparison.Ordinal);

        string jobId = ExtractJobId(stdout, stderr);
        (int budgetExitCode, string budgetStdout, _) = await RunAsync(["jobs", "budget", jobId], PathStrippedToGit());

        Assert.Equal(ExitCodes.Ok, budgetExitCode);
        Assert.Contains($"{jobId}  architect     cap   $10.00  cost    $0.25  done", budgetStdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BareDoctorIncludesTheGhBlockAsync()
    {
        (int exitCode, string stdout, _) = await RunAsync("backends", "doctor");

        Assert.Equal(0, exitCode);
        Assert.Contains("gh:", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DoctorForOneBackendOmitsTheGhBlockAsync()
    {
        (int exitCode, string stdout, _) = await RunAsync("backends", "doctor", "claude");

        Assert.Equal(0, exitCode);
        Assert.DoesNotContain("gh:", stdout, StringComparison.Ordinal);
    }

    // architect.model carries the backend prefix, not --model, so `coordinate` never needs a real
    // backend on PATH to reach a terminal RunResult — unless a test wants one to actually "run"
    // (ArchitectsOwnCostIsRecordedInTheTreeLedgerAsync), in which case architectModel names "claude"
    // and WriteConfigWithFakeClaudeBackend points that backend at a script instead.
    private void WriteDefaultCast(decimal? budgetUsd = null, string architectModel = "nonexistent:x")
    {
        Directory.CreateDirectory(Path.Combine(cwd, ".claustrum", "casts"));
        string budgetJson = budgetUsd is { } value ? value.ToString(CultureInfo.InvariantCulture) : "null";
        File.WriteAllText(Path.Combine(cwd, ".claustrum", "casts", "default.json"), /*lang=json,strict*/
            $$"""{"name":"default","library":"1.0.0","architect":{"mode":"spawned","model":"{{architectModel}}"},"roles":{},"budget_usd":{{budgetJson}}}""");
    }

    // "backends.claude.path" pointed at a tiny script that prints a claude-shaped JSON result — not
    // a real `claude` invocation (BinaryLocator.Locate honours a BackendConfig.Path override ahead
    // of PATH search), the CLI-process-level equivalent of FakeBackend.RepliesOk one layer down.
    private void WriteConfigWithFakeClaudeBackend(decimal costUsd)
    {
        string scriptPath = Path.Combine(cwd, OperatingSystem.IsWindows() ? "fake-claude.cmd" : "fake-claude.sh");
        string costLiteral = costUsd.ToString(CultureInfo.InvariantCulture);
        string json = $$"""{"result":"done","total_cost_usd":{{costLiteral}},"is_error":false}""";

        if (OperatingSystem.IsWindows())
        {
            // cmd.exe's `echo` has no `\"` escape — a backslash-quote prints literally, so
            // ClaudeBackend.Parse sees `{\"result\":...}` and falls back to raw text instead of
            // JSON (2026-09-22, PR #27's windows-latest leg: exit 0 and "done" in stdout, but no
            // "architect cost" line because cost_usd stayed null). `echo` prints an unescaped `"`
            // as-is, so the JSON goes out verbatim; it contains none of cmd's own metacharacters
            // (`&`, `|`, `<`, `>`, `^`, `%`) that this call would need to guard against.
            File.WriteAllText(scriptPath, $"@echo off\r\necho {json}\r\n");
        }
        else
        {
            File.WriteAllText(scriptPath, $"#!/bin/sh\necho '{json}'\n");
            File.SetUnixFileMode(scriptPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }

        // Built with a placeholder + Replace rather than a raw-string interpolation hole immediately
        // followed by three literal closing braces — C# raw-string interpolation cannot tell the
        // hole's own delimiter from adjacent literal braces past a certain run length (CS9007).
        string configJson = /*lang=json,strict*/ """{"backends":{"claude":{"path":"SCRIPT_PATH"}}}""";
        File.WriteAllText(Path.Combine(cwd, "claustrum.json"), configJson.Replace("SCRIPT_PATH", scriptPath.Replace("\\", "\\\\")));
    }

    // The architect's job id from `coordinate`'s human-mode output ("job:    <id>"), so a caller can
    // hand it to `jobs budget` without also asking for --json (which would print a second, competing
    // RunResult document on stdout — CoordinateCommand's own "--json keeps stdout to exactly one" rule).
    private static string ExtractJobId(string stdout, string stderr)
    {
        Match match = Regex.Match(stdout, @"^job:\s+(\S+)", RegexOptions.Multiline);
        Assert.True(match.Success, $"no 'job:' line in stdout:\n{stdout}\nstderr:\n{stderr}");
        return match.Groups[1].Value;
    }

    // git init once, right in cwd, so cwd IS the git root Config.Load reads claustrum.json from
    // (GitRootLocator walks up from cwd looking for .git) — ArchitectsOwnCostIsRecordedInTheTreeLedgerAsync's
    // backends.claude.path override is otherwise silently never read.
    private void InitGitRepo()
    {
        RunGit("init", "-q");
        RunGit("config", "user.email", "test@example.com");
        RunGit("config", "user.name", "claustrum-tests");
        File.WriteAllText(Path.Combine(cwd, "seed.txt"), "seed\n");
        RunGit("add", "-A");
        RunGit("commit", "-q", "-m", "seed");
    }

    private void RunGit(params string[] args)
    {
        ProcessStartInfo startInfo = new("git") { WorkingDirectory = cwd, UseShellExecute = false, CreateNoWindow = true };
        foreach (string arg in args)
            startInfo.ArgumentList.Add(arg);

        using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("git failed to start");
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed ({process.ExitCode})");
    }

    // McpStdioServerTests' own trick: PATH stripped to nothing but git's own directory, so a real
    // `claude` that happens to be installed on the machine running this test cannot be invoked even
    // if the backends.claude.path override were ever silently ignored — a second, independent guard
    // next to the git-root config placement above.
    private static Dictionary<string, string> PathStrippedToGit() => new() { ["PATH"] = GitDirectory() };

    private static string GitDirectory()
    {
        string executable = OperatingSystem.IsWindows() ? "git.exe" : "git";
        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (File.Exists(Path.Combine(directory, executable)))
                return directory;
        }

        throw new InvalidOperationException("git was not found on PATH; this test needs it");
    }

    private Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(params string[] args) => RunAsync(args, extraEnv: null);

    private async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(string[] args, Dictionary<string, string>? extraEnv)
    {
        string binary = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "claustrum.exe" : "claustrum");
        Assert.True(File.Exists(binary), $"built claustrum binary not found at '{binary}'");

        ProcessStartInfo startInfo = new()
        {
            FileName = binary,
            WorkingDirectory = cwd,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
        };
        foreach (string arg in args)
            startInfo.ArgumentList.Add(arg);
        startInfo.Environment["CLAUSTRUM_HOME"] = home;
        startInfo.Environment["CLAUSTRUM_SKIP_PROBE"] = "1";
        if (extraEnv is not null)
            foreach (KeyValuePair<string, string> entry in extraEnv)
                startInfo.Environment[entry.Key] = entry.Value;

        using Process process = Process.Start(startInfo)!;
        process.StandardInput.Close();
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        Task<string> stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);

        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(60));
        await process.WaitForExitAsync(timeout.Token);

        return (process.ExitCode, await stdout, await stderr);
    }
}
