using Claustrum.Cli;
using Claustrum.Core;
using Claustrum.Core.Backends;
using Claustrum.Core.Backends.Acp;
using Claustrum.Core.Config;
using Claustrum.Core.Model;
using Claustrum.Core.Process;
using Claustrum.Tests.Testing;

namespace Claustrum.Tests.Cli;

// #130: `backends doctor --probe` makes one real round trip per backend that has a model alias, through Runner like any run, so the ACP backend's duplex driver has to be
// what that round trip uses (a probe that launched `opencode acp` with nothing on its stdin would report a dead agent). The agent is the fake; nothing is paid.
public sealed class DoctorProbeAcpTests : IDisposable
{
    private readonly string home = Directory.CreateTempSubdirectory("claustrum-probe-acp-home-").FullName;
    private readonly string repo = Directory.CreateTempSubdirectory("claustrum-probe-acp-repo-").FullName;

    public DoctorProbeAcpTests() => FakeAcpAgent.RequirePosixShell();

    public void Dispose()
    {
        TempTree.Delete(home);
        TempTree.Delete(repo);
    }

    private (HomeRedirectPlatform Platform, Config Config, AcpBackend Backend, FakeAcpAgent Fake) Setup(params FakeAcpTurn[] turns)
    {
        FakeAcpAgent fake = new(Directory.CreateDirectory(Path.Combine(home, "fake-acp")).FullName, turns);
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        File.WriteAllText(Path.Combine(repo, "claustrum.json"), "{\"models\":{\"fast\":\"acp-opencode:opencode/fake\"},\"backends\":{" + fake.BackendJson + "}}");
        HomeRedirectPlatform platform = new(home);
        return (platform, Config.Load(platform, repo), new AcpBackend(platform, new OpencodeAcpHarness(platform)), fake);
    }

    [Fact]
    public async Task TheProbeOfTheAcpBackendIsOneOkRoundTripThroughTheDriverAndChargesWhatTheSessionCostAsync()
    {
        (HomeRedirectPlatform platform, Config config, AcpBackend backend, FakeAcpAgent fake) = Setup(new FakeAcpTurn("OK", CostUsd: 0.0012m));
        Runner runner = new(platform, new BackendRegistry([backend]), new ProcessRunner(platform));

        ProbeOutcome outcome = await DoctorProbe.RunAsync(backend, config, runner, TestContext.Current.CancellationToken);

        Assert.NotNull(outcome.Result);
        Assert.Equal(RunStatus.Success, outcome.Result.Status);
        Assert.Equal("OK (", outcome.Line[..4]);
        Assert.Contains("cost $0.0012", outcome.Line, StringComparison.Ordinal);
        Assert.Contains("reply \"OK\"", outcome.Line, StringComparison.Ordinal);
        Assert.Equal(1, fake.Prompts);
        Assert.DoesNotContain("claustrum-report", fake.PromptText(1), StringComparison.Ordinal);
        Assert.Equal(["model=opencode/fake", "mode=claustrum-doctor-probe"], fake.ConfigOptions);
    }

    [Fact]
    public async Task WithNoAliasOnTheAcpBackendTheProbeIsSkippedNamingTheBackendAsync()
    {
        (HomeRedirectPlatform platform, _, AcpBackend backend, FakeAcpAgent fake) = Setup(new FakeAcpTurn("OK"));
        File.WriteAllText(Path.Combine(repo, "claustrum.json"), /*lang=json,strict*/ """{"models":{"fast":"claude:haiku"}}""");
        Runner runner = new(platform, new BackendRegistry([backend]), new ProcessRunner(platform));

        ProbeOutcome outcome = await DoctorProbe.RunAsync(backend, Config.Load(platform, repo), runner, TestContext.Current.CancellationToken);

        Assert.Null(outcome.Result);
        Assert.Contains("add e.g. \"fast\": \"acp-opencode:<model>\"", outcome.Line, StringComparison.Ordinal);
        Assert.Equal(0, fake.Runs);
    }
}
