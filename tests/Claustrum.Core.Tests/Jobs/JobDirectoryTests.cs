using Claustrum.Core.Jobs;
using Claustrum.Core.Tests.Testing;

namespace Claustrum.Core.Tests.Jobs;

// Review finding: the id names the job directory, the `claustrum/<id>` branch and the
// `.claustrum/worktrees/<id>` path, and M3 fans builders out as processes that all start inside the
// same wall-clock second. The old 16-bit suffix collided at ~1-in-65536 per same-second pair, and
// Directory.CreateDirectory being idempotent turned a collision into two jobs sharing one directory.
public sealed class JobDirectoryTests : IDisposable
{
    private readonly string home = Directory.CreateTempSubdirectory("claustrum-jobdir-").FullName;
    private readonly FakePlatform platform = new();

    public JobDirectoryTests() => platform.EnvironmentVariables["CLAUSTRUM_HOME"] = home;

    public void Dispose() => Directory.Delete(home, recursive: true);

    [Fact]
    public void ManyIdsCreatedBackToBackAreAllDistinct()
    {
        HashSet<string> ids = [];
        for (int i = 0; i < 200; i++)
            Assert.True(ids.Add(JobDirectory.Create(platform).Id), "JobDirectory.Create returned a duplicate id");
    }

    [Fact]
    public void ConcurrentCreatesNeverShareADirectory()
    {
        string[] ids = [.. Enumerable.Range(0, 32)
            .AsParallel()
            .WithDegreeOfParallelism(8)
            .Select(_ => JobDirectory.Create(platform).Id)];

        Assert.Equal(ids.Length, ids.Distinct().Count());
    }

    [Fact]
    public void EachJobGetsItsOwnDirectoryUnderTheResolvedRoot()
    {
        JobPaths first = JobDirectory.Create(platform);
        JobPaths second = JobDirectory.Create(platform);

        Assert.NotEqual(first.Directory, second.Directory);
        Assert.True(Directory.Exists(first.Directory));
        Assert.True(Directory.Exists(second.Directory));
    }
}
