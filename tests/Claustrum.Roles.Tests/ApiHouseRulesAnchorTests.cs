using System.Diagnostics;
using System.Runtime.InteropServices;
using Claustrum.Core.Model;
using Claustrum.Roles.Sync;
using Claustrum.Roles.Tests.Sync;

namespace Claustrum.Roles.Tests;

// #55 / #110 (review 2026-10-14): an isolated api run inlines the rules of its worktree, and that worktree must be exactly where
// the main checkout's real path says (`<main>/.claustrum/worktrees/<job>`). A link at the job directory, at `.claustrum` or at
// `worktrees` (planted by anyone who can write the main checkout) would otherwise make the fence the outside directory itself.
// Compose is told the real path it was promised (WithRulesFrom's expectedRoot); anything else inlines nothing, and the open walks
// from `/` without following a link, so even a link swapped in while it runs is a refusal.
public sealed partial class ApiHouseRulesAnchorTests : IDisposable
{
    private const string Marker = "OUTSIDE-MARKER-TEXT";
    private const string Refused = "(not inlined: the worktree resolves outside the repository's .claustrum/worktrees)";

    private readonly RoleLibrary library = new();
    private readonly string main = Directory.CreateTempSubdirectory("claustrum-anchor-main-").FullName;
    private readonly string outside = Directory.CreateTempSubdirectory("claustrum-anchor-out-").FullName;

    public void Dispose()
    {
        LinkFixtures.DeleteTree(main);
        LinkFixtures.DeleteTree(outside);
    }

    private string TypedJob => Path.Combine(main, ".claustrum", "worktrees", "job");

    // What the engine promises Compose: the main checkout's REAL path plus the job's place under it.
    private string Expected => Path.Combine(SyncRoot.Resolve(main), ".claustrum", "worktrees", "job");

    private ResolvedRole Resolved()
    {
        RenderedRole rendered = new RoleRenderer(library).Render("code-reviewer", "high", "api", main);
        return new ResolvedRole(rendered.Name, rendered.SystemBody, "api", "some-model", rendered.Effort, new PermissionPolicy(PermissionLevel.ReadOnly, []), rendered.Blind, HasReport: true, rendered.Warnings);
    }

    private ResolvedRole Reread(string? expectedRoot) => new RoleRenderer(library).WithRulesFrom(Resolved(), TypedJob, expectedRoot);

    private static void WriteRules(string jobDirectory, string agents, string? claude = null)
    {
        Directory.CreateDirectory(jobDirectory);
        File.WriteAllText(Path.Combine(jobDirectory, "AGENTS.md"), agents);
        if (claude is not null)
            File.WriteAllText(Path.Combine(jobDirectory, "CLAUDE.md"), claude);
    }

    // The three places a link can stand above the files: the job directory, `.claustrum`, `worktrees`. Each leads to a directory
    // that has the same shape below it and the marker in its rules files.
    private void PlantLinkAt(string place)
    {
        switch (place)
        {
            case "job":
                Directory.CreateDirectory(Path.Combine(main, ".claustrum", "worktrees"));
                WriteRules(Path.Combine(outside, "job"), Marker, Marker);
                LinkFixtures.DirectoryLink(TypedJob, Path.Combine(outside, "job"));
                break;
            case "worktrees":
                Directory.CreateDirectory(Path.Combine(main, ".claustrum"));
                WriteRules(Path.Combine(outside, "worktrees", "job"), Marker, Marker);
                LinkFixtures.DirectoryLink(Path.Combine(main, ".claustrum", "worktrees"), Path.Combine(outside, "worktrees"));
                break;
            default:
                WriteRules(Path.Combine(outside, "dot", "worktrees", "job"), Marker, Marker);
                LinkFixtures.DirectoryLink(Path.Combine(main, ".claustrum"), Path.Combine(outside, "dot"));
                break;
        }
    }

    [Theory]
    [InlineData("job")]
    [InlineData("worktrees")]
    [InlineData(".claustrum")]
    public void ALinkAtOrAboveTheWorktreeInlinesNothingAndNamesTheAnchorTwiceWithOneWarning(string place)
    {
        PlantLinkAt(place);

        ResolvedRole rewritten = Reread(Expected);

        Assert.DoesNotContain(Marker, rewritten.SystemPrompt, StringComparison.Ordinal);
        Assert.Equal(2, CountOf(rewritten.SystemPrompt, Refused));
        string warning = Assert.Single(rewritten.Warnings ?? []);
        Assert.Contains("resolves to", warning, StringComparison.Ordinal);
        Assert.Contains(Expected, warning, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("job")]
    [InlineData("worktrees")]
    [InlineData(".claustrum")]
    public void WithoutAnExpectedRootALinkedWorktreeIsFencedAtItselfAsBefore(string place)
    {
        PlantLinkAt(place);

        ResolvedRole rewritten = Reread(expectedRoot: null);

        // The old behaviour the anchor exists to close: the outside directory is its own fence, so its rules are inlined.
        Assert.Contains(Marker, rewritten.SystemPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public void TheAnchoredHappyPathInlinesBothFilesWithNoWarnings()
    {
        WriteRules(TypedJob, "AGENTS-HAPPY\n", "CLAUDE-HAPPY\n");

        ResolvedRole rewritten = Reread(Expected);

        Assert.Contains("AGENTS-HAPPY", rewritten.SystemPrompt, StringComparison.Ordinal);
        Assert.Contains("CLAUDE-HAPPY", rewritten.SystemPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain(Refused, rewritten.SystemPrompt, StringComparison.Ordinal);
        Assert.True(rewritten.Warnings is null or { Length: 0 });
    }

    [Fact]
    public void AnAnchorThatIsNotWhereTheWorktreeResolvesRefusesEvenWithoutAnyLink()
    {
        WriteRules(TypedJob, "AGENTS-HAPPY\n");

        ResolvedRole rewritten = Reread(Path.Combine(outside, ".claustrum", "worktrees", "job"));

        Assert.DoesNotContain("AGENTS-HAPPY", rewritten.SystemPrompt, StringComparison.Ordinal);
        Assert.Equal(2, CountOf(rewritten.SystemPrompt, Refused));
    }

    // The anchored root is the root of the walk, not wherever the typed path resolves at that moment.
    [Fact]
    public void ALinkedRulesFileInsideTheAnchoredWorktreeStillInlinesAsTheFileItNames()
    {
        WriteRules(TypedJob, "unused\n");
        File.WriteAllText(Path.Combine(TypedJob, "shared.md"), "SHARED-RULES\n");
        File.Delete(Path.Combine(TypedJob, "AGENTS.md"));
        LinkFixtures.FileLink(Path.Combine(TypedJob, "AGENTS.md"), Path.Combine(TypedJob, "shared.md"));

        ResolvedRole rewritten = Reread(Expected);

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

            ResolvedRole rewritten = Reread(Expected);

            Assert.Contains("DOCS-RULES", rewritten.SystemPrompt, StringComparison.Ordinal);
        }
        finally
        {
            File.SetUnixFileMode(docs, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
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

        string realRules = Path.Combine(TypedJob);
        WriteRules(realRules, "REGULAR-RULES\n", "REGULAR-CLAUDE\n");
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
        string expected = Expected;

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
                string composed = ApiHouseRules.Compose(houseRules, TypedJob, [], expected);
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
