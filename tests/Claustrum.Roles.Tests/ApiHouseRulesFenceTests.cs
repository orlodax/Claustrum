using System.Diagnostics;
using Claustrum.Core.IO;
using Claustrum.Core.Model;
using Claustrum.Roles.Sync;
using Claustrum.Roles.Tests.Sync;

namespace Claustrum.Roles.Tests;

// #55 review (2026-10-13): the rules file is judged by what the OPENED handle is, not by what the path said a moment earlier.
// A link to `.git/config` (a CI token), to a non-Markdown file or into any `.git` is never inlined, a pipe is never read, and a
// link swapped in between the path check and the open never lets the outside bytes into the prompt.
public sealed partial class ApiHouseRulesFenceTests : IDisposable
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

    // Windows asks the handle for its final path (an opened symlink answers with the target's); Unix opens by a no-follow walk,
    // which reaches the target by its real names and refuses the link's own.
    [Fact]
    public void AnOpenedSymlinkIsTheTargetsFileNotTheLinks()
    {
        string target = Write("x", "real", "target.md");
        string link = Path.Combine(cwd, "link.md");
        LinkFixtures.FileLink(link, target);

        if (OperatingSystem.IsWindows())
        {
            using FileStream stream = new(link, FileMode.Open, FileAccess.Read);
            string? opened = OpenedFilePath.TryGet(stream.SafeFileHandle);

            Assert.NotNull(opened);
            Assert.EndsWith(Path.Combine("real", "target.md"), opened, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("link.md", opened, StringComparison.Ordinal);
            return;
        }

        using FileStream? viaTarget = PlainFile.OpenNoFollowUnder(SyncRoot.Resolve(cwd), ["real", "target.md"], out string? refusal);
        using FileStream? viaLink = PlainFile.OpenNoFollowUnder(SyncRoot.Resolve(cwd), ["link.md"], out string? linkRefusal);

        Assert.NotNull(viaTarget);
        Assert.Null(refusal);
        Assert.Null(viaLink);
        Assert.NotNull(linkRefusal);
    }

    // macOS puts every temp directory under a link (/var -> /private/var), and a project checked out through a link is the same on
    // any OS: the working directory is spelled with a link in it, the walk starts from its real path, and the fence must still see
    // the file as inside (#110). The refusals hold under the same spelling.
    [Fact]
    public void AWorkingDirectoryReachedThroughALinkStillInlinesItsRulesAndStillRefusesTheSameLinks()
    {
        string linkedCwd = Path.Combine(outside, "cwd-link");
        LinkFixtures.DirectoryLink(linkedCwd, cwd);
        Write("Plain rules.\n", "AGENTS.md");
        Write($"[credential]\ntoken = {Secret}\n", ".git", "config");
        LinkFixtures.FileLink(Path.Combine(cwd, "CLAUDE.md"), Path.Combine(cwd, ".git", "config"));

        RenderedRole rendered = new RoleRenderer(library).Render("code-reviewer", "high", "api", linkedCwd);

        string body = rendered.SystemBody.Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.Contains("### AGENTS.md (inlined; the api backend has no file access)\n\nPlain rules.", body, StringComparison.Ordinal);
        Assert.Contains("### CLAUDE.md (not inlined: its real path is not a Markdown file inside the working directory)", body, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, body, StringComparison.Ordinal);
        Assert.Contains("not a regular .md file outside .git", Assert.Single(rendered.Warnings ?? []), StringComparison.Ordinal);
    }

    [Fact]
    public void ARulesFileLinkedInsideAWorkingDirectoryReachedThroughALinkIsInlined()
    {
        string linkedCwd = Path.Combine(outside, "cwd-link");
        LinkFixtures.DirectoryLink(linkedCwd, cwd);
        Write("Shared rules.\n", "docs", "rules.md");
        LinkFixtures.FileLink(Path.Combine(cwd, "AGENTS.md"), Path.Combine(cwd, "docs", "rules.md"));

        RenderedRole rendered = new RoleRenderer(library).Render("code-reviewer", "high", "api", linkedCwd);

        Assert.Contains("### AGENTS.md (inlined; the api backend has no file access)\n\nShared rules.", rendered.SystemBody.Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Null(rendered.Warnings);
    }

    // The walk opens the RESOLVED path, so a link that stays inside (here through a directory link) is read as the file it names.
    [Fact]
    public void AFileReachedThroughAnInsideDirectoryLinkIsInlinedAsTheFileItNames()
    {
        Write("Docs rules.\n", "docs", "rules.md");
        LinkFixtures.DirectoryLink(Path.Combine(cwd, "shortcut"), Path.Combine(cwd, "docs"));
        LinkFixtures.FileLink(Path.Combine(cwd, "AGENTS.md"), Path.Combine(cwd, "shortcut", "rules.md"));

        RenderedRole rendered = Render();

        Assert.Contains("### AGENTS.md (inlined; the api backend has no file access)\n\nDocs rules.", rendered.SystemBody.Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Null(rendered.Warnings);
    }

    [Fact]
    public void ALinkToGitConfigNamesTheResolvedFileInItsWarning()
    {
        Write($"[credential]\ntoken = {Secret}\n", ".git", "config");
        LinkFixtures.FileLink(Path.Combine(cwd, "AGENTS.md"), Path.Combine(cwd, ".git", "config"));

        (_, string warning) = RenderRefused("### AGENTS.md (not inlined: its real path is not a Markdown file inside the working directory)");

        Assert.StartsWith("AGENTS.md not inlined: the file is ", warning, StringComparison.Ordinal);
    }

    // A second name for a file is a way past the fence nobody can see from this path: `ln .git/config AGENTS.md` carries a CI token
    // into the prompt although the file is a regular .md inside the root. Refused on Unix (nlink) and on Windows (nNumberOfLinks).
    [Theory]
    [InlineData(".git/config")]
    [InlineData("docs/rules.md")]
    [InlineData("outside")]
    public void AFileWithAnotherHardLinkIsNotInlinedWhereverTheOtherNameIs(string other)
    {
        string existing = other switch
        {
            "outside" => Path.Combine(outside, "secret.md"),
            ".git/config" => Write($"[credential]\ntoken = {Secret}\n", ".git", "config"),
            _ => Write(Secret, "docs", "rules.md"),
        };
        File.WriteAllText(existing, Secret);

        HardLink(existing, Path.Combine(cwd, "AGENTS.md"));

        (_, string warning) = RenderRefused("### AGENTS.md (not inlined: it has other hard links)");

        Assert.Contains("has other hard links", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void AFileWhoseOtherHardLinkWasRemovedIsInlinedAgain()
    {
        string existing = Write("Rules.\n", "docs", "rules.md");
        string rules = Path.Combine(cwd, "AGENTS.md");
        HardLink(existing, rules);
        Assert.Contains("(not inlined: it has other hard links)", Render().SystemBody, StringComparison.Ordinal);

        File.Delete(existing);

        Assert.Contains("### AGENTS.md (inlined; the api backend has no file access)\n\nRules.", Render().SystemBody.Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal);
    }

    private static void HardLink(string existing, string link)
    {
        ProcessStartInfo start = OperatingSystem.IsWindows() ? new("cmd.exe") : new("ln");
        if (OperatingSystem.IsWindows())
        {
            start.ArgumentList.Add("/c");
            start.ArgumentList.Add($"mklink /H \"{link}\" \"{existing}\"");
        }
        else
        {
            start.ArgumentList.Add(existing);
            start.ArgumentList.Add(link);
        }

        start.UseShellExecute = false;
        start.RedirectStandardError = true;
        start.RedirectStandardOutput = true;
        start.CreateNoWindow = true;
        using Process process = Process.Start(start) ?? throw new InvalidOperationException($"{start.FileName} did not start");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            Assert.Skip($"a hard link cannot be made here: {(error + stdout.Result).Trim()}");
    }

    // The swap the no-follow walk exists for, at the speed that matters: renameat2(RENAME_EXCHANGE) swaps AGENTS.md and a staged
    // link to a secret outside the root atomically, so at every instant the name is one or the other and a reader racing the
    // swap lands on either. The earlier File.Move flipper below is too slow to have produced the leaking interleaving (a
    // check-then-open design leaked in about 1 render in 5 under this one). Linux only.
    [Fact]
    public void AnAtomicExchangeOfTheRulesFileWithALinkToASecretNeverLeaksTheSecretWhileComposeLoops()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Skip("renameat2(RENAME_EXCHANGE) is Linux only.");

        string secret = Path.Combine(outside, "secret.md");
        File.WriteAllText(secret, Secret);
        string rules = Write("REGULAR-RULES\n", "AGENTS.md");
        string staging = Path.Combine(cwd, "staging.md");
        LinkFixtures.FileLink(staging, secret);
        string houseRules = library.ReadShared("_shared/house-rules.md");

        bool stop = false;
        int exchanges = 0;
        Thread flipper = new(() =>
        {
            while (!Volatile.Read(ref stop))
            {
                if (RenameAt2(AtFdCwd, rules, AtFdCwd, staging, RenameExchange) == 0)
                    Interlocked.Increment(ref exchanges);
            }
        })
        {
            IsBackground = true,
        };
        flipper.Start();

        int iterations = 0;
        int inlined = 0;
        int refused = 0;
        Stopwatch clock = Stopwatch.StartNew();
        try
        {
            while (clock.Elapsed < TimeSpan.FromSeconds(3) || iterations < 1000)
            {
                string composed = ApiHouseRules.Compose(houseRules, cwd, []);
                Assert.DoesNotContain(Secret, composed, StringComparison.Ordinal);
                if (composed.Contains("REGULAR-RULES", StringComparison.Ordinal))
                    inlined++;
                else
                    refused++;

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

        Assert.True(exchanges > 100, $"only {exchanges} exchanges happened");
        Assert.True(iterations >= 500, $"only {iterations} iterations ran");
        Assert.True(inlined > 0, "the regular file was never inlined, so the loop proved nothing");
        Assert.True(refused > 0, "the link was never met, so the loop proved nothing");
    }

    private const int AtFdCwd = -100;
    private const uint RenameExchange = 2;

    [System.Runtime.InteropServices.LibraryImport("libc", EntryPoint = "renameat2", StringMarshalling = System.Runtime.InteropServices.StringMarshalling.Utf8)]
    private static partial int RenameAt2(int oldDirectory, string oldPath, int newDirectory, string newPath, uint flags);

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
