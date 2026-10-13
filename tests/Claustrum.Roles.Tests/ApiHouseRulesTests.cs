using Claustrum.Core.Model;
using Claustrum.Roles.Tests.Sync;

namespace Claustrum.Roles.Tests;

// #55: for the api harness the cwd's AGENTS.md and CLAUDE.md ride in the system prompt (the backend cannot read
// files); a file whose real path leaves the cwd is never read; each file is capped at 32 KiB.
public sealed class ApiHouseRulesTests : IDisposable
{
    private const int CapBytes = 32 * 1024;
    private const string Secret = "TOP-SECRET-TOKEN";

    private static readonly StringComparison pathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private readonly RoleLibrary library = new();
    private readonly string cwd = Directory.CreateTempSubdirectory("claustrum-api-rules-").FullName;
    private readonly string outside = Directory.CreateTempSubdirectory("claustrum-api-rules-outside-").FullName;

    public void Dispose()
    {
        LinkFixtures.DeleteTree(cwd);
        LinkFixtures.DeleteTree(outside);
    }

    private static string Normalise(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

    private RenderedRole Render(string harness, string? directory = null) =>
        new RoleRenderer(library).Render("code-reviewer", "high", harness, directory ?? cwd);

    private static string Body(RenderedRole rendered) => Normalise(rendered.SystemBody);

    [Fact]
    public void ApiInlinesBothFilesUnderTheirHeadingsAndDropsTheReadInstruction()
    {
        File.WriteAllText(Path.Combine(cwd, "AGENTS.md"), "Agents rule one.\n");
        File.WriteAllText(Path.Combine(cwd, "CLAUDE.md"), "Claude rule two.\n");

        RenderedRole rendered = Render("api");
        string body = Body(rendered);

        Assert.Contains("### AGENTS.md (inlined; the api backend has no file access)\n\nAgents rule one.", body, StringComparison.Ordinal);
        Assert.Contains("### CLAUDE.md (inlined; the api backend has no file access)\n\nClaude rule two.", body, StringComparison.Ordinal);
        Assert.Contains("The repository's rules follow; they are law", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Read `AGENTS.md`", body, StringComparison.Ordinal);
        Assert.True(body.IndexOf("### AGENTS.md", StringComparison.Ordinal) < body.IndexOf("### CLAUDE.md", StringComparison.Ordinal));
        Assert.Null(rendered.Warnings);
    }

    [Fact]
    public void TheFilesSitInsideTheHouseRulesSectionAtTheEndOfTheSystemPrompt()
    {
        File.WriteAllText(Path.Combine(cwd, "AGENTS.md"), "Agents rule one.\n");

        string body = Body(Render("api"));

        Assert.True(body.IndexOf("## House rules", StringComparison.Ordinal) < body.IndexOf("### AGENTS.md", StringComparison.Ordinal));
        Assert.EndsWith("Agents rule one.\n\n### CLAUDE.md (not present in the working directory)", body, StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingFileIsOneLineAndNoWarning()
    {
        File.WriteAllText(Path.Combine(cwd, "CLAUDE.md"), "Claude rule two.\n");

        RenderedRole rendered = Render("api");
        string body = Body(rendered);

        Assert.Contains("### AGENTS.md (not present in the working directory)\n\n### CLAUDE.md (inlined", body, StringComparison.Ordinal);
        Assert.Null(rendered.Warnings);
    }

    [Fact]
    public void BothFilesMissingIsTwoLinesAndNoWarning()
    {
        RenderedRole rendered = Render("api");

        Assert.EndsWith(
            "### AGENTS.md (not present in the working directory)\n\n### CLAUDE.md (not present in the working directory)",
            Body(rendered),
            StringComparison.Ordinal);
        Assert.Null(rendered.Warnings);
    }

    [Fact]
    public void AnEmptyFileSaysSo()
    {
        File.WriteAllText(Path.Combine(cwd, "AGENTS.md"), "");

        Assert.Contains("(inlined; the api backend has no file access)\n\n(the file is empty)", Body(Render("api")), StringComparison.Ordinal);
    }

    [Fact]
    public void AFileOverTheCapIsCutAtACharacterBoundaryMarkedAndWarnedAbout()
    {
        // One ASCII byte, then two-byte characters: byte 32768 is the second half of a character, so the cut must back off one.
        string content = "x" + new string('é', CapBytes);
        File.WriteAllText(Path.Combine(cwd, "AGENTS.md"), content);
        long length = new FileInfo(Path.Combine(cwd, "AGENTS.md")).Length;

        RenderedRole rendered = Render("api");
        string body = Body(rendered);

        Assert.DoesNotContain('�', body);
        Assert.Contains($"[truncated: the first {CapBytes - 1} of {length} bytes of AGENTS.md are shown]", body, StringComparison.Ordinal);
        Assert.Contains("x" + new string('é', (CapBytes - 2) / 2), body, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('é', CapBytes / 2), body, StringComparison.Ordinal);
        Assert.Equal($"AGENTS.md inlined truncated to {CapBytes - 1} of {length} bytes", Assert.Single(rendered.Warnings ?? []));
    }

    [Fact]
    public void AFileOfExactlyTheCapIsInlinedWhole()
    {
        File.WriteAllText(Path.Combine(cwd, "AGENTS.md"), new string('a', CapBytes));

        RenderedRole rendered = Render("api");

        Assert.Contains(new string('a', CapBytes), Body(rendered), StringComparison.Ordinal);
        Assert.DoesNotContain("[truncated", Body(rendered), StringComparison.Ordinal);
        Assert.Null(rendered.Warnings);
    }

    [Fact]
    public void ALinkLeavingTheCwdIsNotInlinedAndTheReceiptNamesTheLinkAndItsRealPath()
    {
        string secret = Path.Combine(outside, "id_rsa");
        File.WriteAllText(secret, Secret);
        string link = Path.Combine(cwd, "AGENTS.md");
        LinkFixtures.FileLink(link, secret);
        File.WriteAllText(Path.Combine(cwd, "CLAUDE.md"), "Claude rule two.\n");

        RenderedRole rendered = Render("api");
        string body = Body(rendered);

        Assert.DoesNotContain(Secret, body, StringComparison.Ordinal);
        Assert.Contains("### AGENTS.md (not inlined: it resolves outside the working directory)", body, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, body, pathComparison);
        Assert.Contains("Claude rule two.", body, StringComparison.Ordinal);
        string warning = Assert.Single(rendered.Warnings ?? []);
        Assert.StartsWith("AGENTS.md not inlined: ", warning, StringComparison.Ordinal);
        Assert.Contains(link, warning, pathComparison);
        Assert.Contains(SyncRootResolve(secret), warning, pathComparison);
        Assert.DoesNotContain(Secret, warning, StringComparison.Ordinal);
    }

    [Fact]
    public void ALinkStayingInsideTheCwdIsInlined()
    {
        File.WriteAllText(Path.Combine(cwd, "RULES.md"), "Shared rules.\n");
        LinkFixtures.FileLink(Path.Combine(cwd, "CLAUDE.md"), Path.Combine(cwd, "RULES.md"));

        RenderedRole rendered = Render("api");

        Assert.Contains("### CLAUDE.md (inlined; the api backend has no file access)\n\nShared rules.", Body(rendered), StringComparison.Ordinal);
        Assert.Null(rendered.Warnings);
    }

    [Theory]
    [InlineData("claude")]
    [InlineData("opencode")]
    [InlineData("cursor")]
    [InlineData("copilot")]
    public void OtherHarnessesRenderTheSameWhateverTheCwdHolds(string harness)
    {
        string empty = Directory.CreateTempSubdirectory("claustrum-api-rules-empty-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(cwd, "AGENTS.md"), "Agents rule one.\n");
            File.WriteAllText(Path.Combine(cwd, "CLAUDE.md"), "Claude rule two.\n");

            RenderedRole withFiles = Render(harness);
            RenderedRole without = Render(harness, empty);

            Assert.Equal(without.SystemBody, withFiles.SystemBody);
            Assert.Contains("Read `AGENTS.md` and `CLAUDE.md` in the working directory first", Body(withFiles), StringComparison.Ordinal);
            Assert.Null(withFiles.Warnings);
        }
        finally
        {
            Directory.Delete(empty);
        }
    }

    [Fact]
    public void ApiWithNoCwdRendersTheLibraryAloneAndKeepsTheReadInstruction()
    {
        RenderedRole rendered = new RoleRenderer(library).Render("code-reviewer", "high", "api", cwd: null);

        Assert.Contains("Read `AGENTS.md` and `CLAUDE.md` in the working directory first", Body(rendered), StringComparison.Ordinal);
        Assert.DoesNotContain("### AGENTS.md", Body(rendered), StringComparison.Ordinal);
    }

    [Fact]
    public void TheBlindRoleKeepsItsFlagAndTheInlinedFilesStayOutOfTheBrief()
    {
        File.WriteAllText(Path.Combine(cwd, "AGENTS.md"), "## Context\nrules that happen to use the heading\n");

        RenderedRole rendered = Render("api");

        Assert.True(rendered.Blind);
        Assert.Contains("## Context\nrules that happen", Body(rendered), StringComparison.Ordinal);
    }

    [Fact]
    public void AnInlinedFileDoesNotMoveTheReportFormatSection()
    {
        File.WriteAllText(Path.Combine(cwd, "AGENTS.md"), "## Report format\nnot the real one\n");

        string body = Body(Render("api"));

        int houseRules = body.IndexOf("## House rules", StringComparison.Ordinal);
        Assert.InRange(body.IndexOf("## Report format", StringComparison.Ordinal), 0, houseRules);
        Assert.Equal(1, Occurrences(body[..houseRules], "\n## Report format\n"));
    }

    private static string SyncRootResolve(string path) => Roles.Sync.SyncRoot.Resolve(path);

    private static int Occurrences(string text, string needle)
    {
        int count = 0;
        for (int at = text.IndexOf(needle, StringComparison.Ordinal); at >= 0; at = text.IndexOf(needle, at + needle.Length, StringComparison.Ordinal))
            count++;

        return count;
    }
}
