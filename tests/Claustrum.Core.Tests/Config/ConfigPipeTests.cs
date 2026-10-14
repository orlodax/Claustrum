using Claustrum.Core.Config;
using Claustrum.Core.Platform;
using Claustrum.Core.Tests.Testing;
using CoreConfig = Claustrum.Core.Config.Config;

namespace Claustrum.Core.Tests;

// 2026-10-13 (found by review): a repo's claustrum.json swapped for a FIFO blocked Config.Load forever. RealPlatform reads it
// through PlainFile; a fake platform holds no real files and serves its in-memory text.
public sealed class ConfigPipeTests : IDisposable
{
    private readonly ScratchRoot scratch = new("claustrum-config-pipe-");

    public void Dispose() => scratch.Dispose();

    [Fact]
    public void AFifoClaustrumJsonIsAConfigErrorNotAHang()
    {
        string repo = scratch.CreateRepo();
        PipeFiles.MakeFifo(Path.Combine(repo, "claustrum.json"));

        ConfigException ex = PipeFiles.Within(() => Assert.Throws<ConfigException>(() => CoreConfig.Load(new RealPlatform(), repo)));

        Assert.Contains("not a regular file", ex.Message, StringComparison.Ordinal);
        Assert.Contains("claustrum.json", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ARegularClaustrumJsonThroughTheRealPlatformStillLoads()
    {
        string repo = scratch.CreateRepo();
        File.WriteAllText(Path.Combine(repo, "claustrum.json"), /*lang=json,strict*/ """{"models":{"mine":"claude:opus"}}""");

        CoreConfig config = CoreConfig.Load(new RealPlatform(), repo);

        Assert.Equal("claude:opus", config.Merged.Models?["mine"]);
    }

    [Fact]
    public void AFakePlatformsInMemoryClaustrumJsonStillLoads()
    {
        FakePlatform platform = new() { HomeDirectory = Path.Combine(scratch.CreateDirectory(), "home") };
        string repo = scratch.CreateDirectory();
        platform.ExtraExistingPaths.Add(Path.Combine(repo, ".git"));
        platform.Files[Path.Combine(repo, "claustrum.json")] = /*lang=json,strict*/ """{"models":{"mine":"claude:sonnet"}}""";

        CoreConfig config = CoreConfig.Load(platform, repo);

        Assert.Equal("claude:sonnet", config.Merged.Models?["mine"]);
    }

    // #56: `backends.<name>.injection` was inert and is gone from the schema; a claustrum.json that still names it must load.
    [Fact]
    public void AClaustrumJsonStillNamingBackendsInjectionLoadsAndKeepsTheRest()
    {
        FakePlatform platform = new() { HomeDirectory = Path.Combine(scratch.CreateDirectory(), "home") };
        string repo = scratch.CreateDirectory();
        platform.ExtraExistingPaths.Add(Path.Combine(repo, ".git"));
        platform.Files[Path.Combine(repo, "claustrum.json")] =
            /*lang=json,strict*/ """{"backends":{"claude":{"path":"/opt/claude","injection":"claude-md"}},"models":{"mine":"claude:sonnet"}}""";

        CoreConfig config = CoreConfig.Load(platform, repo);

        Assert.Equal("claude:sonnet", config.Merged.Models?["mine"]);
        Assert.Equal("/opt/claude", config.Merged.Backends?["claude"].Path);
    }
}
