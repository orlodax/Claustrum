using System.Diagnostics;
using Claustrum.Core.Model;
using Claustrum.Roles.Sync;
using Claustrum.Roles.Tests.Sync;

namespace Claustrum.Roles.Tests;

// #55 review (2026-10-13): the rules file is judged by what the OPENED handle is, not by what the path said a moment earlier.
// A link to `.git/config` (a CI token), to a non-Markdown file or into any `.git` is never inlined, a pipe is never read, and a
// link swapped in between the path check and the open never lets the outside bytes into the prompt.
public sealed class ApiHouseRulesFenceTests : IDisposable
{
    private const string Secret = "TOP-SECRET-TOKEN";

    private readonly RoleLibrary library = new();
    private readonly string cwd = Directory.CreateTempSubdirectory("claustrum-api-fence-").FullName;
    private readonly string outside = Directory.CreateTempSubdirectory("claustrum-api-fence-out-").FullName;

    public void Dispose()
    {
        LinkFixtures.DeleteTree(cwd);
        LinkFixtures.DeleteTree(outside);
    }

    private RenderedRole Render() => new RoleRenderer(library).Render("code-reviewer", "high", "api", cwd);

    private string Write(string content, params string[] relative)
    {
        string path = Path.Combine([cwd, .. relative]);
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? cwd);
        File.WriteAllText(path, content);
        return path;
    }

    private (string Body, string Warning) RenderRefused(string heading)
    {
        RenderedRole rendered = Render();
        string body = rendered.SystemBody.Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.Contains(heading, body, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, body, StringComparison.Ordinal);
        return (body, Assert.Single(rendered.Warnings ?? []));
    }

    [Fact]
    public void ALinkToGitConfigIsNotInlinedAndTheTokenIsAbsent()
    {
        Write($"[credential]\ntoken = {Secret}\n", ".git", "config");
        LinkFixtures.FileLink(Path.Combine(cwd, "AGENTS.md"), Path.Combine(cwd, ".git", "config"));

        (_, string warning) = RenderRefused("### AGENTS.md (not inlined: its real path is not a Markdown file inside the working directory)");

        Assert.Contains("not a regular .md file outside .git", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void ALinkToAFileThatIsNotMarkdownIsNotInlined()
    {
        Write(Secret, "notes.txt");
        LinkFixtures.FileLink(Path.Combine(cwd, "CLAUDE.md"), Path.Combine(cwd, "notes.txt"));

        (_, string warning) = RenderRefused("### CLAUDE.md (not inlined: its real path is not a Markdown file inside the working directory)");

        Assert.StartsWith("CLAUDE.md not inlined:", warning, StringComparison.Ordinal);
        Assert.Contains("not a regular .md file outside .git", warning, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(".git")]
    [InlineData(".GIT")]
    public void ALinkToAMarkdownFileInsideANestedGitDirectoryIsNotInlinedInAnyCase(string gitName)
    {
        Write(Secret, "sub", gitName, "x.md");
        LinkFixtures.FileLink(Path.Combine(cwd, "AGENTS.md"), Path.Combine(cwd, "sub", gitName, "x.md"));

        (_, string warning) = RenderRefused("### AGENTS.md (not inlined: its real path is not a Markdown file inside the working directory)");

        Assert.Contains("outside .git", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void ALinkToAMarkdownFileOutsideTheCwdIsRefusedBeforeItIsOpened()
    {
        string secret = Path.Combine(outside, "outside.md");
        File.WriteAllText(secret, Secret);
        LinkFixtures.FileLink(Path.Combine(cwd, "AGENTS.md"), secret);

        (_, string warning) = RenderRefused("### AGENTS.md (not inlined: it resolves outside the working directory)");

        Assert.Contains("resolves outside the working directory", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void ALinkToAnInRepoMarkdownFileOutsideGitIsInlined()
    {
        Write("Shared rules.\n", "docs", "rules.md");
        LinkFixtures.FileLink(Path.Combine(cwd, "AGENTS.md"), Path.Combine(cwd, "docs", "rules.md"));

        RenderedRole rendered = Render();

        Assert.Contains("### AGENTS.md (inlined; the api backend has no file access)\n\nShared rules.", rendered.SystemBody.Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Null(rendered.Warnings);
    }

    [Fact]
    public void ADirectoryNamedAgentsMdIsNeitherAnErrorNorInlined()
    {
        Directory.CreateDirectory(Path.Combine(cwd, "AGENTS.md"));

        RenderedRole rendered = Render();

        Assert.Contains("### AGENTS.md (not present in the working directory)", rendered.SystemBody, StringComparison.Ordinal);
    }

    [Fact]
    public void AFifoRulesFileReturnsPromptlyNamedAsNotRegularWithAWarning()
    {
        PipeFiles.MakeFifo(Path.Combine(cwd, "CLAUDE.md"));

        RenderedRole rendered = PipeFiles.Within(Render);

        Assert.Contains("### CLAUDE.md (not inlined: it is not a regular file)", rendered.SystemBody, StringComparison.Ordinal);
        string warning = Assert.Single(rendered.Warnings ?? []);
        Assert.StartsWith("CLAUDE.md not inlined:", warning, StringComparison.Ordinal);
        Assert.Contains("not a regular file", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void ANormalAgentsMdNextToAFifoClaudeMdIsStillInlined()
    {
        Write("Agents rule one.\n", "AGENTS.md");
        PipeFiles.MakeFifo(Path.Combine(cwd, "CLAUDE.md"));

        RenderedRole rendered = PipeFiles.Within(Render);

        Assert.Contains("### AGENTS.md (inlined; the api backend has no file access)\n\nAgents rule one.", rendered.SystemBody.Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Contains("### CLAUDE.md (not inlined: it is not a regular file)", rendered.SystemBody, StringComparison.Ordinal);
    }

    [Fact]
    public void ABoundSocketRulesFileIsNotInlined()
    {
        using System.Net.Sockets.Socket socket = PipeFiles.BindSocket(Path.Combine(cwd, "AGENTS.md"));

        RenderedRole rendered = PipeFiles.Within(Render);

        Assert.Contains("### AGENTS.md (not inlined: it is not a regular file)", rendered.SystemBody, StringComparison.Ordinal);
    }

    // OpenedFilePath asks the OS about the handle: an opened symlink answers with the target's real path, never the link's own.
    [Fact]
    public void TheOpenedPathOfASymlinkIsTheTargetsRealPath()
    {
        string target = Write("x", "real", "target.md");
        string link = Path.Combine(cwd, "link.md");
        LinkFixtures.FileLink(link, target);

        using FileStream stream = new(link, FileMode.Open, FileAccess.Read);
        string? opened = OpenedFilePath.TryGet(stream.SafeFileHandle);

        Assert.NotNull(opened);
        Assert.EndsWith(Path.Combine("real", "target.md"), opened, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        Assert.DoesNotContain("link.md", opened, StringComparison.Ordinal);
    }

    // A reader that opens the file at the same moment a writer swaps a link in and out must never get the outside bytes:
    // the verdict is about the handle it got. The flip is a rename over the name, so the name is always one or the other.
    [Fact]
    public void AFileFlippedBetweenRegularAndALinkToASecretNeverLeaksTheSecretWhileComposeLoops()
    {
        string secret = Path.Combine(outside, "secret.md");
        File.WriteAllText(secret, Secret);
        string rules = Path.Combine(cwd, "AGENTS.md");
        string regularStaging = Path.Combine(cwd, "regular.tmp");
        string linkStaging = Path.Combine(cwd, "link.tmp");
        File.WriteAllText(rules, "REGULAR-RULES\n");
        LinkFixtures.FileLink(linkStaging, secret);
        File.Delete(linkStaging);
        string houseRules = library.ReadShared("_shared/house-rules.md");

        bool stop = false;
        Thread flipper = new(() =>
        {
            bool link = false;
            while (!Volatile.Read(ref stop))
            {
                try
                {
                    if (link)
                    {
                        File.CreateSymbolicLink(linkStaging, secret);
                        File.Move(linkStaging, rules, overwrite: true);
                    }
                    else
                    {
                        File.WriteAllText(regularStaging, "REGULAR-RULES\n");
                        File.Move(regularStaging, rules, overwrite: true);
                    }

                    link = !link;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // A rename racing a reader's open may be refused on Windows; the next turn tries again.
                }
            }
        })
        {
            IsBackground = true,
        };
        flipper.Start();

        int iterations = 0;
        int inlined = 0;
        Stopwatch clock = Stopwatch.StartNew();
        try
        {
            while (clock.Elapsed < TimeSpan.FromSeconds(3) || iterations < 2000)
            {
                string composed = ApiHouseRules.Compose(houseRules, cwd, []);
                Assert.DoesNotContain(Secret, composed, StringComparison.Ordinal);
                if (composed.Contains("REGULAR-RULES", StringComparison.Ordinal))
                    inlined++;

                iterations++;
                if (clock.Elapsed > TimeSpan.FromSeconds(30))
                    break;
            }
        }
        finally
        {
            Volatile.Write(ref stop, true);
            flipper.Join(TimeSpan.FromSeconds(10));
        }

        Assert.True(iterations >= 1000, $"only {iterations} iterations ran");
        Assert.True(inlined > 0, "the regular file was never seen, so the loop proved nothing");
    }

    [Fact]
    public void AnEightDotThreeCwdStillInlinesAnOrdinaryRulesFile()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Skip("8.3 short names are a Windows filesystem feature.");

        string shortCwd = ShortPath(cwd);
        if (string.Equals(shortCwd, cwd, StringComparison.OrdinalIgnoreCase) || !shortCwd.Contains('~', StringComparison.Ordinal))
            Assert.Skip("8.3 name generation is off on this volume, so the temp directory has no short spelling.");

        Write("Short-name rules.\n", "AGENTS.md");

        RenderedRole rendered = new RoleRenderer(library).Render("code-reviewer", "high", "api", shortCwd);

        Assert.Contains("Short-name rules.", rendered.SystemBody, StringComparison.Ordinal);
        Assert.Null(rendered.Warnings);
    }

    // cmd's `%~sI` expands a path to its 8.3 spelling; a volume with short names off answers with the long one.
    private static string ShortPath(string path)
    {
        ProcessStartInfo start = new("cmd.exe") { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
        start.ArgumentList.Add("/c");
        start.ArgumentList.Add($"for %I in (\"{path}\") do @echo %~sI");
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("cmd.exe did not start");
        string output = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();
        return output.Length > 0 ? output : path;
    }
}
