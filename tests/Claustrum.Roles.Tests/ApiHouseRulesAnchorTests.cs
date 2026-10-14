using System.Diagnostics;
using System.Runtime.InteropServices;
using Claustrum.Core.Model;
using Claustrum.Roles.Sync;
using Claustrum.Roles.Tests.Sync;

namespace Claustrum.Roles.Tests;

// #55 / #110 (review 2026-10-14): an isolated api run inlines the rules of its worktree, and that worktree must be exactly the
// job's directory under the main checkout's `.claustrum/worktrees` (a RulesAnchor: main checkout + job id), links that stay
// inside the repository followed (#104 accepts `.claustrum -> .cache/claustrum`). A link that leaves the repository, or a worktree
// path that is not that directory, inlines nothing; the open walks from `/` without following a link, so even a link swapped in
// while it runs is a refusal.
public sealed partial class ApiHouseRulesAnchorTests : IDisposable
{
    private const string Marker = "OUTSIDE-MARKER-TEXT";
    private const string OutsideHeading = "(not inlined: the job's worktree directory resolves outside the repository)";
    private const string NotTheJobHeading = "(not inlined: the worktree path is not the job's worktree)";
    private const string JobId = "job";

    private readonly RoleLibrary library = new();
    private readonly string main = Directory.CreateTempSubdirectory("claustrum-anchor-main-").FullName;
    private readonly string outside = Directory.CreateTempSubdirectory("claustrum-anchor-out-").FullName;

    public void Dispose()
    {
        LinkFixtures.DeleteTree(main);
        LinkFixtures.DeleteTree(outside);
    }

    private string TypedJob => Path.Combine(main, ".claustrum", "worktrees", JobId);

    private RulesAnchor Anchor => new(main, JobId);

    private ResolvedRole Resolved()
    {
        RenderedRole rendered = new RoleRenderer(library).Render("code-reviewer", "high", "api", main);
        return new ResolvedRole(rendered.Name, rendered.SystemBody, "api", "some-model", rendered.Effort, new PermissionPolicy(PermissionLevel.ReadOnly, []), rendered.Blind, HasReport: true, rendered.Warnings);
    }

    private ResolvedRole Reread(RulesAnchor? anchor, string? rulesCwd = null) => new RoleRenderer(library).WithRulesFrom(Resolved(), rulesCwd ?? TypedJob, anchor);

    private static void WriteRules(string jobDirectory, string agents, string? claude = null)
    {
        Directory.CreateDirectory(jobDirectory);
        File.WriteAllText(Path.Combine(jobDirectory, "AGENTS.md"), agents);
        if (claude is not null)
            File.WriteAllText(Path.Combine(jobDirectory, "CLAUDE.md"), claude);
    }

    // The three places a link can stand above the files: the job directory, `.claustrum`, `worktrees`. `target` is the directory
    // the link leads to for the place: a twin of the same shape below it, holding the marker in its rules files.
    private void PlantLinkAt(string place, string targetRoot)
    {
        switch (place)
        {
            case "job":
                Directory.CreateDirectory(Path.Combine(main, ".claustrum", "worktrees"));
                WriteRules(Path.Combine(targetRoot, "job"), Marker, Marker);
                LinkFixtures.DirectoryLink(TypedJob, Path.Combine(targetRoot, "job"));
                break;
            case "worktrees":
                Directory.CreateDirectory(Path.Combine(main, ".claustrum"));
                WriteRules(Path.Combine(targetRoot, "worktrees", "job"), Marker, Marker);
                LinkFixtures.DirectoryLink(Path.Combine(main, ".claustrum", "worktrees"), Path.Combine(targetRoot, "worktrees"));
                break;
            default:
                WriteRules(Path.Combine(targetRoot, "dot", "worktrees", "job"), Marker, Marker);
                LinkFixtures.DirectoryLink(Path.Combine(main, ".claustrum"), Path.Combine(targetRoot, "dot"));
                break;
        }
    }

    [Theory]
    [InlineData("worktrees")]
    [InlineData(".claustrum")]
    public void ALinkThatLeavesTheRepositoryInlinesNothingAndNamesItTwiceWithOneWarning(string place)
    {
        PlantLinkAt(place, outside);

        ResolvedRole rewritten = Reread(Anchor);

        Assert.DoesNotContain(Marker, rewritten.SystemPrompt, StringComparison.Ordinal);
        Assert.Equal(2, CountOf(rewritten.SystemPrompt, OutsideHeading));
        string warning = Assert.Single(rewritten.Warnings ?? []);
        Assert.Contains("outside the repository", warning, StringComparison.Ordinal);
    }

    // The job directory itself is never resolved (second review, 2026-10-14): linked anywhere, in or out of the repository
    // (the main checkout, another job, .git), it is not the job's worktree and nothing is inlined.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ALinkedJobDirectoryIsNotTheJobsWorktreeWhereverItPoints(bool inside)
    {
        string target = outside;
        if (inside)
        {
            target = Path.Combine(main, ".cache", "claustrum");
            Directory.CreateDirectory(target);
        }

        PlantLinkAt("job", target);

        ResolvedRole rewritten = Reread(Anchor);

        Assert.DoesNotContain(Marker, rewritten.SystemPrompt, StringComparison.Ordinal);
        Assert.Equal(2, CountOf(rewritten.SystemPrompt, NotTheJobHeading));
        Assert.Contains("not the job's worktree", Assert.Single(rewritten.Warnings ?? []), StringComparison.Ordinal);
    }

    // #104: a dotfiles-style `.claustrum -> .cache/claustrum` stays inside the repository, so the run's own worktree is inlined.
    [Theory]
    [InlineData("worktrees")]
    [InlineData(".claustrum")]
    public void ALinkThatStaysInsideTheRepositoryIsFollowedAndTheRulesAreInlined(string place)
    {
        string cache = Path.Combine(main, ".cache", "claustrum");
        Directory.CreateDirectory(cache);
        PlantLinkAt(place, cache);

        ResolvedRole rewritten = Reread(Anchor);

        Assert.Contains(Marker, rewritten.SystemPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain(OutsideHeading, rewritten.SystemPrompt, StringComparison.Ordinal);
        Assert.True(rewritten.Warnings is null or { Length: 0 });
    }

    [Theory]
    [InlineData("job")]
    [InlineData("worktrees")]
    [InlineData(".claustrum")]
    public void WithoutAnAnchorALinkedWorktreeIsFencedAtItselfAsBefore(string place)
    {
        PlantLinkAt(place, outside);

        ResolvedRole rewritten = Reread(anchor: null);

        // The old behaviour the anchor exists to close: the outside directory is its own fence, so its rules are inlined.
        Assert.Contains(Marker, rewritten.SystemPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public void TheAnchoredHappyPathInlinesBothFilesWithNoWarnings()
    {
        WriteRules(TypedJob, "AGENTS-HAPPY\n", "CLAUDE-HAPPY\n");

        ResolvedRole rewritten = Reread(Anchor);

        Assert.Contains("AGENTS-HAPPY", rewritten.SystemPrompt, StringComparison.Ordinal);
        Assert.Contains("CLAUDE-HAPPY", rewritten.SystemPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("not inlined", rewritten.SystemPrompt, StringComparison.Ordinal);
        Assert.True(rewritten.Warnings is null or { Length: 0 });
    }

    // A worktree path that resolves to another place in the repository (a planted directory, a swapped path) is not the job's.
    [Fact]
    public void AWorktreePathThatResolvesToAnotherPlaceInTheRepositoryIsNotTheJobsWorktree()
    {
        WriteRules(TypedJob, "THE-JOBS\n");
        string other = Path.Combine(main, "somewhere-else");
        WriteRules(other, "ELSEWHERE\n", "ELSEWHERE\n");

        ResolvedRole rewritten = Reread(Anchor, other);

        Assert.DoesNotContain("ELSEWHERE", rewritten.SystemPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("THE-JOBS", rewritten.SystemPrompt, StringComparison.Ordinal);
        Assert.Equal(2, CountOf(rewritten.SystemPrompt, NotTheJobHeading));
        Assert.Contains("not the job's worktree", Assert.Single(rewritten.Warnings ?? []), StringComparison.Ordinal);
    }

    [Fact]
    public void AnotherJobsAnchorOnThisWorktreeIsNotTheJobsWorktree()
    {
        WriteRules(TypedJob, "THE-JOBS\n");

        ResolvedRole rewritten = Reread(new RulesAnchor(main, "another-job"));

        Assert.DoesNotContain("THE-JOBS", rewritten.SystemPrompt, StringComparison.Ordinal);
        Assert.Equal(2, CountOf(rewritten.SystemPrompt, NotTheJobHeading));
    }

    [Fact]
    public void ALinkedRulesFileInsideTheAnchoredWorktreeStillInlinesAsTheFileItNames()
    {
        WriteRules(TypedJob, "unused\n");
        File.WriteAllText(Path.Combine(TypedJob, "shared.md"), "SHARED-RULES\n");
        File.Delete(Path.Combine(TypedJob, "AGENTS.md"));
        LinkFixtures.FileLink(Path.Combine(TypedJob, "AGENTS.md"), Path.Combine(TypedJob, "shared.md"));

        ResolvedRole rewritten = Reread(Anchor);

        Assert.Contains("SHARED-RULES", rewritten.SystemPrompt, StringComparison.Ordinal);
    }

    // A directory searchable but not readable (mode 0311) is walked like a path open would: the open uses O_PATH on Linux. macOS
    // opens directories for reading and refuses by design, so the case is Linux's. Skipped for an account modes do not stop.
    [Fact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void ARulesFileBehindADirectoryThatIsSearchableButNotReadableIsInlinedOnLinux()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Skip("O_PATH directory opens are Linux's; macOS still refuses a directory it cannot read, by design.");

        WriteRules(TypedJob, "unused\n");
        string docs = Path.Combine(TypedJob, "docs");
        Directory.CreateDirectory(docs);
        File.WriteAllText(Path.Combine(docs, "rules.md"), "DOCS-RULES\n");
        File.Delete(Path.Combine(TypedJob, "AGENTS.md"));
        LinkFixtures.FileLink(Path.Combine(TypedJob, "AGENTS.md"), Path.Combine(docs, "rules.md"));
        File.SetUnixFileMode(docs, UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        try
        {
            try
            {
                _ = Directory.GetFileSystemEntries(docs);
                Assert.Skip("this account lists a mode-0311 directory (root, or a capability that bypasses modes).");
            }
            catch (UnauthorizedAccessException)
            {
                // The point of the case.
            }

            ResolvedRole rewritten = Reread(Anchor);

            Assert.Contains("DOCS-RULES", rewritten.SystemPrompt, StringComparison.Ordinal);
        }
        finally
        {
            File.SetUnixFileMode(docs, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    // Windows: the root a handle's final path is judged against is the MAIN checkout's final spelling plus the job's own relative
    // part, never one recomputed from the worktree path. A missing directory is placed under its nearest existing ancestor, as
    // WindowsFinalPath.TryResolve does by design (PR #116 Windows CI).
    [Fact]
    public void OnWindowsTheRootSpellingIsTheFinalPathJoinedWithWhatIsBelowIt()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Skip("RootSpelling asks the Windows final-path API; elsewhere it answers null.");

        string below = Path.Combine(".claustrum", "worktrees", JobId);
        string? spelling = OpenedFilePath.RootSpelling(main, below);

        Assert.NotNull(spelling);
        Assert.EndsWith(below, spelling, StringComparison.OrdinalIgnoreCase);
        string? missing = OpenedFilePath.RootSpelling(Path.Combine(main, "no-such-directory"), below);
        Assert.NotNull(missing);
        Assert.EndsWith(Path.Combine("no-such-directory", below), missing, StringComparison.OrdinalIgnoreCase);
    }

    // Windows: the job directory swapped for a junction to a directory that holds rules files inlines nothing (the path check or
    // the handle's final path catches it).
    [Fact]
    public void OnWindowsAJobDirectoryThatIsAJunctionToAnOutsideDirectoryInlinesNothing()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Skip("a junction is a Windows directory-link kind.");

        Directory.CreateDirectory(Path.Combine(main, ".claustrum", "worktrees"));
        WriteRules(Path.Combine(outside, "job"), Marker, Marker);
        LinkFixtures.Junction(TypedJob, Path.Combine(outside, "job"));

        ResolvedRole rewritten = Reread(Anchor);

        Assert.DoesNotContain(Marker, rewritten.SystemPrompt, StringComparison.Ordinal);
    }

    // The anchor holds while the names are swapped as fast as a writer can: renameat2(RENAME_EXCHANGE) exchanges the real
    // directory with a staged link to the outside twin atomically, at each of the three places. Whatever the reader gets is the
    // real rules or a refusal, never the marker. Linux only; about 3 s each.
    [Theory]
    [InlineData("job")]
    [InlineData("worktrees")]
    [InlineData(".claustrum")]
    public void AnAtomicExchangeOfTheWorktreeOrItsParentsWithALinkToAnOutsideTwinNeverLeaksTheMarker(string place)
    {
        if (!OperatingSystem.IsLinux())
            Assert.Skip("renameat2(RENAME_EXCHANGE) is Linux only.");

        WriteRules(TypedJob, "REGULAR-RULES\n", "REGULAR-CLAUDE\n");
        (string swapped, string staging, string twin) = place switch
        {
            "job" => (TypedJob, Path.Combine(main, ".claustrum", "worktrees", "stage"), Path.Combine(outside, "job")),
            "worktrees" => (Path.Combine(main, ".claustrum", "worktrees"), Path.Combine(main, ".claustrum", "stage"), Path.Combine(outside, "worktrees")),
            _ => (Path.Combine(main, ".claustrum"), Path.Combine(main, "stage"), Path.Combine(outside, "dot")),
        };
        string twinJob = place switch
        {
            "job" => twin,
            "worktrees" => Path.Combine(twin, "job"),
            _ => Path.Combine(twin, "worktrees", "job"),
        };
        WriteRules(twinJob, Marker, Marker);
        LinkFixtures.DirectoryLink(staging, twin);
        string houseRules = library.ReadShared("_shared/house-rules.md");

        bool stop = false;
        int exchanges = 0;
        Thread flipper = new(() =>
        {
            while (!Volatile.Read(ref stop))
            {
                if (RenameAt2(AtFdCwd, swapped, AtFdCwd, staging, RenameExchange) == 0)
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
            while (clock.Elapsed < TimeSpan.FromSeconds(3) || iterations < 500)
            {
                string composed = ApiHouseRules.Compose(houseRules, TypedJob, [], Anchor);
                Assert.DoesNotContain(Marker, composed, StringComparison.Ordinal);
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
        Assert.True(inlined > 0, "the real rules were never inlined, so the loop proved nothing");
        Assert.True(refused > 0, "the link was never met, so the loop proved nothing");
    }

    private static int CountOf(string text, string needle) => text.Split(needle).Length - 1;

    private const int AtFdCwd = -100;
    private const uint RenameExchange = 2;

    [LibraryImport("libc", EntryPoint = "renameat2", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int RenameAt2(int oldDirectory, string oldPath, int newDirectory, string newPath, uint flags);
}
