using Claustrum.Core.Model;

namespace Claustrum.Roles.Tests;

// Issue #16: tester and ui-reviewer were re-ported from the owner's current ~/.claude/agents files.
// Descriptions are pinned as literals rather than read from that path at test time — CI runs on
// ubuntu-latest/windows-latest (AGENTS.md), neither of which has the owner's home directory, so a
// live file read would make this suite non-portable. Copied verbatim (YAML folded scalar `>-`
// flattens each wrapped line to one space-joined line) from the frontmatter `description:` of
// ~/.claude/agents/tester.md and ~/.claude/agents/ui-reviewer.md as they stood on 2026-09-22.
public sealed class TesterAndUiReviewerPortTests : IDisposable
{
    private const string OwnerTesterDescription =
        "Testing & quality-gate agent. Use to AUTHOR and RUN tests and verify the CI gate for this repo's stack. Diagnoses failures at ROOT CAUSE and reports precisely. Invoked by the architect once a batch of builders is complete and its code review is triaged, or explicitly as \"tester\" to write or run tests.";

    private const string OwnerUiReviewerDescription =
        "Blind UI-review agent. Use to REVIEW a change by USING the running app in a browser — after a builder batch, in parallel with the code-reviewer — checking that what the user asked for actually works on screen and that what must still work still does. Invoke explicitly as \"ui-reviewer\". Leaf role: it reports ranked findings with reproduction steps and browser evidence; it does not fix code, write tests, or delegate. Default tier for a focused change on one or two screens; escalate to `ui-reviewer-xhigh`/`ui-reviewer-max` for cross-cutting UI batches — see \"Choosing your tier\".";

    private readonly string cwd = Directory.CreateTempSubdirectory("claustrum-tester-uireviewer-port-").FullName;
    private readonly RoleLibrary library = new();

    public void Dispose() => Directory.Delete(cwd, recursive: true);

    [Fact]
    public void TesterDescriptionMatchesTheOwnersFrontmatterFlattenedToOneLine() =>
        Assert.Equal(OwnerTesterDescription, library.LoadRole("tester", cwd).Definition.Description);

    [Fact]
    public void UiReviewerDescriptionMatchesTheOwnersFrontmatterFlattenedToOneLine() =>
        Assert.Equal(OwnerUiReviewerDescription, library.LoadRole("ui-reviewer", cwd).Definition.Description);

    // roles/ui-reviewer/parts/browser.claude.md is deliberately claude-only (no browser.default.md),
    // so only a claude render pulls in the verb table it holds.
    [Fact]
    public void UiReviewerRenderedForClaudeContainsTheBrowserVerbTable()
    {
        RoleRenderer renderer = new(library);

        RenderedRole rendered = renderer.Render("ui-reviewer", "high", "claude", cwd);

        Assert.Contains("preview_start", rendered.SystemBody, StringComparison.Ordinal);
        Assert.Contains("| Verb | Browser pane (default) | Claude in Chrome | Other harness |", rendered.SystemBody, StringComparison.Ordinal);
    }

    // No browser.opencode.md and no browser.default.md exist: RoleLibrary.ReadPart must fail loudly
    // naming the part, not silently emit an empty table.
    [Fact]
    public void UiReviewerRenderedForOpencodeThrowsNamingTheBrowserPart()
    {
        RoleRenderer renderer = new(library);

        RoleRenderException ex = Assert.Throws<RoleRenderException>(() => renderer.Render("ui-reviewer", "high", "opencode", cwd));
        Assert.Contains("browser", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("opencode")]
    [InlineData("copilot")]
    [InlineData("cursor")]
    public void TesterRendersForNonClaudeHarnessesWithTheDefaultEnvironmentText(string harness)
    {
        RoleRenderer renderer = new(library);

        RenderedRole rendered = renderer.Render("tester", "high", harness, cwd);

        Assert.Contains("use your host's", rendered.SystemBody, StringComparison.Ordinal);
        Assert.Contains("the one appropriate to the OS you are running on", rendered.SystemBody, StringComparison.Ordinal);
    }

    [Fact]
    public void TesterRendersForClaudeWithTheClaudeSpecificEnvironmentText()
    {
        RoleRenderer renderer = new(library);

        RenderedRole rendered = renderer.Render("tester", "high", "claude", cwd);

        Assert.Contains("use the host's", rendered.SystemBody, StringComparison.Ordinal);
        Assert.Contains("whichever shell tools you actually have,", rendered.SystemBody, StringComparison.Ordinal);
    }
}
