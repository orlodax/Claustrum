using System.Globalization;
using System.Text;
using Claustrum.Core.Backends;
using Claustrum.Core.Backends.Acp;
using Claustrum.Core.Config;
using Claustrum.Core.Platform;
using Claustrum.Core.Tests.Testing;

namespace Claustrum.Core.Tests.Backends.Acp;

// OpencodeAcpHarness.SessionTotalAsync is `opencode session export <id>` through the shared OpencodeSessionExport (moved out of OpencodeBackend unchanged:
// OpencodePostRunTests holds the export's own edge cases). What is pinned here is what the ACP row asks of it: the configured binary of
// `backends.acp-opencode.path`, the arguments, the total as the only figure taken, and a failure said in the export's words. The stand-in `opencode`
// is a POSIX shell script, as in OpencodePostRunTests.
public sealed class OpencodeAcpHarnessTests : IDisposable
{
    private readonly string scratch = Directory.CreateTempSubdirectory("claustrum-acp-harness-").FullName;

    public void Dispose() => TempTree.Delete(scratch);

    private string Calls => Path.Combine(scratch, "calls.txt");

    private OpencodeAcpHarness HarnessAnswering(params (string Stdout, string Stderr, int ExitCode)[] replies)
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("the stand-in `opencode` is a POSIX shell script.");

        for (int i = 0; i < replies.Length; i++)
        {
            File.WriteAllText(Path.Combine(scratch, $"r{i + 1}.out"), replies[i].Stdout);
            File.WriteAllText(Path.Combine(scratch, $"r{i + 1}.err"), replies[i].Stderr);
            File.WriteAllText(Path.Combine(scratch, $"r{i + 1}.exit"), replies[i].ExitCode.ToString(CultureInfo.InvariantCulture));
        }

        string script = new StringBuilder("#!/bin/sh\n")
            .Append($"n=$(cat '{scratch}/count' 2>/dev/null || echo 0)\nn=$((n+1))\nprintf '%s' \"$n\" > '{scratch}/count'\n")
            .Append($"printf '%s\\n' \"$*\" >> '{Calls}'\n")
            .Append($"k=$n\n[ -f '{scratch}/r'$k.out ] || k={replies.Length}\n")
            .Append($"cat '{scratch}/r'$k.out\ncat '{scratch}/r'$k.err >&2\nexit $(cat '{scratch}/r'$k.exit)\n")
            .ToString();
        string path = Path.Combine(scratch, "opencode");
        File.WriteAllText(path, script);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        return new OpencodeAcpHarness(new RealPlatform());
    }

    private PostRunContext Context() => new(new ProcessSpec("opencode", ["acp"], scratch, [], []), null, scratch, new BackendConfig(Path.Combine(scratch, "opencode")), EnvPassthroughAll: false, "");

    private string[] Invocations => File.Exists(Calls) ? File.ReadAllLines(Calls) : [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Export = /*lang=json,strict*/ """{"info":{"cost":0.0018094076,"tokens":{"input":17966,"output":11,"reasoning":103,"cache":{"read":8448,"write":0}}},"messages":[]}""";

    [Fact]
    public async Task TheTotalIsTheExportsInfoCostFromTheConfiguredBinaryAndNothingElseIsTakenAsync()
    {
        OpencodeAcpHarness harness = HarnessAnswering((Export, "", 0));

        AcpSessionTotal? total = await harness.SessionTotalAsync("ses_abc", Context(), Ct);

        Assert.Equal(new AcpSessionTotal(0.0018094076m, null), total);
        Assert.Equal(["session export --standalone ses_abc"], Invocations);
    }

    [Fact]
    public async Task AFailedExportIsAFailureInTheProcessesWordsWithNoCostAsync()
    {
        OpencodeAcpHarness harness = HarnessAnswering(("", "no such session", 3));

        AcpSessionTotal? total = await harness.SessionTotalAsync("ses_abc", Context(), Ct);

        Assert.Equal(new AcpSessionTotal(null, "no such session"), total);
    }

    [Fact]
    public async Task AFailedExportWithNothingOnStderrNamesItsExitCodeAsync()
    {
        OpencodeAcpHarness harness = HarnessAnswering(("", "", 7));

        AcpSessionTotal? total = await harness.SessionTotalAsync("ses_abc", Context(), Ct);

        Assert.Equal(new AcpSessionTotal(null, "exit code 7"), total);
    }

    [Fact]
    public async Task AnExportWithoutACostIsAFailureNotAZeroAsync()
    {
        OpencodeAcpHarness harness = HarnessAnswering((/*lang=json,strict*/ """{"info":{"tokens":{}}}""", "", 0));

        AcpSessionTotal? total = await harness.SessionTotalAsync("ses_abc", Context(), Ct);

        Assert.Equal(new AcpSessionTotal(null, "export info has no cost"), total);
    }

    [Fact]
    public async Task ACutOffExportIsReadOnceMoreAndASecondWholeOneSucceedsAsync()
    {
        OpencodeAcpHarness harness = HarnessAnswering(("""{"info":{"cost":0.""", "", 0), (Export, "", 0));

        AcpSessionTotal? total = await harness.SessionTotalAsync("ses_abc", Context(), Ct);

        Assert.Equal(new AcpSessionTotal(0.0018094076m, null), total);
        Assert.Equal(2, Invocations.Length);
    }

    [Fact]
    public async Task AnExportThatStaysCutOffFailsNamingTheParseErrorAfterOneRetryAsync()
    {
        OpencodeAcpHarness harness = HarnessAnswering(("""{"info":{"cost":0.""", "", 0));

        AcpSessionTotal? total = await harness.SessionTotalAsync("ses_abc", Context(), Ct);

        Assert.Null(total?.CostUsd);
        Assert.StartsWith("unparseable export JSON: ", total?.Failure, StringComparison.Ordinal);
        Assert.Equal(2, Invocations.Length);
    }

    [Fact]
    public async Task AnOpencodeThatCannotBeFoundIsAFailureReasonNotAThrowAsync()
    {
        OpencodeAcpHarness harness = new(new FakePlatform { Os = ClaustrumOs.Linux });
        PostRunContext context = new(new ProcessSpec("opencode", ["acp"], scratch, [], []), null, scratch, BackendConfig: null, EnvPassthroughAll: false, "");

        AcpSessionTotal? total = await harness.SessionTotalAsync("ses_abc", context, Ct);

        Assert.Null(total?.CostUsd);
        Assert.Contains("BackendNotFoundException", total?.Failure, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheExportsTemporaryFileIsGoneAfterwardAsync()
    {
        OpencodeAcpHarness harness = HarnessAnswering((Export, "", 0));

        _ = await harness.SessionTotalAsync("ses_abc", Context(), Ct);

        Assert.Empty(Directory.GetFiles(scratch, "session-export-*"));
    }

    // The total is read with the run's own environment and directory, as the native backend does.
    [Fact]
    public async Task TheExportRunsInTheRunsDirectoryWithTheRunsEnvironmentAsync()
    {
        OpencodeAcpHarness harness = HarnessAnswering((Export, "", 0));
        string path = Path.Combine(scratch, "opencode");
        File.WriteAllText(path, File.ReadAllText(path).Replace("exit $(cat", $"pwd >> '{scratch}/cwd.txt'\nprintenv MY_RUN_VAR >> '{scratch}/env.txt'\nexit $(cat", StringComparison.Ordinal));
        PostRunContext context = Context() with { Spec = new ProcessSpec("opencode", ["acp"], scratch, new Dictionary<string, string> { ["MY_RUN_VAR"] = "from-the-run" }, []) };

        _ = await harness.SessionTotalAsync("ses_abc", context, Ct);

        Assert.Equal("from-the-run", (await File.ReadAllTextAsync(Path.Combine(scratch, "env.txt"), Ct)).Trim());
        Assert.Equal(Path.GetFullPath(scratch), Path.GetFullPath((await File.ReadAllTextAsync(Path.Combine(scratch, "cwd.txt"), Ct)).Trim()));
    }
}
