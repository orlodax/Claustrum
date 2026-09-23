using Claustrum.Roles.Sync;

namespace Claustrum.Roles.Tests.Sync;

// docs/PLAN.md "Verification" item 2: ClaudeSync's output per role x claude must equal
// tests/golden/claude/*.md byte-for-byte. The goldens are full generated files (frontmatter +
// claustrum:generated marker + body), so the comparison runs ClaudeSync end to end against a temp
// cwd rather than RoleRenderer alone. Only the files named in GeneratedFileMatchesGoldenByteForByte
// are recorded goldens; the remaining tier stubs get "renders without error" only
// (TierStubWithoutARecordedGoldenStillRendersANonemptyFile) because there is no recorded golden
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
    [InlineData("architect", "architect.md")]
    [InlineData("architect", "architect-xhigh.md")]
    [InlineData("builder", "builder.md")]
    [InlineData("builder", "builder-xhigh.md")]
    [InlineData("code-reviewer", "code-reviewer.md")]
    [InlineData("tester", "tester.md")]
    [InlineData("tester", "tester-xhigh.md")]
    [InlineData("ui-reviewer", "ui-reviewer.md")]
    [InlineData("demo-author", "demo-author.md")]
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
    [InlineData("architect", "max")]
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
