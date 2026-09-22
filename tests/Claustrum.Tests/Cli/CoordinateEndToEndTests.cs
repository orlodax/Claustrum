using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
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

        JsonElement request = JsonDocument.Parse(File.ReadAllText(Path.Combine(home, "jobs", jobId, "request.json"))).RootElement;
        Assert.Equal(jobId, request.GetProperty("env").GetProperty("CLAUSTRUM_PARENT_JOB").GetString());
    }

    [Fact]
    public async Task HumanModePrintsUnlimitedTreeLineForAnUncappedCastAsync()
    {
        WriteDefaultCast(budgetUsd: null);

        (int exitCode, string stdout, _) = await RunAsync("coordinate", "--brief", "x");

        Assert.Equal(ExitCodes.BackendMissing, exitCode);
        Assert.Contains("tree: unlimited (children not accounted)", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HumanModePrintsTreeSpentLineForACappedCastAsync()
    {
        WriteDefaultCast(budgetUsd: 5.00m);

        (int exitCode, string stdout, _) = await RunAsync("coordinate", "--brief", "x");

        Assert.Equal(ExitCodes.BackendMissing, exitCode);
        Assert.Contains("tree spent $0.00, reserved $0.00", stdout, StringComparison.Ordinal);
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
    // backend on PATH to reach a terminal RunResult.
    private void WriteDefaultCast(decimal? budgetUsd = null)
    {
        Directory.CreateDirectory(Path.Combine(cwd, ".claustrum", "casts"));
        string budgetJson = budgetUsd is { } value ? value.ToString(CultureInfo.InvariantCulture) : "null";
        File.WriteAllText(Path.Combine(cwd, ".claustrum", "casts", "default.json"), /*lang=json,strict*/
            $$"""{"name":"default","library":"1.0.0","architect":{"mode":"spawned","model":"nonexistent:x"},"roles":{},"budget_usd":{{budgetJson}}}""");
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
        startInfo.Environment["CLAUSTRUM_SKIP_PROBE"] = "1";

        using Process process = Process.Start(startInfo)!;
        process.StandardInput.Close();
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        Task<string> stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);

        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(60));
        await process.WaitForExitAsync(timeout.Token);

        return (process.ExitCode, await stdout, await stderr);
    }
}
