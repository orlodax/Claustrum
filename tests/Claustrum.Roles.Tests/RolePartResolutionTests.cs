namespace Claustrum.Roles.Tests;

// #135: RoleLibrary.ReadPart resolves `{part}.{harness}.md` -> `{part}.md` -> `{part}.default.md`.
// The library ships ui-reviewer/demo-author with a neutral `browser.md` and no harness-specific file, and
// the other roles with a legacy `.claude.md`/`.default.md` pair, so the two precedence questions between a
// neutral file and its neighbours are driven through local overrides where the library has no example.
public sealed class RolePartResolutionTests : IDisposable
{
    private readonly string cwd = Directory.CreateTempSubdirectory("claustrum-role-part-resolution-").FullName;
    private readonly RoleLibrary library = new();

    public void Dispose() => Directory.Delete(cwd, recursive: true);

    private void WritePart(string role, string fileName, string text)
    {
        string directory = Path.Combine(cwd, ".claustrum", "roles", role, "parts");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, fileName), text);
    }

    // A neutral part with no harness-specific sibling serves every harness: ui-reviewer ships only
    // browser.md, so claude and opencode resolve to the same file.
    [Fact]
    public void ANeutralPartWithNoHarnessSpecificSiblingServesEveryHarness()
    {
        string claude = library.ReadPart("ui-reviewer", "browser", "claude", null);
        string opencode = library.ReadPart("ui-reviewer", "browser", "opencode", null);

        Assert.Equal(claude, opencode);
        Assert.Contains("| Verb | Browser pane (Claude) | Claude in Chrome | Other harness |", claude, StringComparison.Ordinal);
    }

    // The legacy `.default.md` is still the last fallback: tester ships `environment.claude.md` and
    // `environment.default.md` and no neutral `environment.md`, so opencode lands on the default.
    [Fact]
    public void TheLegacyDefaultPartIsStillTheLastFallback()
    {
        string opencode = library.ReadPart("tester", "environment", "opencode", null);

        Assert.Contains("the one appropriate to the OS you are running on", opencode, StringComparison.Ordinal);
        Assert.NotEqual(library.ReadPart("tester", "environment", "claude", null), opencode);
    }

    // With a harness-specific file beside a neutral one the harness-specific wins, and the neutral one still
    // serves a harness it does not name.
    [Fact]
    public void AHarnessSpecificLocalPartWinsOverANeutralOne()
    {
        WritePart("builder", "delegation.md", "NEUTRAL");
        WritePart("builder", "delegation.claude.md", "HARNESS-SPECIFIC");

        Assert.Equal("HARNESS-SPECIFIC", library.ReadPart("builder", "delegation", "claude", cwd));
        Assert.Equal("NEUTRAL", library.ReadPart("builder", "delegation", "opencode", cwd));
    }

    // With a neutral file beside a legacy default the neutral one wins.
    [Fact]
    public void ANeutralLocalPartWinsOverALegacyDefault()
    {
        WritePart("builder", "delegation.md", "NEUTRAL");
        WritePart("builder", "delegation.default.md", "DEFAULT");

        Assert.Equal("NEUTRAL", library.ReadPart("builder", "delegation", "opencode", cwd));
    }

    // And the default is reached only when neither the harness-specific nor the neutral file exists.
    [Fact]
    public void TheDefaultLocalPartIsReachedOnlyAsTheLastFallback()
    {
        WritePart("builder", "delegation.default.md", "DEFAULT");

        Assert.Equal("DEFAULT", library.ReadPart("builder", "delegation", "opencode", cwd));
    }
}
