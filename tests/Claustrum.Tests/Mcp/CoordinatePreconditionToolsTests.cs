using Claustrum.Core.Jobs;
using Claustrum.Mcp;
using Claustrum.Tests.Testing;
using ModelContextProtocol;

namespace Claustrum.Tests.Mcp;

// The MCP door of #74's precondition, in-process like ClaustrumToolsTests: the refusal is the tool's own McpException,
// carrying the same message the CLI prints, and no job directory is minted for it. A refused call spawns nothing, and
// the repository's committed claustrum.json points the claude backend at a script in any case (IsolatedRepo).
[Collection(AppServicesHomeCollectionDefinition.Name)]
public sealed class CoordinatePreconditionToolsTests(AppServicesHomeFixture fixture) : IDisposable
{
    private readonly IsolatedRepo repo = IsolatedRepo.ForCoordinate();

    public void Dispose()
    {
        repo.Dispose();
        fixture.Platform.Environment["CLAUSTRUM_PARENT_JOB"] = null;
    }

    private string[] JobDirectories()
    {
        string root = JobDirectory.ResolveRoot(fixture.Platform);
        return Directory.Exists(root) ? Directory.GetDirectories(root) : [];
    }

    [Theory]
    [MemberData(nameof(PreconditionCases.Names), MemberType = typeof(PreconditionCases))]
    public async Task EachRefusalIsAToolErrorNamingWhatToFixAndMintsNoJobAsync(string name)
    {
        PreconditionCase refusal = PreconditionCases.Apply(repo, name);
        string[] jobsBefore = JobDirectories();

        McpException exception = await Assert.ThrowsAsync<McpException>(
            () => ClaustrumTools.CoordinateAsync(brief: "x", cwd: refusal.Cwd, cast: refusal.CastName, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains(refusal.Fragment, exception.Message, StringComparison.Ordinal);
        Assert.Equal(jobsBefore, JobDirectories());
        Assert.False(Directory.Exists(Path.Combine(repo.Repo, ".claustrum", "worktrees")));
    }
}
