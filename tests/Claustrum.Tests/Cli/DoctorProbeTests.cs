using Claustrum.Cli;
using Claustrum.Core;
using Claustrum.Core.Backends;
using Claustrum.Core.Config;
using Claustrum.Core.Model;
using Claustrum.Core.Process;
using Claustrum.Tests.Testing;

namespace Claustrum.Tests.Cli;

// DoctorProbe (internal static, InternalsVisibleTo("Claustrum.Tests") on the Claustrum assembly —
// AppServices.cs) is what backends doctor --probe calls (issue #12). Every piece here is unit-testable
// against a fake IPlatform/IBackend without a real backend install, per NOTES.md "doctor --probe
// really calls a backend now" — RunAsync is the one exception that spawns a real (short, harmless)
// process to exercise the actual Runner/ProcessRunner pipeline end to end.
public sealed class DoctorProbeTests
{
    [Theory]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData(" 1 ", true)]
    [InlineData("0", false)]
    [InlineData("yes", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsSkippedRecognizesOnlyOneAndTrueCaseInsensitiveAndTrimmed(string? value, bool expected)
    {
        HomeRedirectPlatform platform = new(Path.GetTempPath());
        // Explicit, not omitted: this must hold regardless of whatever the tester's own shell happens
        // to export, not merely when nobody has set it (HomeRedirectPlatform otherwise falls through
        // to the real process environment for a key it does not know about).
        platform.Environment["CLAUSTRUM_SKIP_PROBE"] = value;

        Assert.Equal(expected, DoctorProbe.IsSkipped(platform));
    }

    [Theory]
    [InlineData("claude", "fast")]
    [InlineData("opencode", null)]
    [InlineData("cursor", null)]
    [InlineData("copilot", null)]
    [InlineData("api", null)]
    public void SelectModelAliasOnBuiltInDefaultsOnlyResolvesClaude(string backend, string? expectedAlias)
    {
        string cwd = Directory.CreateTempSubdirectory("claustrum-doctorprobe-cwd-").FullName;
        try
        {
            Config config = Config.Load(new HomeRedirectPlatform(Path.GetTempPath()), cwd);

            Assert.Equal(expectedAlias, DoctorProbe.SelectModelAlias(config, backend));
        }
        finally
        {
            Directory.Delete(cwd, recursive: true);
        }
    }

    // Config.Load only merges the repo layer when `cwd` sits inside a git root (GitRootLocator.Find),
    // so this needs a real `.git` marker — a directory is enough, GitRootLocator only checks presence.
    [Fact]
    public void RepoClaustrumJsonAliasIsPickedForTheBackendItResolvesTo()
    {
        string repo = CreateGitRepoWithClaustrumJson( /*lang=json,strict*/ """{"models":{"fast":"cursor:auto"}}""");
        Config config = Config.Load(new HomeRedirectPlatform(Path.GetTempPath()), repo);

        Assert.Equal("fast", DoctorProbe.SelectModelAlias(config, "cursor"));

        Directory.Delete(repo, recursive: true);
    }

    // aliasesCheapestFirst tries "fast" before "cheap-coding" before "standard-coding" before
    // "frontier-coding" before "frontier-reasoning" — overriding the two middle ones to the same
    // backend and leaving "fast" resolving elsewhere proves the earlier one wins, not merely "a"
    // match.
    [Fact]
    public void CheapestMatchingAliasWinsWhenSeveralResolveToTheSameBackend()
    {
        string repo = CreateGitRepoWithClaustrumJson( /*lang=json,strict*/
            """{"models":{"cheap-coding":"widget:small","frontier-coding":"widget:big"}}""");
        Config config = Config.Load(new HomeRedirectPlatform(Path.GetTempPath()), repo);

        Assert.Equal("cheap-coding", DoctorProbe.SelectModelAlias(config, "widget"));

        Directory.Delete(repo, recursive: true);
    }

    // A broken alias must not abort the whole diagnostic: SelectModelAlias swallows the
    // ConfigException per-alias and keeps walking the cheapest-first list.
    [Fact]
    public void ACyclicAliasIsSkippedNotFatal()
    {
        string repo = CreateGitRepoWithClaustrumJson( /*lang=json,strict*/ """{"models":{"fast":"fast"}}""");
        Config config = Config.Load(new HomeRedirectPlatform(Path.GetTempPath()), repo);

        Assert.Equal("cheap-coding", DoctorProbe.SelectModelAlias(config, "claude"));

        Directory.Delete(repo, recursive: true);
    }

    // The `models.ContainsKey(alias)` guard (DoctorProbe.cs): without it, ResolveModel's bare-word
    // fallback (SplitBackendModel) would report "claude" for an alias that is not configured at all.
    // Config.Load can never actually produce this (the built-in layer always populates all five
    // aliases and MergeLayer only adds keys, never removes them), so this hand-builds a Config that
    // skips Load entirely to exercise the guard directly.
    [Fact]
    public void AnAliasAbsentFromModelsNeverYieldsClaude()
    {
        Config config = new() { Merged = new ConfigDocument(Models: [], Roles: null, Backends: null, Defaults: null, Jobs: null), Origins = [] };

        Assert.Null(DoctorProbe.SelectModelAlias(config, "claude"));
    }

    [Fact]
    public void DescribeSuccessReportsDurationCostAndClampedReply()
    {
        string line = DoctorProbe.Describe(Result(RunStatus.Success, durationSeconds: 1.3, costUsd: 0.0012m, finalMessage: "OK"));

        Assert.Equal("OK (1.3s, cost $0.0012, reply \"OK\")", line);
    }

    [Fact]
    public void DescribeSuccessWithNoCostSaysSo()
    {
        string line = DoctorProbe.Describe(Result(RunStatus.Success, costUsd: null, finalMessage: "OK"));

        Assert.Contains("cost not reported", line, StringComparison.Ordinal);
    }

    [Fact]
    public void DescribeSuccessClampsTheReplyToFortyCharacters()
    {
        string reply = new('x', 60);
        string line = DoctorProbe.Describe(Result(RunStatus.Success, finalMessage: reply));

        string expectedReply = $"{reply[..39]}…";
        Assert.Contains($"reply \"{expectedReply}\"", line, StringComparison.Ordinal);
    }

    [Fact]
    public void DescribeBackendMissingIsSkippedWithTheFirstErrorLine()
    {
        string line = DoctorProbe.Describe(Result(RunStatus.BackendMissing, error: "'claude' was not found on PATH"));

        Assert.Equal("skipped ('claude' was not found on PATH)", line);
    }

    [Fact]
    public void DescribeTimeoutNamesTheProbeWindowAndTheLog()
    {
        string line = DoctorProbe.Describe(Result(RunStatus.Timeout, logPath: "/x/stdout.log"));

        Assert.Equal("failed (no reply within 120s) — log: /x/stdout.log", line);
    }

    [Theory]
    [InlineData("401 Unauthorized: please login")]
    [InlineData("missing API key")]
    [InlineData("no credential found")]
    public void DescribeDegradesToNoCredentialOnAnAuthLookingMessage(string error)
    {
        string line = DoctorProbe.Describe(Result(RunStatus.Failed, error: error, logPath: "/x/stdout.log"));

        Assert.StartsWith("no credential (", line, StringComparison.Ordinal);
        Assert.EndsWith("— log: /x/stdout.log", line, StringComparison.Ordinal);
    }

    [Fact]
    public void DescribeFailedWithNoAuthMarkerReportsFailedNotCredential()
    {
        string line = DoctorProbe.Describe(Result(RunStatus.Failed, error: "boom: connection refused", logPath: "/x/stdout.log"));

        Assert.Equal("failed (boom: connection refused) — log: /x/stdout.log", line);
    }

    // Pins the shape of tests/fixtures/claude/error-max-budget.json (issue #12): its message names a
    // dollar figure, not any of authMarkers' words, so the "no credential" arm must not fire on it.
    [Fact]
    public void DescribeReportsAMaxBudgetErrorAsFailedNotCredential()
    {
        string line = DoctorProbe.Describe(Result(RunStatus.Failed, error: "Reached maximum budget ($0.05)", logPath: "/x/stdout.log"));

        Assert.Equal("failed (Reached maximum budget ($0.05)) — log: /x/stdout.log", line);
    }

    [Fact]
    public void DescribeClampsAnOverlongErrorLineToOneHundredTwentyCharacters()
    {
        string longError = new('e', 200);
        string line = DoctorProbe.Describe(Result(RunStatus.Failed, error: longError, logPath: "/x/stdout.log"));

        string expectedClamp = $"{longError[..119]}…";
        Assert.Equal($"failed ({expectedClamp}) — log: /x/stdout.log", line);
    }

    [Fact]
    public async Task RunAsyncEndToEndChargesTheProbeCapAndLeavesNoTempCwdBehindAsync()
    {
        string fakeHome = Directory.CreateTempSubdirectory("claustrum-doctorprobe-home-").FullName;
        string repo = CreateGitRepoWithClaustrumJson( /*lang=json,strict*/ """{"models":{"fast":"fake-probe:test-model"}}""");
        try
        {
            HomeRedirectPlatform platform = new(fakeHome);
            FakeBackend backend = FakeBackend.RepliesOk("fake-probe", costUsd: 0.0012m);
            Runner runner = new(platform, new BackendRegistry([backend]), new ProcessRunner(platform));
            Config config = Config.Load(platform, repo);

            string[] probeDirsBefore = Directory.GetDirectories(Path.GetTempPath(), "claustrum-probe-*");

            ProbeOutcome outcome = await DoctorProbe.RunAsync(backend, config, runner, TestContext.Current.CancellationToken);

            Assert.NotNull(outcome.Result);
            Assert.Equal(RunStatus.Success, outcome.Result!.Status);
            Assert.StartsWith("OK (", outcome.Line, StringComparison.Ordinal);

            Assert.NotNull(backend.LastRun);
            Assert.Equal(DoctorProbe.ProbeBudgetUsd, backend.LastRun!.BudgetUsd);
            Assert.DoesNotContain("claustrum-report", backend.LastRun.Brief, StringComparison.Ordinal);

            string[] probeDirsAfter = Directory.GetDirectories(Path.GetTempPath(), "claustrum-probe-*");
            Assert.Equal(probeDirsBefore.OrderBy(p => p, StringComparer.Ordinal), probeDirsAfter.OrderBy(p => p, StringComparer.Ordinal));
        }
        finally
        {
            Directory.Delete(fakeHome, recursive: true);
            Directory.Delete(repo, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsyncWithNoResolvingAliasSkipsWithoutCreatingAJobDirectoryAsync()
    {
        string fakeHome = Directory.CreateTempSubdirectory("claustrum-doctorprobe-home-").FullName;
        string cwd = Directory.CreateTempSubdirectory("claustrum-doctorprobe-cwd-").FullName;
        try
        {
            HomeRedirectPlatform platform = new(fakeHome);
            FakeBackend backend = FakeBackend.RepliesOk("fake-probe", costUsd: 0.01m);
            Runner runner = new(platform, new BackendRegistry([backend]), new ProcessRunner(platform));
            Config config = Config.Load(platform, cwd); // no claustrum.json anywhere in play: no alias resolves to "fake-probe"

            ProbeOutcome outcome = await DoctorProbe.RunAsync(backend, config, runner, TestContext.Current.CancellationToken);

            Assert.Null(outcome.Result);
            Assert.Contains("skipped (no model alias in claustrum.json resolves to this backend", outcome.Line, StringComparison.Ordinal);
            Assert.Contains("fake-probe", outcome.Line, StringComparison.Ordinal);
            Assert.Null(backend.LastRun);
            Assert.False(Directory.Exists(Path.Combine(fakeHome, ".claustrum", "jobs")));
        }
        finally
        {
            Directory.Delete(fakeHome, recursive: true);
            Directory.Delete(cwd, recursive: true);
        }
    }

    // A temp-dir deletion failure must not mask an already-computed outcome (DoctorProbe.RunAsync's
    // `finally` swallows IOException/UnauthorizedAccessException around the delete). Reproducing an
    // actual failure needs write access denied to something *inside* the probe's own temp cwd, whose
    // path DoctorProbe generates internally with no seam to inject a pre-existing lock into — so this
    // has the backend's own spawned process (which runs with that cwd) chmod a subdirectory of it to
    // 000, denying Directory.Delete's own recursive read of it afterwards. That is POSIX-specific
    // (chmod 000 does not block a Windows deletion the same way, and nothing here can hold a Windows
    // file lock open past the spawned process's own exit — the only point an external handle could be
    // injected without the path in hand), so this runs on POSIX only.
    //
    // 2026-09-21 (issue #12): the 000 directory used to also break Runner's own *after*-snapshot
    // (WorktreeSnapshot's file-scan branch enumerated the whole cwd, including "locked", with
    // SearchOption.AllDirectories) and turned a finished run into RunStatus.Failed before the delete
    // was ever reached. Now that ScanFiles walks by hand and skips what it cannot read, the run
    // legitimately succeeds; this test's actual claim — a temp cwd that DoctorProbe.RunAsync's own
    // cleanup cannot fully delete is left on disk rather than masked or thrown out of RunAsync — is
    // still exercised via the leftover "locked" subdirectory alone.
    [Fact]
    public async Task RunAsyncSurvivesATempCwdItCannotFullyDeleteAsync()
    {
        if (OperatingSystem.IsWindows())
            return;

        string fakeHome = Directory.CreateTempSubdirectory("claustrum-doctorprobe-home-").FullName;
        string repo = CreateGitRepoWithClaustrumJson( /*lang=json,strict*/ """{"models":{"fast":"fake-probe:test-model"}}""");
        string? leftover = null;
        try
        {
            HomeRedirectPlatform platform = new(fakeHome);
            FakeBackend backend = FakeBackend.RepliesOk("fake-probe", costUsd: 0.01m, extraShellCommand: "mkdir -p locked && chmod 000 locked");
            Runner runner = new(platform, new BackendRegistry([backend]), new ProcessRunner(platform));
            Config config = Config.Load(platform, repo);

            string[] probeDirsBefore = Directory.GetDirectories(Path.GetTempPath(), "claustrum-probe-*");

            ProbeOutcome outcome = await DoctorProbe.RunAsync(backend, config, runner, TestContext.Current.CancellationToken);

            Assert.NotNull(outcome.Result);
            Assert.Equal(RunStatus.Success, outcome.Result!.Status);
            Assert.NotEmpty(outcome.Line);

            string[] probeDirsAfter = Directory.GetDirectories(Path.GetTempPath(), "claustrum-probe-*");
            leftover = Assert.Single(probeDirsAfter.Except(probeDirsBefore));
            Assert.True(Directory.Exists(leftover), "the temp cwd should still be there: its own delete should have failed too");
        }
        finally
        {
            if (leftover is not null)
            {
                string locked = Path.Combine(leftover, "locked");
                // The owner can always chmod their own directory back regardless of its current mode
                // — unlike DoctorProbe's own best-effort delete, this cleanup is allowed to be sure.
                if (Directory.Exists(locked))
                    File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                Directory.Delete(leftover, recursive: true);
            }

            Directory.Delete(fakeHome, recursive: true);
            Directory.Delete(repo, recursive: true);
        }
    }

    private static string CreateGitRepoWithClaustrumJson(string claustrumJson)
    {
        string dir = Directory.CreateTempSubdirectory("claustrum-doctorprobe-repo-").FullName;
        Directory.CreateDirectory(Path.Combine(dir, ".git"));
        File.WriteAllText(Path.Combine(dir, "claustrum.json"), claustrumJson);
        return dir;
    }

    private static RunResult Result(
        RunStatus status, double durationSeconds = 1.3, decimal? costUsd = null, string finalMessage = "", string? error = null, string logPath = "/tmp/probe.log") => new(
        SchemaVersion: "1",
        JobId: "doctor-probe-job",
        Status: status,
        Backend: "fake-probe",
        Model: "test-model",
        Role: "doctor-probe",
        FinalMessage: finalMessage,
        ChangedFiles: [],
        Diff: null,
        DiffTruncated: false,
        SessionId: null,
        CostUsd: costUsd,
        Usage: null,
        ExitCode: status == RunStatus.Success ? 0 : 1,
        LogPath: logPath,
        DurationSeconds: durationSeconds,
        Error: error,
        Raw: null,
        Report: null,
        ReportStatus: ReportStatus.Missing,
        Warnings: []);
}
