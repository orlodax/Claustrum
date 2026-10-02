using System.Text.Json;
using Claustrum.Casts;
using Claustrum.Cli;
using Claustrum.Tests.Delegation;
using Claustrum.Tests.Testing;

namespace Claustrum.Tests.Cli;

// The process-door half of #43's harness check, through the real built binary: a role resolved to a
// harness its role.json does not list exits 2 with the message on stderr and mints no job — the in-process
// tests (HarnessRefusalTests) cannot see the exit code or the stream. Every refusal throws before a backend
// is looked up, and the one run that does reach the registry targets "nonexistent"; PATH is emptied for the
// child anyway, so no installed backend could be started even if a refusal were ever lost.
public sealed class HarnessRefusalEndToEndTests : IDisposable
{
    private const string Sources = "--backend/--model, the cast or claustrum.json";

    private readonly ClaustrumCli cli = new();
    private readonly string emptyPath;

    public HarnessRefusalEndToEndTests() => emptyPath = Directory.CreateDirectory(Path.Combine(cli.Home, "empty-path")).FullName;

    public void Dispose() => cli.Dispose();

    private Task<CliResult> RunAsync(params string[] args) => cli.RunAsync(args, stdin: "", pathOverride: emptyPath);

    [Theory]
    [MemberData(nameof(HarnessRefusalTests.BrowserRolesOnOtherHarnesses), MemberType = typeof(HarnessRefusalTests))]
    public async Task RunRefusesABrowserRoleOnAnotherHarnessExitingTwoAndMintingNoJobAsync(string role, string backend)
    {
        CliResult result = await RunAsync("run", role, "--backend", backend, "--brief", "hi");

        Assert.Equal(ExitCodes.Usage, result.ExitCode);
        Assert.Contains($"role '{role}' runs on claude only", result.Stderr, StringComparison.Ordinal);
        Assert.Contains($"'{backend}'", result.Stderr, StringComparison.Ordinal);
        Assert.Contains(Sources, result.Stderr, StringComparison.Ordinal);
        Assert.Contains($".claustrum/roles/{role}/role.json", result.Stderr, StringComparison.Ordinal);
        Assert.False(Directory.Exists(cli.JobsDirectory));
    }

    [Fact]
    public async Task RunRefusesABuilderOnApiAsync()
    {
        CliResult result = await RunAsync("run", "builder", "--backend", "api", "--brief", "hi");

        Assert.Equal(ExitCodes.Usage, result.ExitCode);
        Assert.Contains("role 'builder' runs on claude, opencode, cursor, copilot only", result.Stderr, StringComparison.Ordinal);
        Assert.False(Directory.Exists(cli.JobsDirectory));
    }

    [Fact]
    public async Task RunRefusesADemoAuthorWhoseModelFlagLandsOnAnotherHarnessAsync()
    {
        CliResult result = await RunAsync("run", "demo-author", "--model", "opencode:some-model", "--brief", "hi");

        Assert.Equal(ExitCodes.Usage, result.ExitCode);
        Assert.Contains("role 'demo-author' runs on claude only", result.Stderr, StringComparison.Ordinal);
        Assert.False(Directory.Exists(cli.JobsDirectory));
    }

    // The cast routes the demo-author with no flag at all: default.json applies itself, and the refusal
    // names the same three places a person could fix it.
    [Fact]
    public async Task RunRefusesADemoAuthorTheDefaultCastPutsOnAnotherHarnessAsync()
    {
        CastStore.Save(cli.Cwd, new Cast(
            "default", "1.0.0", new CastArchitect(CastArchitect.Host),
            new Dictionary<string, CastRoleEntry?> { ["demo-author"] = new CastRoleEntry(Model: "cursor:some-model", Backend: null, Tier: null) },
            BudgetUsd: null));

        CliResult result = await RunAsync("run", "demo-author", "--brief", "hi");

        Assert.Equal(ExitCodes.Usage, result.ExitCode);
        Assert.Contains("'cursor'", result.Stderr, StringComparison.Ordinal);
        Assert.Contains(Sources, result.Stderr, StringComparison.Ordinal);
        Assert.False(Directory.Exists(cli.JobsDirectory));
    }

    [Fact]
    public async Task CoordinateRefusesAnArchitectTheCastSpawnsOnApiAndMintsNoJobAsync()
    {
        CastStore.Save(cli.Cwd, new Cast("default", "1.0.0", new CastArchitect(CastArchitect.Spawned, Model: "api:some-model"), [], null));

        CliResult result = await RunAsync("coordinate", "--brief", "x");

        Assert.Equal(ExitCodes.Usage, result.ExitCode);
        Assert.Contains("role 'architect' runs on claude, opencode, cursor, copilot only", result.Stderr, StringComparison.Ordinal);
        Assert.Contains(".claustrum/roles/architect/role.json", result.Stderr, StringComparison.Ordinal);
        Assert.False(Directory.Exists(cli.JobsDirectory));
    }

    // An unregistered backend name is not judged by the check: it reaches Runner's own registry check and
    // ends backend_missing (exit 3), with the rendered system prompt already written to the job.
    [Fact]
    public async Task RunOfABuilderOnAnUnregisteredBackendStillExitsThreeWithItsSystemPromptWrittenAsync()
    {
        CliResult result = await RunAsync("run", "builder", "--backend", "nonexistent", "--brief", "hi", "--json");

        Assert.Equal(ExitCodes.BackendMissing, result.ExitCode);
        using JsonDocument document = JsonDocument.Parse(result.Stdout);
        Assert.Equal("backend_missing", document.RootElement.GetProperty("status").GetString());
        string jobId = document.RootElement.GetProperty("job_id").GetString()!;
        Assert.True(File.Exists(Path.Combine(cli.JobsDirectory, jobId, "system.md")));
    }

    [Fact]
    public async Task RunOfADemoAuthorOnAnUnregisteredBackendFailsAtRenderNamingTheBrowserPartAndMintsNoJobAsync()
    {
        CliResult result = await RunAsync("run", "demo-author", "--backend", "nonexistent", "--brief", "hi");

        Assert.Equal(ExitCodes.Usage, result.ExitCode);
        Assert.Contains("part 'browser'", result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("runs on", result.Stderr, StringComparison.Ordinal);
        Assert.False(Directory.Exists(cli.JobsDirectory));
    }
}
