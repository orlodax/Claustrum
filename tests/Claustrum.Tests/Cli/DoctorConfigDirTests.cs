using Claustrum.Cli;
using Claustrum.Tests.Testing;

namespace Claustrum.Tests.Cli;

// #57: `backends doctor` says which Claude Code home a delegated claude will read — the CLAUDE_CONFIG_DIR
// EnvAllowList now passes — without `--probe`: reading a variable is free, and a path is not a secret. PATH is
// emptied so no backend on the machine counts as installed, and CLAUDE_CONFIG_DIR is set or removed on the
// child explicitly, so the developer's own export cannot decide the answer.
public sealed class DoctorConfigDirTests : IDisposable
{
    private readonly ClaustrumCli cli = new();
    private readonly string emptyPath;

    public DoctorConfigDirTests() => emptyPath = Directory.CreateDirectory(Path.Combine(cli.Home, "empty-path")).FullName;

    public void Dispose() => cli.Dispose();

    private Task<CliResult> RunAsync(string? configDir, params string[] args) =>
        cli.RunAsync(args, "", emptyPath, new Dictionary<string, string?> { ["CLAUDE_CONFIG_DIR"] = configDir });

    [Fact]
    public async Task DoctorClaudePrintsTheConfigDirFromTheVariableWithoutProbeAsync()
    {
        CliResult result = await RunAsync("/home/me/.claude-work", "backends", "doctor", "claude");

        Assert.Equal(ExitCodes.Ok, result.ExitCode);
        Assert.Contains("  config dir: /home/me/.claude-work (CLAUDE_CONFIG_DIR)", result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("auth:", result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("probe:", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DoctorClaudeSaysTheDefaultHomeWhenTheVariableIsNotSetAsync()
    {
        CliResult result = await RunAsync(null, "backends", "doctor", "claude");

        Assert.Equal(ExitCodes.Ok, result.ExitCode);
        Assert.Contains("  config dir: default (~/.claude; CLAUDE_CONFIG_DIR not set)", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEmptyVariableCountsAsNotSetAsync()
    {
        CliResult result = await RunAsync("", "backends", "doctor", "claude");

        Assert.Contains("config dir: default (~/.claude; CLAUDE_CONFIG_DIR not set)", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BareDoctorPrintsTheLineOnceUnderClaudeAndForNoOtherBackendAsync()
    {
        CliResult result = await RunAsync("/home/me/.claude-work", "backends", "doctor");

        Assert.Equal(ExitCodes.Ok, result.ExitCode);
        Assert.Equal(1, result.Stdout.Split("config dir:").Length - 1);
        int claude = result.Stdout.IndexOf("claude:", StringComparison.Ordinal);
        int api = result.Stdout.IndexOf("\napi:", StringComparison.Ordinal);
        int line = result.Stdout.IndexOf("config dir:", StringComparison.Ordinal);
        Assert.True(claude >= 0 && claude < line && line < api, "the line sits inside the claude block, before api's");
    }

    [Theory]
    [InlineData("opencode")]
    [InlineData("cursor")]
    [InlineData("copilot")]
    [InlineData("api")]
    public async Task OtherBackendsPrintNoConfigDirLineAsync(string backend)
    {
        CliResult result = await RunAsync("/home/me/.claude-work", "backends", "doctor", backend);

        Assert.DoesNotContain("config dir:", result.Stdout, StringComparison.Ordinal);
    }
}
