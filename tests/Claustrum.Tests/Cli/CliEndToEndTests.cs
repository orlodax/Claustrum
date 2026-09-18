using System.Diagnostics;
using System.Text.Json;
using Claustrum.Cli;

namespace Claustrum.Tests.Cli;

// The exit-code half of docs/PLAN.md §A5 and §B4, driven through the real built binary: several of
// the 2026-09-14 review findings (a JSONC .vscode/mcp.json, `cast create --answers`, a cast file
// with no `roles`) only show as the wrong exit code or a raw stack trace at the process door, which
// no in-process test observes. `claustrum` is on the test output path via the project reference, so
// this needs no publish step. CLAUSTRUM_HOME keeps every job out of the real ~/.claustrum.
public sealed class CliEndToEndTests : IDisposable
{
    private const int Ok = 0;
    private const int Usage = 2;

    private readonly string cwd = Directory.CreateTempSubdirectory("claustrum-e2e-").FullName;
    private readonly string home = Directory.CreateTempSubdirectory("claustrum-e2e-home-").FullName;

    public void Dispose()
    {
        Directory.Delete(cwd, recursive: true);
        Directory.Delete(home, recursive: true);
    }

    [Fact]
    public async Task SyncCheckOnAFreshRepoExitsTwoAndNamesWhatIsMissingAsync()
    {
        (int exitCode, string stdout, _) = await RunAsync("sync", "--check");

        Assert.Equal(Usage, exitCode);
        Assert.Contains("missing:", stdout, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(cwd, ".claude")));
    }

    [Fact]
    public async Task SyncThenSyncCheckExitsZeroAsync()
    {
        Assert.Equal(Ok, (await RunAsync("sync")).ExitCode);

        (int exitCode, string stdout, _) = await RunAsync("sync", "--check");

        Assert.Equal(Ok, exitCode);
        Assert.DoesNotContain("stale:", stdout, StringComparison.Ordinal);
    }

    // The third branch of --check, between "missing" and clean: a generated file that still carries
    // the marker but no longer matches what the library renders.
    [Fact]
    public async Task SyncCheckReportsAStaleGeneratedFileAsync()
    {
        Assert.Equal(Ok, (await RunAsync("sync")).ExitCode);
        string agent = Path.Combine(cwd, ".claude", "agents", "builder.md");
        File.WriteAllText(agent, File.ReadAllText(agent) + "\nhand-edited tail\n");

        (int exitCode, string stdout, _) = await RunAsync("sync", "--check");

        Assert.Equal(Usage, exitCode);
        Assert.Contains("stale:", stdout, StringComparison.Ordinal);
        Assert.Contains("hand-edited tail", File.ReadAllText(agent), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SyncDryRunPrintsAUnifiedDiffAndWritesNothingAsync()
    {
        (int exitCode, string stdout, _) = await RunAsync("sync", "--dry-run");

        Assert.Equal(Ok, exitCode);
        Assert.Contains("--- /dev/null", stdout, StringComparison.Ordinal);
        Assert.Contains("+++ b/", stdout, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(cwd, ".claude")));
    }

    [Fact]
    public async Task SyncOnlyOpencodeWritesOpencodeFilesNotClaudeAsync()
    {
        (int exitCode, _, _) = await RunAsync("sync", "--only", "opencode", "--roles", "builder");

        Assert.Equal(Ok, exitCode);
        Assert.True(File.Exists(Path.Combine(cwd, ".opencode", "agent", "builder.md")));
        Assert.True(File.Exists(Path.Combine(cwd, ".opencode", "command", "claustrum.md")));
        Assert.False(Directory.Exists(Path.Combine(cwd, ".claude")));
    }

    [Fact]
    public async Task SyncOnlyCopilotWritesGithubFilesAsync()
    {
        (int exitCode, _, _) = await RunAsync("sync", "--only", "copilot", "--roles", "builder");

        Assert.Equal(Ok, exitCode);
        Assert.True(File.Exists(Path.Combine(cwd, ".github", "agents", "builder.agent.md")));
        Assert.True(File.Exists(Path.Combine(cwd, ".github", "skills", "claustrum", "SKILL.md")));
        Assert.False(Directory.Exists(Path.Combine(cwd, ".claude")));
    }

    [Fact]
    public async Task SyncOnlyClaudeAndOpencodeWritesBothAsync()
    {
        (int exitCode, string stdout, _) = await RunAsync("sync", "--only", "claude,opencode", "--roles", "builder");

        Assert.Equal(Ok, exitCode);
        Assert.True(File.Exists(Path.Combine(cwd, ".claude", "agents", "builder.md")));
        Assert.True(File.Exists(Path.Combine(cwd, ".opencode", "agent", "builder.md")));
        Assert.Contains("written:", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SyncOnlyUnsupportedHarnessExitsTwoNamingItAsync()
    {
        (int exitCode, _, string stderr) = await RunAsync("sync", "--only", "cursor");

        Assert.Equal(Usage, exitCode);
        Assert.Contains("cursor", stderr, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(cwd, ".claude")));
    }

    [Fact]
    public async Task SyncRefusesBothCheckAndDryRunAsync()
    {
        (int exitCode, _, string stderr) = await RunAsync("sync", "--check", "--dry-run");

        Assert.Equal(Usage, exitCode);
        Assert.Contains("not both", stderr, StringComparison.Ordinal);
    }

    // Finding #1 at the process door: a JSONC `.vscode/mcp.json` used to exit 1; genuinely malformed
    // JSON has to exit 2 with the file named, not a raw JsonException.
    [Fact]
    public async Task SyncToleratesAJsoncVsCodeMcpFileAsync()
    {
        Directory.CreateDirectory(Path.Combine(cwd, ".vscode"));
        File.WriteAllText(Path.Combine(cwd, ".vscode", "mcp.json"), /*lang=json*/ """
            {
              // Managed by hand: see the team wiki
              "servers": { "fetch": { "type": "stdio", "command": "uvx", "args": ["mcp-server-fetch"] }, },
            }
            """);

        (int exitCode, _, string stderr) = await RunAsync("sync");

        Assert.Equal(Ok, exitCode);
        Assert.Contains("fetch", File.ReadAllText(Path.Combine(cwd, ".vscode", "mcp.json")), StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(cwd, ".claustrum", "sync-manifest.json")), $"sync did not run to completion: {stderr}");
    }

    [Fact]
    public async Task SyncOnMalformedJsonExitsTwoNamingTheFileAsync()
    {
        File.WriteAllText(Path.Combine(cwd, ".mcp.json"), "{ this is not json");

        (int exitCode, _, string stderr) = await RunAsync("sync");

        Assert.Equal(Usage, exitCode);
        Assert.Contains(".mcp.json", stderr, StringComparison.Ordinal);
    }

    // Finding #2: the generated skill tells agents to run exactly this, and the CLI used to reject
    // it ("Unrecognized command or argument '--answers'").
    [Fact]
    public async Task CastCreateAcceptsAnswersAsAnOptionAsync()
    {
        File.WriteAllText(Path.Combine(cwd, "answers.json"), /*lang=json,strict*/ """{"builder":"claude:opus","budget":"no cap"}""");

        (int exitCode, _, string stderr) = await RunAsync("cast", "create", "--answers", "answers.json");

        Assert.Equal(Ok, exitCode);
        Assert.True(File.Exists(Path.Combine(cwd, ".claustrum", "casts", "default.json")), stderr);
    }

    // Finding #8. The guard against finding #2 masking this one: an argument-parse failure also
    // exits 2 and echoes the file name, so assert the message is the JSON error, not "Unrecognized".
    [Fact]
    public async Task CastCreateOnAMalformedAnswersFileExitsTwoNamingTheFileAsync()
    {
        File.WriteAllText(Path.Combine(cwd, "answers.json"), /*lang=json,strict*/ """{"builder":1}""");

        (int exitCode, _, string stderr) = await RunAsync("cast", "create", "--answers", "answers.json");

        Assert.Equal(Usage, exitCode);
        Assert.Contains("answers.json", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("Unrecognized", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CastCreateShowUseAndListRoundTripAsync()
    {
        File.WriteAllText(Path.Combine(cwd, "answers.json"), /*lang=json,strict*/ """{"builder":"claude:haiku","budget":"3"}""");
        Assert.Equal(Ok, (await RunAsync("cast", "create", "--answers", "answers.json", "--name", "staging")).ExitCode);

        (int showExit, string showOut, _) = await RunAsync("cast", "show", "staging");
        Assert.Equal(Ok, showExit);
        Assert.Contains("claude:haiku", showOut, StringComparison.Ordinal);

        Assert.Equal(Ok, (await RunAsync("cast", "use", "staging")).ExitCode);

        (int listExit, string listOut, _) = await RunAsync("cast", "list");
        Assert.Equal(Ok, listExit);
        Assert.Contains("default", listOut, StringComparison.Ordinal);
        Assert.Contains("staging", listOut, StringComparison.Ordinal);

        // `use` copies the cast over default.json — the copy must keep the source's role entries.
        Assert.Contains("claude:haiku", (await RunAsync("cast", "show", "default")).Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CastShowForAnUnknownCastExitsTwoAsync()
    {
        (int exitCode, _, string stderr) = await RunAsync("cast", "show", "nope");

        Assert.Equal(Usage, exitCode);
        Assert.Contains("nope", stderr, StringComparison.Ordinal);
    }

    // Finding #3: default.json applies itself to every run, so a cast file with no "roles" key used
    // to NRE ("Value cannot be null") on every run in the repo instead of naming the broken file.
    [Fact]
    public async Task RunWithACastFileMissingRolesExitsTwoNamingTheFileAsync()
    {
        const string noRoles = /*lang=json,strict*/ """{"name":"default","library":"1.0.0","architect":{"mode":"host"},"budget_usd":null}""";
        Directory.CreateDirectory(Path.Combine(cwd, ".claustrum", "casts"));
        File.WriteAllText(Path.Combine(cwd, ".claustrum", "casts", "default.json"), noRoles);

        (int exitCode, _, string stderr) = await RunAsync("run", "builder", "--brief", "hello");

        Assert.Equal(Usage, exitCode);
        Assert.Contains("roles", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("Value cannot be null", stderr, StringComparison.Ordinal);
    }

    // Finding #7 at the process door: the gate fires before anything spawns, and must leave no job
    // directory behind (JobDirectory.Create used to run as an argument, ahead of the gate).
    [Fact]
    public async Task BlindGateRejectionExitsTwoAndLeavesNoJobDirectoryAsync()
    {
        File.WriteAllText(Path.Combine(cwd, "brief.md"), "## Task\nreview it\n\n## Context\nwhy we did it\n");

        (int exitCode, _, string stderr) = await RunAsync("run", "code-reviewer", "--brief-file", "brief.md");

        Assert.Equal(Usage, exitCode);
        Assert.Contains("blind", stderr, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(Path.Combine(home, "jobs")));
    }

    // docs/PLAN.md §D4 "claustrum jobs clean removes worktrees of finished jobs". backend
    // "nonexistent" reaches Runner's registry check *after* DelegateEngine has already created the
    // worktree (max_parallel > 1), so the job still finishes (status backend_missing, result.json
    // written) with a real worktree on disk to clean up — no real backend install needed, same trick
    // DelegateEngineTests uses in-process.
    [Fact]
    public async Task JobsCleanRemovesAFinishedWorktreeAndKeepsItsBranchAsync()
    {
        RunGit(cwd, "init", "-q");
        RunGit(cwd, "config", "user.email", "test@example.com");
        RunGit(cwd, "config", "user.name", "claustrum-tests");
        File.WriteAllText(Path.Combine(cwd, "seed.txt"), "seed\n");
        RunGit(cwd, "add", "-A");
        RunGit(cwd, "commit", "-q", "-m", "seed");
        Directory.CreateDirectory(Path.Combine(cwd, ".claustrum", "casts"));
        File.WriteAllText(Path.Combine(cwd, ".claustrum", "casts", "default.json"), /*lang=json,strict*/
            """{"name":"default","library":"1.0.0","architect":{"mode":"host"},"roles":{"builder":{"backend":"nonexistent","max_parallel":2}},"budget_usd":null}""");

        (int runExit, string runOut, string runErr) = await RunAsync("run", "builder", "--brief", "hi", "--json");
        Assert.Equal(ExitCodes.BackendMissing, runExit);
        string jobId = JsonDocument.Parse(runOut).RootElement.GetProperty("job_id").GetString()!;
        string worktreePath = Path.Combine(cwd, ".claustrum", "worktrees", jobId);
        Assert.True(Directory.Exists(worktreePath), runErr);

        (int cleanExit, string cleanOut, _) = await RunAsync("jobs", "clean");

        Assert.Equal(Ok, cleanExit);
        Assert.Contains(jobId, cleanOut, StringComparison.Ordinal);
        Assert.False(Directory.Exists(worktreePath));
        Assert.Contains($"claustrum/{jobId}", ListBranches(cwd));
    }

    [Fact]
    public async Task JobsCleanOnARepoWithNoWorktreesIsANoOpAsync()
    {
        (int exitCode, string stdout, _) = await RunAsync("jobs", "clean");

        Assert.Equal(Ok, exitCode);
        Assert.Contains("no worktrees", stdout, StringComparison.Ordinal);
    }

    private static string[] ListBranches(string dir)
    {
        ProcessStartInfo startInfo = new("git")
        {
            WorkingDirectory = dir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("branch");
        startInfo.ArgumentList.Add("--format=%(refname:short)");

        using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("git failed to start");
        string stdout = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static void RunGit(string cwd, params string[] args)
    {
        ProcessStartInfo startInfo = new("git")
        {
            WorkingDirectory = cwd,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (string arg in args)
            startInfo.ArgumentList.Add(arg);

        using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("git failed to start");
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed ({process.ExitCode}): {stderr}");
    }

    [Fact]
    public async Task BareDoctorNeverPrintsProbeSectionsAsync()
    {
        (int exitCode, string stdout, _) = await RunAsync("backends", "doctor");

        Assert.Equal(Ok, exitCode);
        Assert.DoesNotContain("auth:", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("os:", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("mcp:", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DoctorProbeAddsAuthOsAndMcpSectionsAsync()
    {
        (int exitCode, string stdout, _) = await RunAsync("backends", "doctor", "--probe");

        Assert.Equal(Ok, exitCode);
        Assert.Contains("auth:", stdout, StringComparison.Ordinal);
        Assert.Contains("os:", stdout, StringComparison.Ordinal);
        Assert.Contains("mcp:", stdout, StringComparison.Ordinal);
        Assert.Contains(".mcp.json:", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DoctorProbeReportsNoMcpFileOnAFreshRepoAsync()
    {
        (int exitCode, string stdout, _) = await RunAsync("backends", "doctor", "--probe");

        Assert.Equal(Ok, exitCode);
        Assert.Contains(".mcp.json:        not present", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DoctorProbeDetectsAnExistingMcpRegistrationAsync()
    {
        File.WriteAllText(Path.Combine(cwd, ".mcp.json"), /*lang=json,strict*/
            """{"mcpServers":{"claustrum":{"command":"claustrum","args":["mcp"]}}}""");

        (int exitCode, string stdout, _) = await RunAsync("backends", "doctor", "--probe");

        Assert.Equal(Ok, exitCode);
        Assert.Contains(".mcp.json:        registers claustrum", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DoctorProbeToleratesJsoncInVsCodeMcpFileAsync()
    {
        Directory.CreateDirectory(Path.Combine(cwd, ".vscode"));
        File.WriteAllText(Path.Combine(cwd, ".vscode", "mcp.json"), /*lang=json*/ """
            {
              // hand-edited
              "servers": { "claustrum": { "type": "stdio", "command": "claustrum", "args": ["mcp"] }, },
            }
            """);

        (int exitCode, string stdout, _) = await RunAsync("backends", "doctor", "--probe");

        Assert.Equal(Ok, exitCode);
        Assert.Contains(".vscode/mcp.json: registers claustrum", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InitScaffoldsClaustrumDirectoryAndClaudeSyncOnAFreshRepoAsync()
    {
        (int exitCode, string stdout, _) = await RunAsync("init");

        Assert.Equal(Ok, exitCode);
        Assert.True(Directory.Exists(Path.Combine(cwd, ".claustrum", "casts")));
        Assert.True(Directory.Exists(Path.Combine(cwd, ".claustrum", "briefs")));
        Assert.True(Directory.Exists(Path.Combine(cwd, ".claustrum", "worktrees")));
        Assert.True(File.Exists(Path.Combine(cwd, "claustrum.json")));
        Assert.Contains("cheap-coding", File.ReadAllText(Path.Combine(cwd, "claustrum.json")), StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(cwd, ".claude", "agents", "builder.md")));
        Assert.Contains(".claustrum/worktrees/", File.ReadAllText(Path.Combine(cwd, ".gitignore")), StringComparison.Ordinal);
        Assert.Contains("claude:", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InitDetectsGithubDirectoryAndAlsoSyncsCopilotAsync()
    {
        Directory.CreateDirectory(Path.Combine(cwd, ".github"));

        (int exitCode, string stdout, _) = await RunAsync("init");

        Assert.Equal(Ok, exitCode);
        Assert.Contains("copilot:", stdout, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(cwd, ".github", "agents", "builder.agent.md")));
        Assert.True(File.Exists(Path.Combine(cwd, ".github", "skills", "claustrum", "SKILL.md")));
    }

    [Fact]
    public async Task InitWithAllSyncsEveryHarnessRegardlessOfDetectionAsync()
    {
        (int exitCode, string stdout, _) = await RunAsync("init", "--all");

        Assert.Equal(Ok, exitCode);
        Assert.Contains("claude:", stdout, StringComparison.Ordinal);
        Assert.Contains("opencode:", stdout, StringComparison.Ordinal);
        Assert.Contains("copilot:", stdout, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(cwd, ".opencode", "agent", "builder.md")));
    }

    [Fact]
    public async Task InitAppendsAPointerToAnExistingAgentsMdButNeverCreatesOneAsync()
    {
        File.WriteAllText(Path.Combine(cwd, "AGENTS.md"), "# House rules\n");

        (int exitCode, _, _) = await RunAsync("init");

        Assert.Equal(Ok, exitCode);
        string agentsMd = File.ReadAllText(Path.Combine(cwd, "AGENTS.md"));
        Assert.Contains("# House rules", agentsMd, StringComparison.Ordinal);
        Assert.Contains("Claustrum delegation", agentsMd, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(cwd, "CLAUDE.md")));
    }

    [Fact]
    public async Task InitNeverCreatesAnAgentsMdThatDidNotExistAsync()
    {
        (int exitCode, _, _) = await RunAsync("init");

        Assert.Equal(Ok, exitCode);
        Assert.False(File.Exists(Path.Combine(cwd, "AGENTS.md")));
    }

    [Fact]
    public async Task InitIsIdempotentOnRerunAsync()
    {
        Assert.Equal(Ok, (await RunAsync("init")).ExitCode);
        string configBefore = File.ReadAllText(Path.Combine(cwd, "claustrum.json"));

        (int exitCode, string stdout, _) = await RunAsync("init");

        Assert.Equal(Ok, exitCode);
        Assert.Equal(configBefore, File.ReadAllText(Path.Combine(cwd, "claustrum.json")));
        Assert.Contains("already present", stdout, StringComparison.Ordinal);
        Assert.Contains("0 written, 15 skipped", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InitDoesNotDuplicateTheAgentsMdPointerOnRerunAsync()
    {
        File.WriteAllText(Path.Combine(cwd, "AGENTS.md"), "# House rules\n");
        Assert.Equal(Ok, (await RunAsync("init")).ExitCode);

        Assert.Equal(Ok, (await RunAsync("init")).ExitCode);

        string agentsMd = File.ReadAllText(Path.Combine(cwd, "AGENTS.md"));
        int occurrences = agentsMd.Split("## Claustrum delegation").Length - 1;
        Assert.Equal(1, occurrences);
    }

    private async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(params string[] args)
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

        using Process process = Process.Start(startInfo)!;
        process.StandardInput.Close();
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        Task<string> stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);

        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(60));
        await process.WaitForExitAsync(timeout.Token);

        return (process.ExitCode, await stdout, await stderr);
    }
}
