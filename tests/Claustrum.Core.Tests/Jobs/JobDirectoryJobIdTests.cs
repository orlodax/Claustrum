using Claustrum.Core.Jobs;
using Claustrum.Core.Tests.Testing;

namespace Claustrum.Core.Tests.Jobs;

// #124 stage 2 review F6: a job id a user types becomes a directory under the jobs store, so its shape is checked before any
// Path.Combine; on Windows a drive-qualified `C:x` replaces the store root. The shape is exactly what JobDirectory.Create mints.
public sealed class JobDirectoryJobIdTests : IDisposable
{
    private readonly string home = Directory.CreateTempSubdirectory("claustrum-jobid-").FullName;
    private readonly FakePlatform platform = new();

    public JobDirectoryJobIdTests() => platform.EnvironmentVariables["CLAUSTRUM_HOME"] = home;

    public void Dispose() => Directory.Delete(home, recursive: true);

    [Fact]
    public void EveryIdTheStoreMintsIsAJobId()
    {
        for (int i = 0; i < 50; i++)
            Assert.True(JobDirectory.IsJobId(JobDirectory.Create(platform).Id));
    }

    [Theory]
    [InlineData("20261015-190000-aaaaaaaa")]
    [InlineData("20261015-190000-0123abcd")]
    public void TheMintedShapeIsAccepted(string id) => Assert.True(JobDirectory.IsJobId(id));

    [Theory]
    [InlineData("")]
    [InlineData("../x")]
    [InlineData("..")]
    [InlineData("C:x")]
    [InlineData("C:\\x")]
    [InlineData("/tmp/x")]
    [InlineData("20261015-190000-aaaaaaaa/..")]
    [InlineData("20261015-190000-aaaaaaaa\n")]
    [InlineData(" 20261015-190000-aaaaaaaa")]
    [InlineData("20261015-190000-AAAAAAAA")]
    [InlineData("20261015-190000-aaaaaaa")]
    [InlineData("20261015-190000-aaaaaaaaa")]
    [InlineData("20261015-190000-gggggggg")]
    [InlineData("20261015190000-aaaaaaaa")]
    [InlineData("2026101-190000-aaaaaaaa")]
    [InlineData("٢٠٢٦١٠١٥-١٩٠٠٠٠-aaaaaaaa")]
    [InlineData("20261015-190000-aaaaaaaa-extra")]
    [InlineData("x")]
    public void AnythingElseIsRefused(string id) => Assert.False(JobDirectory.IsJobId(id));

    [Fact]
    public void OpenRefusesABadIdAndAnUnknownJobAndFindsAMintedOne()
    {
        JobPaths minted = JobDirectory.Create(platform);

        Assert.Null(JobDirectory.Open(platform, "../x"));
        Assert.Null(JobDirectory.Open(platform, "20261015-000000-deadbeef"));
        Assert.Equal(minted.Directory, JobDirectory.Open(platform, minted.Id)?.Directory);
    }
}
