using System.ComponentModel;
using System.Text.RegularExpressions;
using Claustrum.Casts;
using Claustrum.Cli;
using Claustrum.Core.Git;

namespace Claustrum.Coordination;

// #74: in a git repository `coordinate` runs its architect in `.claustrum/worktrees/<job id>` on
// `claustrum/<job id>`, cut from HEAD — a tree holding the committed files and nothing else, from which
// its children read their cast, claustrum.json and .claustrum/roles. What that tree has to agree on
// with the operator's checkout is refused here, before a job exists (NOTES.md "coordinate runs the
// architect in its own worktree").
public static partial class ArchitectWorktree
{
    // init's .gitignore block. The runner's commit leaves these out whatever the rules say (JobWorktree), but
    // the architect's own `git add -A` does not, and git's plain `worktree remove` refuses a worktree holding
    // an unignored brief or lock: `jobs clean` could never remove it (exit 128, measured 2026-10-09, git 2.56).
    private const string WorktreesDirectory = ".claustrum/worktrees/";
    private const string BriefsDirectory = ".claustrum/briefs/";
    private const string LocksDirectory = ".claustrum/locks/";

    // F4: what `claustrum init` writes and a harness reads from its cwd, the worktree. Ignored, one is the
    // operator's local copy on purpose; untracked, it is init's output nobody committed (G1: tracked with a
    // local edit, the worktree gets HEAD's copy — a warning, not a refusal).
    private static readonly string[] harnessConfigs = [".mcp.json", "opencode.json", "opencode.jsonc"];

    private const string RolesDirectory = ".claustrum/roles/";

    // G5: `jobs clean` knows a probe left by a hard kill by this prefix (TryRemoveStrayProbeAsync).
    private const string ProbePrefix = "probe-";

    private const int NamedCap = 5;

    /// <summary>
    /// Refuses, as a <see cref="CliUsageException"/> (exit 2) naming what to fix: a cwd below the git root; a cast,
    /// `claustrum.json` or a role file under `.claustrum/roles/` that differs from HEAD — modified, staged, untracked,
    /// ignored, or hidden by `skip-worktree`/`assume-unchanged`; an uncommitted file holding the machinery's ignore
    /// rules; an untracked harness config `claustrum init` writes; and a machinery directory no committed rule
    /// ignores. Returns what does not refuse: a tracked harness config whose local edit stays out of the worktree.
    /// </summary>
    public static async Task<string[]> RequireReadyAsync(string cwd, string gitRoot, string castName, CancellationToken cancellationToken)
    {
        // The worktree is the whole repository: from a subdirectory, children given its root as --cwd
        // would look for the cast where the operator never put it.
        if (!string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(cwd)), Path.TrimEndingDirectorySeparator(gitRoot), StringComparison.Ordinal))
        {
            throw new CliUsageException(
                $"coordinate runs the architect in a worktree of the whole repository, whose children read the cast from its root: "
                + $"run it from {gitRoot} (or --cwd \"{gitRoot}\"), with the cast in .claustrum/casts/ there");
        }

        (string[] unignored, string[] ruleFiles) = await MachineryIgnoresAsync(gitRoot, cancellationToken);
        string[] paths =
        [
            OnDiskPath(gitRoot, CastStore.PathFor(cwd, castName)),
            OnDiskPath(gitRoot, Path.Combine(gitRoot, "claustrum.json")),
            ".claustrum/roles",
            .. ruleFiles,
            .. harnessConfigs,
        ];
        Difference[] differences = [.. await UncommittedAsync(gitRoot, paths, cancellationToken), .. await HiddenAsync(gitRoot, paths, cancellationToken)];

        List<string> required = [];
        List<string> rules = [];
        List<string> harness = [];
        List<string> warnings = [];
        foreach (Difference difference in differences.Where(difference => IsRead(difference.Path)))
        {
            string named = $"{difference.Path} ({difference.State})";
            if (ruleFiles.Contains(difference.Path, StringComparer.Ordinal))
                rules.Add($"{difference.Path} ({difference.State} — the worktree gets HEAD's ignore rules; commit it first)");
            else if (!harnessConfigs.Contains(difference.Path, StringComparer.Ordinal))
                required.Add(named);
            else if (difference.InHead)
                warnings.Add($"{named}: the architect's worktree gets HEAD's copy — your local edit stays out of it");
            else
                harness.Add(named);
        }

        List<string> committed = [];
        if (required.Count > 0)
            committed.Add($"commit (or un-ignore) {Named(required)} first");
        if (rules.Count > 0)
            committed.Add(Named(rules));
        if (harness.Count > 0)
            committed.Add($"commit or git-ignore {Named(harness)} — an ignored local copy is fine");

        List<string> problems = [];
        if (committed.Count > 0)
            problems.Add($"coordinate runs the architect in a worktree, which sees only committed files — {string.Join("; ", committed)}");
        if (unignored.Length > 0)
        {
            problems.Add(
                $"coordinate runs the architect in a worktree and commits what it leaves there — git-ignore {string.Join(", ", unignored)} in a committed "
                + ".gitignore first (`claustrum init` writes these rules), or the architect's own `git add` can take in its children's worktrees, "
                + "briefs and lock files, and `claustrum jobs clean` cannot remove its worktree");
        }

        if (problems.Count > 0)
            throw new CliUsageException(string.Join("; ", problems));

        return [.. warnings];
    }

    /// <summary>
    /// G5: a `coordinate` killed between making its probe and removing it leaves an empty
    /// `.claustrum/worktrees/probe-&lt;hex&gt;`, which git calls "not a working tree" on every later `jobs clean`.
    /// Deletes <paramref name="directory"/> only when it is exactly that — so named, empty, and no worktree git
    /// lists — and says whether it did. Anything else, a non-empty one included, is left to the caller.
    /// </summary>
    public static async Task<bool> TryRemoveStrayProbeAsync(string cwd, string directory, CancellationToken cancellationToken)
    {
        string name = Path.GetFileName(directory);
        try
        {
            if (!name.StartsWith(ProbePrefix, StringComparison.Ordinal) || Directory.EnumerateFileSystemEntries(directory).Any())
                return false;

            // By its tail, case-insensitively: git lists a realpath, and a false match only keeps the directory.
            (int exitCode, string listed, _) = await GitProcess.RunAsync(cwd, ["worktree", "list", "--porcelain"], cancellationToken);
            string tail = $"/.claustrum/worktrees/{name}";
            if (exitCode != 0 || listed.Split('\n').Any(line => line.StartsWith("worktree ", StringComparison.Ordinal)
                && line.TrimEnd('\r').Replace('\\', '/').EndsWith(tail, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            // Non-recursive: whatever was written into it since the check above makes this throw, and it stays.
            Directory.Delete(directory);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or TimeoutException or InvalidOperationException)
        {
            // Not provably a stray probe: `jobs clean` handles it like any other entry, and reports what git says.
            return false;
        }
    }

    // Which machinery directories a rule ignores, and the in-tree files those rules live in: only those
    // can differ in the worktree — `.git/info/exclude` and the global excludes file are shared by every
    // worktree. A `!` pattern is a match that un-ignores.
    private static async Task<(string[] Unignored, string[] RuleFiles)> MachineryIgnoresAsync(string gitRoot, CancellationToken cancellationToken)
    {
        // F1 (2026-10-09): asked about the shapes Claustrum really writes there — a whitelist `.gitignore`
        // (`*`, `!*/`, `!*.md`) ignores a file named `probe` and un-ignores a worktree directory and a brief.
        // git calls a path a directory only when one is on disk, so that probe is made, then removed.
        string worktrees = Path.Combine(gitRoot, ".claustrum", "worktrees");
        bool madeWorktrees = !Directory.Exists(worktrees);
        string probeName = $"{ProbePrefix}{Random.Shared.Next():x8}";
        (string Directory, string Probe)[] probes =
        [
            (WorktreesDirectory, $"{WorktreesDirectory}{probeName}/"),
            (BriefsDirectory, $"{BriefsDirectory}1-builder.md"),
            (LocksDirectory, $"{LocksDirectory}default__builder.1.lock"),
        ];

        string output;
        try
        {
            Directory.CreateDirectory(Path.Combine(worktrees, probeName));

            // Exit 1 is "none of them is ignored", an answer like any other.
            output = await GitAsync(gitRoot, "check-ignore", ["check-ignore", "-v", "--no-index", "--", .. probes.Select(probe => probe.Probe)], 1, cancellationToken);

            // G5: a `jobs clean` beside this run deletes an empty probe, and git's answer about a path that is
            // no directory is the one a whitelist `.gitignore` gets wrong — so it is not taken.
            if (!Directory.Exists(Path.Combine(worktrees, probeName)))
                throw new CliUsageException($"{Path.Combine(worktrees, probeName)}, coordinate's probe, was removed while git was asked about it — run coordinate again");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new CliUsageException($"coordinate runs the architect in a worktree under {worktrees}, which could not be written: {ex.Message}");
        }
        finally
        {
            TryDeleteEmpty(Path.Combine(worktrees, probeName));
            if (madeWorktrees)
                TryDeleteEmpty(worktrees);
        }

        HashSet<string> ignored = new(StringComparer.Ordinal);
        HashSet<string> ruleFiles = new(StringComparer.Ordinal);
        foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int tab = line.LastIndexOf('\t');
            if (tab < 0 || CheckIgnoreRule().Match(line[..tab]) is not { Success: true } rule || rule.Groups["pattern"].Value.StartsWith('!'))
                continue;

            ignored.Add(line[(tab + 1)..]);
            string source = rule.Groups["source"].Value;
            if (!Path.IsPathRooted(source) && !source.StartsWith(".git/", StringComparison.Ordinal))
                ruleFiles.Add(source);
        }

        return ([.. probes.Where(probe => !ignored.Contains(probe.Probe)).Select(probe => probe.Directory)], [.. ruleFiles]);
    }

    // Non-recursive: the probe and a `.claustrum/worktrees` made only for it are empty, unless a run beside
    // this one has just put a worktree there — which stays.
    private static void TryDeleteEmpty(string directory)
    {
        try
        {
            Directory.Delete(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An empty directory left behind is read by nothing; not worth failing a refusal or a run over.
        }
    }

    // F3 (2026-10-09): a case-insensitive filesystem opens `spawned.json` for `--cast Spawned`, while a
    // pathspec matches case-sensitively — so git is asked about the name on disk, not the one typed.
    private static string OnDiskPath(string gitRoot, string path)
    {
        string full = Path.GetFullPath(path);
        if (File.Exists(full) && Path.GetDirectoryName(full) is { } directory)
        {
            string name = Path.GetFileName(full);
            try
            {
                string[] entries = [.. Directory.EnumerateFiles(directory).Select(Path.GetFileName).OfType<string>()];
                if (!entries.Contains(name, StringComparer.Ordinal)
                    && entries.FirstOrDefault(entry => string.Equals(entry, name, StringComparison.OrdinalIgnoreCase)) is { } actual)
                {
                    full = Path.Combine(directory, actual);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A directory that opens its files but will not list them: the typed name is all there is to ask about.
            }
        }

        return Path.GetRelativePath(gitRoot, full).Replace(Path.DirectorySeparatorChar, '/');
    }

    // `--untracked-files=all`: under `status.showUntrackedFiles=no` a plain `--porcelain --ignored` lists
    // neither untracked nor ignored files (measured 2026-10-09, review R2's trap), and `normal` collapses a
    // new role to `?? .claustrum/roles/<role>/`, a path IsRead cannot judge (G1). `-z` keeps paths unquoted;
    // `--literal-pathspecs`, because a cast name is the caller's text.
    private static async Task<Difference[]> UncommittedAsync(string gitRoot, string[] paths, CancellationToken cancellationToken)
    {
        string output = await GitAsync(
            gitRoot, "status", ["--literal-pathspecs", "status", "--porcelain", "-z", "--ignored", "--untracked-files=all", "--", .. paths], 0, cancellationToken);

        List<Difference> found = [];
        string[] entries = output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        for (int index = 0; index < entries.Length; index++)
        {
            string entry = entries[index];
            if (entry.Length < 4)
                continue;

            // A rename or a copy is followed by its source path, which is not an entry of its own.
            if (entry[0] is 'R' or 'C' || entry[1] is 'R' or 'C')
                index++;

            string code = entry[..2];
            if (code == "!!" && harnessConfigs.Contains(entry[3..], StringComparer.Ordinal))
                continue;

            found.Add(new Difference(entry[3..], State(code), InHead(code)));
        }

        return [.. found];
    }

    // F2 (2026-10-09): `update-index --skip-worktree` or `--assume-unchanged` hides a local edit from
    // status, so the operator's copy and HEAD's can differ unseen. `ls-files -v` tags the first `S`/`s`
    // and the second in lower case (measured, git 2.56); an unmerged path is `M`, and status names it.
    // The bits sit on tracked paths, so HEAD has a copy of each.
    private static async Task<Difference[]> HiddenAsync(string gitRoot, string[] paths, CancellationToken cancellationToken)
    {
        string output = await GitAsync(gitRoot, "ls-files", ["--literal-pathspecs", "ls-files", "-v", "-z", "--", .. paths], 0, cancellationToken);
        return [.. output.Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Where(entry => entry.Length > 2 && (entry[0] is 'S' or 's' || char.IsLower(entry[0])))
            .Select(entry => new Difference(entry[2..], entry[0] is 'S' or 's' ? "skip-worktree" : "assume-unchanged", InHead: true))];
    }

    private static string State(string code) => code switch
    {
        "??" => "untracked",
        "!!" => "ignored",
        _ => "uncommitted changes",
    };

    // Untracked, ignored, staged as new (`A`; `add -N`'s ` A`) or the new side of a staged rename or copy:
    // HEAD has no such path, so the worktree has no copy of it at all.
    private static bool InHead(string code) => code is not ("??" or "!!") && code[0] is not ('A' or 'R' or 'C') && code[1] != 'A';

    // G1: under .claustrum/roles RoleLibrary reads these and nothing else, so an ignored `.DS_Store`, swap file
    // or `~` backup there is nobody's input. Case-insensitive, as a case-insensitive filesystem opens `role.md`
    // for `ROLE.md`; on Linux that refuses an untracked `role.md` nothing reads, the cheaper mistake.
    private static bool IsRead(string path) =>
        !path.StartsWith(RolesDirectory, StringComparison.Ordinal) || RoleFile().IsMatch(path);

    private static string Named(List<string> paths) =>
        string.Join(", ", paths.Take(NamedCap)) + (paths.Count > NamedCap ? $" (and {paths.Count - NamedCap} more)" : "");

    // A git that fails here would fail the worktree add after the mint, leaving a job nothing closes:
    // refused now, with exit 2 and nothing minted, like a failed `gh`.
    private static async Task<string> GitAsync(string gitRoot, string verb, string[] args, int highestAnswer, CancellationToken cancellationToken)
    {
        string reason;
        try
        {
            (int exitCode, string stdout, string stderr) = await GitProcess.RunAsync(gitRoot, args, cancellationToken);
            if (exitCode >= 0 && exitCode <= highestAnswer)
                return stdout;

            reason = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? $"exit {exitCode}";
        }
        catch (Exception ex) when (ex is Win32Exception or TimeoutException or IOException or InvalidOperationException)
        {
            reason = ex.Message;
        }

        throw new CliUsageException($"coordinate runs the architect in a git worktree, and `git {verb}` failed in {gitRoot}: {reason}");
    }

    // `<source>:<line number>:<pattern>`, the part of a `check-ignore -v` line before its tab. Lazy, so
    // a Windows source's drive colon stays inside <source>.
    [GeneratedRegex(@"^(?<source>.+?):\d+:(?<pattern>.*)$")]
    private static partial Regex CheckIgnoreRule();

    // RoleLibrary.LoadRole's `<role>/role.json` and `<role>/ROLE.md`, ReadPart's `<role>/parts/<part>.<harness|default>.md`.
    [GeneratedRegex(@"^\.claustrum/roles/[^/]+/(role\.json|ROLE\.md|parts/[^/]+\.md)$", RegexOptions.IgnoreCase)]
    private static partial Regex RoleFile();

    // One path whose operator copy and HEAD's differ: State says how, InHead whether HEAD has the path at all.
    private sealed record Difference(string Path, string State, bool InHead);
}
