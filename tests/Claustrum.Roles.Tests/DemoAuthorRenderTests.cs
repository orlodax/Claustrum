namespace Claustrum.Roles.Tests;

// The demo-author's rules were rewritten over several review rounds of PR #42 (where the deck lives,
// who writes the ignore line, when nothing may be written). Each phrase below is one of those
// decisions, so a re-wrap that keeps it passes and a revert that drops it fails. The `DoesNotContain`
// half pins text an earlier round had and a later one removed on purpose.
public sealed class DemoAuthorRenderTests : IDisposable
{
    private readonly string cwd = Directory.CreateTempSubdirectory("claustrum-demo-author-render-").FullName;
    private readonly RoleLibrary library = new();

    public void Dispose() => Directory.Delete(cwd, recursive: true);

    private string RenderedBody() => Prose.Flatten(new RoleRenderer(library).Render("demo-author", "high", "claude", cwd).SystemBody);

    [Theory]
    [InlineData("git log --all --format=%H -1")]
    [InlineData("-<short commit>")]
    [InlineData("no deck file")]
    [InlineData("lists any ignore line that was")]
    public void TheRenderedRoleCarriesTheTrackedPathAndBlockedRules(string phrase) =>
        Assert.Contains(phrase, RenderedBody(), StringComparison.Ordinal);

    // "In every case" is what makes the info/exclude line unconditional (a .gitignore line on a feature
    // branch does not ignore the deck in the main checkout on another branch): the two must sit together.
    [Fact]
    public void InEveryCaseIsTheRuleThatPutsTheLineIntoInfoExclude()
    {
        string body = RenderedBody();

        int start = body.IndexOf("In every case", StringComparison.Ordinal);
        Assert.True(start >= 0, "'In every case' is missing from the rendered demo-author");
        Assert.Contains("info/exclude", body[start..Math.Min(body.Length, start + 300)], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("rm -r --cached")]
    [InlineData("x-*")]
    [InlineData("~/Videos")]
    [InlineData("nothing was written")]
    [InlineData("**write nothing**")]
    public void TextAnEarlierRoundRemovedIsNotBack(string phrase) =>
        Assert.DoesNotContain(phrase, RenderedBody(), StringComparison.Ordinal);

    // `--path-format=absolute` needs git 2.31, so the capture script resolves the common dir against the
    // checkout root instead; the flag is named once, as the thing not to use, and only in the Claude part.
    [Fact]
    public void PathFormatAbsoluteAppearsOnlyAsTheNotWarningInTheClaudePart()
    {
        const string flag = "--path-format=absolute";
        Assert.DoesNotContain(flag, library.LoadRole("demo-author", cwd).RoleMdTemplate, StringComparison.Ordinal);

        string part = Prose.Flatten(library.ReadPart("demo-author", "browser", "claude", cwd));
        Assert.Contains($"not `{flag}`", part, StringComparison.Ordinal);
        Assert.Equal(1, CountOf(part, flag));

        string body = RenderedBody();
        Assert.Equal(CountOf(body, $"not `{flag}`"), CountOf(body, flag));
    }

    [Theory]
    [InlineData("Write every deck file — and the `info/exclude` line — from the capture script")]
    [InlineData("protected folder")]
    [InlineData("creates its `info/` directory if it is missing")]
    public void TheClaudePartTellsTheCaptureScriptToWriteEverythingItself(string phrase) =>
        Assert.Contains(phrase, Prose.Flatten(library.ReadPart("demo-author", "browser", "claude", cwd)), StringComparison.Ordinal);

    [Theory]
    [InlineData("names only what you actually wrote")]
    [InlineData("no deck file was written")]
    public void TheReportContractSaysWhatGitignoreMayClaim(string phrase)
    {
        string contract = Prose.Flatten(library.ReadShared("_shared/report/demo-author.md"));

        Assert.Contains(phrase, contract, StringComparison.Ordinal);
        Assert.Contains(phrase, RenderedBody(), StringComparison.Ordinal);
    }

    // The report schema carries the new `gitignore` key, because the architect commits a line only
    // when the demo-author reports it there.
    [Fact]
    public void TheReportSchemaCarriesTheGitignoreKey() =>
        Assert.Contains("\"gitignore\":", library.ReadShared("_shared/report/demo-author.md"), StringComparison.Ordinal);

    [Fact]
    public void TheDemoAuthorRendersForEveryHarnessItListsAndOnlyForThose()
    {
        RoleRenderer renderer = new(library);

        foreach (string harness in library.LoadRole("demo-author", cwd).Definition.Harnesses)
            Assert.False(string.IsNullOrWhiteSpace(renderer.Render("demo-author", "high", harness, cwd).SystemBody));

        // The browser part is claude-only, so any other harness fails loudly and names it.
        RoleRenderException exception = Assert.Throws<RoleRenderException>(() => renderer.Render("demo-author", "high", "opencode", cwd));
        Assert.Contains("browser", exception.Message, StringComparison.Ordinal);
    }

    private static int CountOf(string text, string phrase)
    {
        int count = 0;
        for (int index = text.IndexOf(phrase, StringComparison.Ordinal); index >= 0; index = text.IndexOf(phrase, index + phrase.Length, StringComparison.Ordinal))
            count++;

        return count;
    }
}
