using System.Text.Json;
using Claustrum.Core.Config;
using Claustrum.Core.Model;
using Claustrum.Delegation;
using Claustrum.Tests.Testing;

namespace Claustrum.Tests.Delegation;

// #145 on the refusals of #104: a run refused because `.claustrum` leaves the repo is still a receipt, and it lists the issues dispatch found (a read dir
// that is missing) as every other receipt does, whichever of the engine's gates refused it: the isolated path's, the one-slot gate's, a branch lock's.
// In process, against a real repository and the fake claude, as DelegateEngineFenceTests; the user config is the fixture home's, put back after each test.
[Collection(AppServicesHomeCollectionDefinition.Name)]
public sealed class DelegateEngineFenceIssuesTests(AppServicesHomeFixture fixture) : IDisposable
{
    private readonly IsolatedRepo repo = new();
    private readonly string outside = Directory.CreateTempSubdirectory("claustrum-fence-issues-outside-").FullName;

    public void Dispose()
    {
        UserConfig.Write(fixture.HomeDirectory);
        repo.Dispose();
        fixture.Platform.Environment["CLAUSTRUM_PARENT_JOB"] = null;
        TempTree.Delete(outside);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Missing => Path.Combine(outside, "no-such-read-dir");

    private DelegateRequest Request(int? maxParallel, string? branch = null) => new(
        Role: "builder",
        Brief: "do the task",
        Cwd: repo.Repo,
        Tier: "high",
        Overrides: new ConfigOverrides(Backend: "claude"),
        ResumeSession: null,
        AttachFiles: [],
        Env: [],
        Stream: false,
        DiffCapBytes: 64 * 1024,
        MaxParallel: maxParallel,
        CastName: "default",
        Branch: branch);

    private void Arrange()
    {
        UserConfig.Write(
            fixture.HomeDirectory,
            "{\"models\":{\"frontier-reasoning\":\"claude:opus\",\"frontier-coding\":\"claude:opus\",\"standard-coding\":\"claude:sonnet\",\"cheap-coding\":\"claude:haiku\",\"fast\":\"claude:haiku\"},"
            + "\"read_dirs\":[" + JsonSerializer.Serialize(Missing) + "]}");
        TestLinks.CreateDirectoryLink(Path.Combine(repo.Repo, ".claustrum"), Directory.CreateDirectory(Path.Combine(outside, "claustrum")).FullName);
    }

    private void AssertCarriesTheDispatchIssue(RunResult receipt)
    {
        Assert.Equal(RunStatus.Failed, receipt.Status);
        EnvironmentIssue issue = Assert.Single(receipt.EnvironmentIssues!);
        Assert.Equal(EnvironmentIssueKind.MissingDirectory, issue.Kind);
        Assert.Equal(Missing, issue.Where);
        Assert.Equal("dispatch", issue.First);

        string resultJson = Path.Combine(Core.Jobs.JobDirectory.ResolveRoot(fixture.Platform), receipt.JobId, "result.json");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(resultJson));
        Assert.Equal("dispatch", document.RootElement.GetProperty("environment_issues")[0].GetProperty("first").GetString());
    }

    [Fact]
    public async Task TheIsolatedPathsRefusalCarriesTheDispatchIssueAsync()
    {
        Arrange();

        MachineryRefusedException ex = await Assert.ThrowsAsync<MachineryRefusedException>(() => DelegateEngine.RunAsync(Request(maxParallel: 2), Ct));

        AssertCarriesTheDispatchIssue(ex.Receipt);
    }

    [Fact]
    public async Task TheOneSlotGatesRefusalCarriesTheDispatchIssueAsync()
    {
        Arrange();

        MachineryRefusedException ex = await Assert.ThrowsAsync<MachineryRefusedException>(() => DelegateEngine.RunAsync(Request(maxParallel: 1), Ct));

        AssertCarriesTheDispatchIssue(ex.Receipt);
    }

    [Fact]
    public async Task ABranchRunsRefusalCarriesTheDispatchIssueAsync()
    {
        Arrange();

        MachineryRefusedException ex = await Assert.ThrowsAsync<MachineryRefusedException>(() => DelegateEngine.RunAsync(Request(maxParallel: 2, branch: "main"), Ct));

        AssertCarriesTheDispatchIssue(ex.Receipt);
    }
}
