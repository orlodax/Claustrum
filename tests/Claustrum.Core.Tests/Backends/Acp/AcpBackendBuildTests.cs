using System.Text.Json;
using Claustrum.Core.Backends;
using Claustrum.Core.Backends.Acp;
using Claustrum.Core.Backends.Opencode;
using Claustrum.Core.Config;
using Claustrum.Core.Live;
using Claustrum.Core.Model;
using Claustrum.Core.Platform;
using Claustrum.Core.Process;
using Claustrum.Core.Tests.Testing;

namespace Claustrum.Core.Tests.Backends.Acp;

// #130: `acp-opencode` launches `opencode acp` and speaks to it over its stdin, so Build only decides what to launch: the binary, `acp`,
// the role's agent in OPENCODE_CONFIG_CONTENT (the native backend's own, deny list included) and no stdin. The ACP config options the
// driver sets afterwards come from the harness (OpencodeAcpHarness.Prepare), pinned here in the order measured on opencode 2.0.30.
public sealed class AcpBackendBuildTests
{
    private static readonly FakePlatform platform = new();

    private readonly AcpBackend backend = AcpLines.Backend(platform);

    [Fact]
    public void ItIsCalledAcpOpencodeButIsJudgedAndRenderedAsOpencodeAndCanResume()
    {
        Assert.Equal("acp-opencode", backend.Name);
        Assert.Equal("opencode", backend.Harness);
        Assert.True(backend.CanResume);
    }

    [Fact]
    public void BuildLaunchesOpencodeAcpInTheRunsDirectoryWithNothingOnStdin()
    {
        ProcessSpec spec = backend.Build(AcpLines.Run());

        Assert.Equal("opencode", spec.Exe);
        Assert.Equal(["acp"], spec.Args);
        Assert.Equal("/repo", spec.Cwd);
        Assert.Null(spec.StdinText);
        Assert.Empty(spec.TempFiles);
        Assert.Null(spec.McpServers);
        Assert.Null(spec.Warnings);
    }

    // The brief, the model and the session never ride argv: the driver sends them as JSON-RPC requests once `acp` is up.
    [Fact]
    public void NeitherTheBriefNorTheModelNorAResumedSessionIsOnTheCommandLine()
    {
        ProcessSpec spec = backend.Build(AcpLines.Run(model: "openrouter/deepseek/deepseek-v4-flash#max", resume: "ses_old", brief: "SECRET-BRIEF"));

        Assert.Equal(["acp"], spec.Args);
        Assert.Null(spec.StdinText);
        Assert.DoesNotContain(spec.Env, entry => entry.Value.Contains("SECRET-BRIEF", StringComparison.Ordinal));
    }

    public static TheoryData<PermissionLevel, string[]> Permissions() => new()
    {
        { PermissionLevel.ReadOnly, [] },
        { PermissionLevel.Shell, ["git push"] },
        { PermissionLevel.Edit, [] },
        { PermissionLevel.EditShell, ["git push", "rm -rf*"] },
        { PermissionLevel.Full, [] },
    };

    // "The native backend's BuildConfigContent": byte for byte what `opencode run` is given, so the deny list and the three headless guards
    // of the native backend are the ACP run's too, and a change to either cannot drift apart.
    [Theory]
    [MemberData(nameof(Permissions))]
    public void TheConfigContentIsExactlyTheNativeBackendsForTheSameRun(PermissionLevel level, string[] deny)
    {
        ResolvedRun run = AcpLines.Run(permission: new PermissionPolicy(level, deny));

        string acp = backend.Build(run).Env["OPENCODE_CONFIG_CONTENT"];
        string native = new OpencodeBackend(platform).Build(run).Env["OPENCODE_CONFIG_CONTENT"];

        Assert.Equal(native, acp);
    }

    [Fact]
    public void TheConfigNamesTheRolesAgentAndPointsItsPromptAtTheSystemPromptFile()
    {
        ProcessSpec spec = backend.Build(AcpLines.Run(role: "code-reviewer"));

        using JsonDocument config = JsonDocument.Parse(spec.Env["OPENCODE_CONFIG_CONTENT"]);
        JsonElement agent = config.RootElement.GetProperty("agent").GetProperty("claustrum-code-reviewer");
        Assert.Equal("primary", agent.GetProperty("mode").GetString());
        Assert.Equal("{file:/job/system.md}", agent.GetProperty("prompt").GetString());
    }

    [Fact]
    public void TheFilewatcherIsOffAndTheOldPermissionVariableIsNotSet()
    {
        ProcessSpec spec = backend.Build(AcpLines.Run());

        Assert.Equal("1", spec.Env["OPENCODE_DISABLE_FILEWATCHER"]);
        Assert.False(spec.Env.ContainsKey("OPENCODE_PERMISSION"));
    }

    [Fact]
    public void TheRunsOwnEnvironmentIsCarriedAndTheHarnessesValuesWinOverIt()
    {
        Dictionary<string, string> own = new() { ["MY_VAR"] = "mine", ["OPENCODE_DISABLE_FILEWATCHER"] = "0" };

        ProcessSpec spec = backend.Build(AcpLines.Run(env: own));

        Assert.Equal("mine", spec.Env["MY_VAR"]);
        Assert.Equal("1", spec.Env["OPENCODE_DISABLE_FILEWATCHER"]);
        Assert.Equal("0", own["OPENCODE_DISABLE_FILEWATCHER"]);
        Assert.False(own.ContainsKey("OPENCODE_CONFIG_CONTENT"));
    }

    [Fact]
    public void BuildIsRepeatableBecauseBuildAndCreateDriverBothCallIt()
    {
        ResolvedRun run = AcpLines.Run(permission: new PermissionPolicy(PermissionLevel.Shell, ["git push"]));

        Assert.Equal(backend.Build(run).Env, backend.Build(run).Env);
    }

    // ---- the config options the driver sets after the session opens ---------------------------------------------

    private static (string ConfigId, string Value)[] Options(ResolvedRun run) => new OpencodeAcpHarness(platform).Prepare(run).ConfigOptions;

    [Fact]
    public void AModelWithoutAVariantIsSetThenTheRolesAgentIsChosenAsTheMode()
    {
        Assert.Equal([("model", "opencode/fake"), ("mode", "claustrum-builder")], Options(AcpLines.Run()));
    }

    [Fact]
    public void TheModeIsTheAgentOfTheRoleThatRuns()
    {
        Assert.Equal([("model", "opencode/fake"), ("mode", "claustrum-tester")], Options(AcpLines.Run(role: "tester")));
    }

    // opencode's ACP `model` option refuses `id#variant` with -32602, which would read as model_not_found and start a cast's fallback;
    // the variant is the `effort` option, whose values follow the model, so it comes second (NOTES.md "A `#<variant>` model id").
    [Fact]
    public void AVariantInTheModelIdBecomesTheEffortOptionSetBetweenTheModelAndTheMode()
    {
        Assert.Equal(
            [("model", "openrouter/deepseek/deepseek-v4-flash"), ("effort", "max"), ("mode", "claustrum-builder")],
            Options(AcpLines.Run(model: "openrouter/deepseek/deepseek-v4-flash#max")));
    }

    [Fact]
    public void OnlyTheFirstHashSplitsTheModelFromItsVariant()
    {
        Assert.Equal([("model", "a/b"), ("effort", "c#d"), ("mode", "claustrum-builder")], Options(AcpLines.Run(model: "a/b#c#d")));
    }

    [Fact]
    public void PrepareCarriesTheSameEnvironmentBuildDoes()
    {
        ResolvedRun run = AcpLines.Run(permission: new PermissionPolicy(PermissionLevel.Shell, ["git push"]));

        AcpHarnessSetup setup = new OpencodeAcpHarness(platform).Prepare(run);

        Assert.Equal(setup.Env, [.. backend.Build(run).Env.Where(entry => setup.Env.ContainsKey(entry.Key))]);
        Assert.Null(setup.Warnings);
    }

    // ---- what the harness says about itself --------------------------------------------------------------------

    [Fact]
    public void OpencodeIsTheRowsExecutableWithAcpAsItsArgumentAndAVersionProbe()
    {
        OpencodeAcpHarness harness = new(platform);

        Assert.Equal("opencode", harness.Name);
        Assert.Equal("opencode", harness.Exe);
        Assert.Equal(["acp"], harness.Args);
        Assert.Equal(["--version"], harness.VersionArgs);
        Assert.True(harness.CostIsRunningTotal);
        Assert.True(harness.LoadsSessions);
    }

    // ---- the live seam ----------------------------------------------------------------------------------------

    [Fact]
    public void TheLiveProtocolIsThePerTurnOneNotTheStreamOne()
    {
        Assert.Same(AcpLiveProtocol.Instance, backend.LiveProtocol);
        Assert.IsAssignableFrom<IResumeLiveProtocol>(backend.LiveProtocol);
        Assert.IsNotAssignableFrom<IStreamLiveProtocol>(backend.LiveProtocol);
    }

    [Fact]
    public void ItIsADuplexBackendWithAPostRunHookAndAnAcpStreamFormatter()
    {
        Assert.IsAssignableFrom<IDuplexBackend>(backend);
        Assert.IsAssignableFrom<IPostRunHook>(backend);
        Assert.IsType<AcpStreamFormatter>(backend.CreateStreamFormatter("/repo"));
    }

    [Fact]
    public void ACarriedTurnIsAProcessSpecThatNeverRunsWhoseStdinIsTheMessage()
    {
        ProcessSpec carried = backend.CarryTurn("ses_old", "and the second question?");

        Assert.Equal("and the second question?", carried.StdinText);
        Assert.Equal("and the second question?", AcpLiveProtocol.Message(carried));
    }

    [Fact]
    public void CreateDriverHandsBackAOneProcessDriverNotAPerTurnOne()
    {
        string scratch = Directory.CreateTempSubdirectory("claustrum-acp-driver-").FullName;
        try
        {
            IProcessLive driver = backend.CreateDriver(AcpLines.Run(), AcpLines.JobIn(scratch), live: null);

            Assert.False(driver.PerTurn);
            Assert.False(driver.ReportDelivered);
        }
        finally
        {
            TempTree.Delete(scratch);
        }
    }

    // ---- the registry ------------------------------------------------------------------------------------------

    [Fact]
    public void TheDefaultRegistryHasSixBackendsAndAcpOpencodeIsTheLast()
    {
        BackendRegistry registry = BackendRegistry.CreateDefault(platform);

        Assert.Equal(["claude", "api", "opencode", "copilot", "cursor", "acp-opencode"], registry.All.Select(entry => entry.Name));
        Assert.True(registry.TryGet("acp-opencode", out IBackend? found));
        Assert.IsType<AcpBackend>(found);
    }

    [Fact]
    public void EveryOtherBackendIsItsOwnHarnessAndNeitherDuplexNorAcp()
    {
        BackendRegistry registry = BackendRegistry.CreateDefault(platform);

        foreach (IBackend other in registry.All.Where(entry => entry.Name != "acp-opencode"))
        {
            Assert.Equal(other.Name, other.Harness);
            Assert.IsNotType<AcpBackend>(other);
            Assert.False(other is IDuplexBackend, $"{other.Name} must not be driven as a duplex backend");
        }
    }

    // Opt-in: no spelling but the full name reaches the ACP backend.
    [Theory]
    [InlineData("acp")]
    [InlineData("ACP-OPENCODE")]
    [InlineData("acp-opencode ")]
    [InlineData("acp_opencode")]
    public void NothingButTheExactNameResolvesToIt(string name)
    {
        Assert.False(BackendRegistry.CreateDefault(platform).TryGet(name, out _));
    }

    // ---- detection ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task DetectReportsAMissingOpencodeAsNotFoundWithTheUsualProblemAndNoAdvisoryAsync()
    {
        Doctor doctor = await backend.DetectAsync(config: null, TestContext.Current.CancellationToken);

        Assert.False(doctor.Found);
        Assert.Equal(["'opencode' was not found on PATH"], doctor.Problems);
        Assert.Empty(doctor.Advisories);
    }

    [Fact]
    public async Task DetectRunsTheConfiguredBinaryWithDashDashVersionAndReadsItsVersionAsync()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("the stand-in `opencode` is a POSIX shell script.");

        string scratch = Directory.CreateTempSubdirectory("claustrum-acp-detect-").FullName;
        try
        {
            string script = Path.Combine(scratch, "opencode");
            File.WriteAllText(script, $"#!/bin/sh\nprintf '%s\\n' \"$*\" > '{Path.Combine(scratch, "argv.txt")}'\necho '2.0.30'\n");
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            AcpBackend real = new(new RealPlatform(), new OpencodeAcpHarness(new RealPlatform()));

            Doctor doctor = await real.DetectAsync(new BackendConfig(script), TestContext.Current.CancellationToken);

            Assert.True(doctor.Found);
            Assert.Equal("2.0.30", doctor.Version);
            Assert.Equal(script, doctor.Path);
            Assert.Equal("--version", (await File.ReadAllTextAsync(Path.Combine(scratch, "argv.txt"), TestContext.Current.CancellationToken)).Trim());
            Assert.Empty(doctor.Advisories);
        }
        finally
        {
            TempTree.Delete(scratch);
        }
    }
}
