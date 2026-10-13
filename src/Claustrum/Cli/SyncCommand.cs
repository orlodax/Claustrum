using System.CommandLine;
using Claustrum.Roles.Sync;

namespace Claustrum.Cli;

// docs/PLAN.md §A5/§B4 `claustrum sync [--only claude,opencode,copilot,cursor] [--roles a,b]
// [--global] [--force] [--check|--dry-run]`. Bare `sync` (no --only) still targets only `claude`,
// matching M1/M2's documented default; `--only` accepts any of supportedHarnesses, any other value
// is a usage error (exit 2).
public static class SyncCommand
{
    private static readonly string[] supportedHarnesses = ["claude", "opencode", "copilot", "cursor"];

    public static Command Build()
    {
        Option<string?> only = new("--only") { Description = "Comma-separated harnesses to sync (claude, opencode, copilot, cursor)." };
        Option<string?> roles = new("--roles") { Description = "Comma-separated role names (default: every role)." };
        Option<bool> global = new("--global") { Description = "Write to the user-global agent directories instead of the repo." };
        Option<bool> force = new("--force") { Description = "Overwrite hand-edited files that lack the claustrum:generated marker." };
        Option<bool> check = new("--check") { Description = "Don't write; exit 2 if anything is hand-edited, stale, or missing (for CI)." };
        Option<bool> dryRun = new("--dry-run") { Description = "Don't write; print a unified diff of what would change." };

        Command command = new("sync", "Render the role library into a coding-harness's native files.") { only, roles, global, force, check, dryRun };
        command.SetAction(parseResult => Execute(
            parseResult.GetValue(only),
            parseResult.GetValue(roles),
            parseResult.GetValue(global),
            parseResult.GetValue(force),
            parseResult.GetValue(check),
            parseResult.GetValue(dryRun)));

        return command;
    }

    private static int Execute(string? only, string? rolesOption, bool global, bool force, bool check, bool dryRun)
    {
        if (check && dryRun)
            throw new CliUsageException("use either --check or --dry-run, not both");

        string[] harnesses = only is { Length: > 0 } ? only.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries) : ["claude"];
        string[] unsupported = [.. harnesses.Where(h => !supportedHarnesses.Contains(h))];
        if (unsupported.Length > 0)
        {
            Console.Error.WriteLine($"not yet supported: {string.Join(", ", unsupported)} (supported: {string.Join(", ", supportedHarnesses)})");
            return ExitCodes.Usage;
        }

        string[]? roleFilter = rolesOption is { Length: > 0 }
            ? rolesOption.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            : null;

        SyncMode mode = check ? SyncMode.Check : dryRun ? SyncMode.DryRun : SyncMode.Write;
        SyncResult result = MergeResults(harnesses.Select(harness => SyncOne(harness, roleFilter, global, force, mode)));

        return dryRun ? PrintDryRun(result) : check ? PrintCheck(result) : PrintWrite(result);
    }

    private static SyncResult SyncOne(string harness, string[]? roleFilter, bool global, bool force, SyncMode mode)
    {
        string? globalRoot = global ? AnnounceGlobalRoot(harness) : null;
        string home = AppServices.Platform.HomeDirectory;
        return harness switch
        {
            "claude" => new ClaudeSync(AppServices.RoleLibrary, AppServices.RoleRenderer, home)
                .Sync(Environment.CurrentDirectory, roleFilter, global, force, mode, global ? DesktopTarget() : null, globalRoot),
            "opencode" => new OpencodeSync(AppServices.RoleLibrary, AppServices.RoleRenderer, home)
                .Sync(Environment.CurrentDirectory, roleFilter, global, force, mode, globalRoot),
            "copilot" => new CopilotSync(AppServices.RoleLibrary, AppServices.RoleRenderer, home)
                .Sync(Environment.CurrentDirectory, roleFilter, global, force, mode, globalRoot),
            "cursor" => new CursorSync(AppServices.RoleLibrary, AppServices.RoleRenderer, home)
                .Sync(Environment.CurrentDirectory, roleFilter, global, force, mode, global ? CursorGlobalBinary() : null, globalRoot),
            _ => throw new ArgumentOutOfRangeException(nameof(harness), harness, "not in supportedHarnesses"),
        };
    }

    // Printed in every mode, before the sync: a root moved by an environment variable is the one
    // thing a user cannot see from the file list alone (issue #83).
    private static string AnnounceGlobalRoot(string harness)
    {
        (string path, string reason) = SyncGlobalRoot.Resolve(AppServices.Platform, harness);

        // A linked root is the user's own setup (stow): the syncs still get the path as typed, and the
        // real one is only shown (issue #95).
        string real = SyncRoot.Resolve(path);
        Console.WriteLine(SyncRoot.SamePath(path, real)
            ? $"global root ({harness}): {path} ({reason})"
            : $"global root ({harness}): {path} → {real} ({reason})");
        return path;
    }

    // The absolute path comes from the running process, not from PATH: the desktop app spawns the
    // server from a GUI process whose PATH rarely holds the install directory (issue #24).
    private static ClaudeDesktopTarget? DesktopTarget()
    {
        (string? binaryPath, string? binaryReason) = ClaustrumBinaryPath.Resolve();
        (ClaudeDesktopTarget? target, string? skipReason) = ClaudeDesktopConfig.Resolve(AppServices.Platform, binaryPath, binaryReason);
        if (skipReason is not null)
            Console.WriteLine($"claude desktop config: not written ({skipReason})");

        return target;
    }

    // `~/.cursor/mcp.json` is read by a GUI Cursor, so it needs the absolute binary for the same
    // reason (issue #25). Null skips that one merge: registering the dotnet host under the
    // `claustrum` key would be worse than leaving the file alone.
    private static string? CursorGlobalBinary()
    {
        (string? binaryPath, string? skipReason) = ClaustrumBinaryPath.Resolve();
        if (skipReason is not null)
            Console.WriteLine($"cursor global mcp.json: not written ({skipReason})");

        return binaryPath;
    }

    // Multiple --only harnesses report as one combined result: each list is a simple concatenation
    // (there is no overlap in the paths two different harnesses touch), and ProposedContent (DryRun
    // only) merges by path since no two harnesses ever propose the same one. A role's ignored override
    // is the exception: every harness that syncs the role names it, so it is listed once.
    private static SyncResult MergeResults(IEnumerable<SyncResult> results)
    {
        List<string> written = [];
        List<string> skipped = [];
        List<string> foreign = [];
        Dictionary<string, string>? proposedContent = null;
        Dictionary<string, string> symlinks = [];
        Dictionary<string, string> refused = [];
        Dictionary<string, string> realPaths = [];
        List<string> ignoredOverrides = [];

        foreach (SyncResult result in results)
        {
            written.AddRange(result.Written);
            skipped.AddRange(result.Skipped);
            foreign.AddRange(result.Foreign);
            ignoredOverrides.AddRange(result.IgnoredOverrides ?? []);
            foreach (KeyValuePair<string, string> link in result.Symlinks ?? new Dictionary<string, string>())
                symlinks[link.Key] = link.Value;
            foreach (KeyValuePair<string, string> entry in result.Refused ?? new Dictionary<string, string>())
                refused[entry.Key] = entry.Value;
            foreach (KeyValuePair<string, string> entry in result.RealPaths ?? new Dictionary<string, string>())
                realPaths[entry.Key] = entry.Value;

            if (result.ProposedContent is null)
                continue;

            proposedContent ??= [];
            foreach (KeyValuePair<string, string> entry in result.ProposedContent)
                proposedContent[entry.Key] = entry.Value;
        }

        return new SyncResult(written, skipped, foreign, proposedContent, symlinks, refused, realPaths, [.. ignoredOverrides.Distinct(StringComparer.Ordinal)]);
    }

    private static int PrintWrite(SyncResult result)
    {
        IReadOnlyDictionary<string, string> symlinks = result.Symlinks ?? new Dictionary<string, string>();
        IReadOnlyDictionary<string, string> realPaths = result.RealPaths ?? new Dictionary<string, string>();

        PrintPaths("written", [.. result.Written.Where(path => !symlinks.ContainsKey(path))], realPaths: realPaths);
        PrintPaths(
            "replaced symlink (the link target was left untouched)",
            [.. result.Written.Where(symlinks.ContainsKey).Select(path => $"{path} → {symlinks[path]}{LandsAt(path, realPaths)}")]);
        PrintPaths("skipped (already up to date)", result.Skipped, realPaths: realPaths);
        PrintPaths("foreign (hand-edited; use --force to adopt)", result.Foreign, symlinks, realPaths);
        PrintIgnoredOverrides(result);

        return PrintRefused(result) ? ExitCodes.Usage : ExitCodes.Ok;
    }

    private static int PrintDryRun(SyncResult result)
    {
        IReadOnlyDictionary<string, string> symlinks = result.Symlinks ?? new Dictionary<string, string>();
        IReadOnlyDictionary<string, string> realPaths = result.RealPaths ?? new Dictionary<string, string>();

        foreach (string path in result.Written)
        {
            string? existing = ExistingContent(path, symlinks.ContainsKey(path));
            string relativePath = Path.GetRelativePath(Environment.CurrentDirectory, path);
            if (symlinks.TryGetValue(path, out string? target))
                Console.WriteLine($"would replace symlink {relativePath} → {target}{LandsAt(path, realPaths)}");
            else if (realPaths.TryGetValue(path, out string? real))
                Console.WriteLine($"resolved {relativePath} → {real}");

            Console.WriteLine(UnifiedDiff.Format(relativePath, existing, result.ProposedContent![path]));
        }

        PrintPaths("foreign (hand-edited; use --force to adopt)", result.Foreign, symlinks, realPaths);
        PrintIgnoredOverrides(result);

        return PrintRefused(result) ? ExitCodes.Usage : ExitCodes.Ok;
    }

    // Never read through a leaf link: --force replaces the link and leaves its target alone (#84), so
    // that content is not "removed", and printing it would show a linked secret as deleted lines (#95).
    private static string? ExistingContent(string path, bool isLink)
    {
        if (isLink)
            return null;

        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // For CI (docs/PLAN.md §B4 "--check ... exit 2 on hand-edited/stale/missing"): distinguishing
    // "missing" from "stale" only needs a File.Exists check here, so SyncResult does not need its own
    // separate list for it.
    private static int PrintCheck(SyncResult result)
    {
        IReadOnlyDictionary<string, string> symlinks = result.Symlinks ?? new Dictionary<string, string>();
        IReadOnlyDictionary<string, string> realPaths = result.RealPaths ?? new Dictionary<string, string>();

        // A link is checked first: File.Exists is true for a dangling one too, and either way sync would replace it.
        foreach (string path in result.Written)
        {
            if (symlinks.TryGetValue(path, out string? target))
                Console.WriteLine($"would replace symlink: {path} → {target}{LandsAt(path, realPaths)}");
            else
                Console.WriteLine($"{(File.Exists(path) ? "stale" : "missing")}: {path}{Annotation(path, null, realPaths)}");
        }

        PrintPaths("foreign (hand-edited; use --force to adopt)", result.Foreign, symlinks, realPaths);
        PrintIgnoredOverrides(result);
        bool refused = PrintRefused(result);

        return result.Written.Count > 0 || result.Foreign.Count > 0 || refused ? ExitCodes.Usage : ExitCodes.Ok;
    }

    // Said in every mode and never an error: `--global` renders the library alone, so a repo's own
    // override is not in the global files, and the user would otherwise see no sign it was left out (#99).
    private static void PrintIgnoredOverrides(SyncResult result) =>
        PrintPaths(
            "ignored local overrides (--global renders the library only; a repo's .claustrum/roles/ applies to that repo)",
            result.IgnoredOverrides ?? []);

    // Prints the refused section and says whether there was one: a refused target is a failed sync in
    // every mode, so the caller turns it into exit 2 (issue #95).
    private static bool PrintRefused(SyncResult result)
    {
        if (result.Refused is not { Count: > 0 } refused)
            return false;

        PrintPaths(
            "refused (resolves outside the repo; sync never writes through a link that leaves it)",
            [.. refused.Select(entry => $"{entry.Key} → {entry.Value}")]);
        return true;
    }

    private static void PrintPaths(
        string label, IReadOnlyList<string> paths,
        IReadOnlyDictionary<string, string>? symlinks = null, IReadOnlyDictionary<string, string>? realPaths = null)
    {
        if (paths.Count == 0)
            return;

        Console.WriteLine($"{label}:");
        foreach (string path in paths)
            Console.WriteLine($"  {path}{Annotation(path, symlinks, realPaths)}");
    }

    // Not "resolved →": on a line that already reads `path → target` that would read as the target's resolution.
    private static string LandsAt(string path, IReadOnlyDictionary<string, string> realPaths)
    {
        return realPaths.TryGetValue(path, out string? real) ? $" (lands at {real})" : "";
    }

    // A symlink at the path wins over the resolved-path note: the link target is the more specific fact.
    private static string Annotation(string path, IReadOnlyDictionary<string, string>? symlinks, IReadOnlyDictionary<string, string>? realPaths)
    {
        if (symlinks is not null && symlinks.TryGetValue(path, out string? target))
            return $" (symlink → {target})";

        return realPaths is not null && realPaths.TryGetValue(path, out string? real) ? $" (resolved → {real})" : "";
    }
}
