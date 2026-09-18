using Claustrum.Roles.Sync;

namespace Claustrum.Roles.Tests.Sync;

// docs/PLAN.md "Verification" item 2: renderer output for builder/code-reviewer/tester/ui-reviewer x
// high/xhigh/max x claude must equal tests/golden/claude/*.md byte-for-byte. The golden files are
// full generated files (frontmatter + claustrum:generated marker + body), so the comparison runs
// ClaudeSync end to end against a temp cwd rather than RoleRenderer alone. Only builder.md,
// builder-xhigh.md, code-reviewer.md, tester.md, tester-xhigh.md and ui-reviewer.md exist as
// recorded golden fixtures; builder-max.md/code-reviewer-xhigh.md/code-reviewer-max.md/tester-max.md/
// ui-reviewer-xhigh.md/ui-reviewer-max.md are exercised for "renders without error" only
// (TierStubWithoutARecordedGoldenStillRendersANonemptyFile below) since there is no recorded golden
// text to diff against — see the tester report for why those were not fabricated here.
public sealed class ClaudeSyncGoldenTests : IDisposable
{
    private readonly string cwd = Directory.CreateTempSubdirectory("claustrum-sync-golden-").FullName;
    private readonly string fakeHome = Directory.CreateTempSubdirectory("claustrum-sync-golden-home-").FullName;

    public void Dispose()
    {
        Directory.Delete(cwd, recursive: true);
        Directory.Delete(fakeHome, recursive: true);
    }

    [Theory]
    [InlineData("builder", "builder.md")]
    [InlineData("builder", "builder-xhigh.md")]
    [InlineData("code-reviewer", "code-reviewer.md")]
    [InlineData("tester", "tester.md")]
    [InlineData("tester", "tester-xhigh.md")]
    [InlineData("ui-reviewer", "ui-reviewer.md")]
    public void GeneratedFileMatchesGoldenByteForByte(string role, string generatedFileName)
    {
        RoleLibrary library = new();
        ClaudeSync sync = new(library, new RoleRenderer(library), fakeHome);

        sync.Sync(cwd, roles: [role]);

        string actual = File.ReadAllText(Path.Combine(cwd, ".claude", "agents", generatedFileName));
        string expected = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "golden", "claude", generatedFileName));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("builder", "max")]
    [InlineData("code-reviewer", "xhigh")]
    [InlineData("code-reviewer", "max")]
    [InlineData("tester", "max")]
    [InlineData("ui-reviewer", "xhigh")]
    [InlineData("ui-reviewer", "max")]
    public void TierStubWithoutARecordedGoldenStillRendersANonemptyFile(string role, string tier)
    {
        RoleLibrary library = new();
        ClaudeSync sync = new(library, new RoleRenderer(library), fakeHome);

        sync.Sync(cwd, roles: [role]);

        string content = File.ReadAllText(Path.Combine(cwd, ".claude", "agents", $"{role}-{tier}.md"));
        Assert.Contains($"effort: {tier}", content);
        Assert.Contains("claustrum:generated", content);
    }
}
