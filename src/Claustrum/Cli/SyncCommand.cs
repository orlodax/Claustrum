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
        Console.WriteLine($"global root ({harness}): {path} ({reason})");
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
    // only) merges by path since no two harnesses ever propose the same one.
    private static SyncResult MergeResults(IEnumerable<SyncResult> results)
    {
        List<string> written = [];
        List<string> skipped = [];
        List<string> foreign = [];
        Dictionary<string, string>? proposedContent = null;
        Dictionary<string, string> symlinks = [];

        foreach (SyncResult result in results)
        {
            written.AddRange(result.Written);
            skipped.AddRange(result.Skipped);
            foreign.AddRange(result.Foreign);
            foreach (KeyValuePair<string, string> link in result.Symlinks ?? new Dictionary<string, string>())
                symlinks[link.Key] = link.Value;

            if (result.ProposedContent is null)
                continue;

            proposedContent ??= [];
            foreach (KeyValuePair<string, string> entry in result.ProposedContent)
                proposedContent[entry.Key] = entry.Value;
        }

        return new SyncResult(written, skipped, foreign, proposedContent, symlinks);
    }

    private static int PrintWrite(SyncResult result)
    {
        IReadOnlyDictionary<string, string> symlinks = result.Symlinks ?? new Dictionary<string, string>();

        PrintPaths("written", [.. result.Written.Where(path => !symlinks.ContainsKey(path))]);
        PrintPaths(
            "replaced symlink (the link target was left untouched)",
            [.. result.Written.Where(symlinks.ContainsKey).Select(path => $"{path} → {symlinks[path]}")]);
        PrintPaths("skipped (already up to date)", result.Skipped);
        PrintPaths("foreign (hand-edited; use --force to adopt)", result.Foreign, symlinks);

        return ExitCodes.Ok;
    }

    private static int PrintDryRun(SyncResult result)
    {
        IReadOnlyDictionary<string, string> symlinks = result.Symlinks ?? new Dictionary<string, string>();

        foreach (string path in result.Written)
        {
            // File.Exists is true for a dangling link on Unix but ReadAllText then throws: the diff's
            // "before" is nothing when the link leads nowhere.
            bool readable = File.Exists(path) && (!symlinks.ContainsKey(path) || File.ResolveLinkTarget(path, returnFinalTarget: true)?.Exists == true);
            string? existing = readable ? File.ReadAllText(path) : null;
            string relativePath = Path.GetRelativePath(Environment.CurrentDirectory, path);
            if (symlinks.TryGetValue(path, out string? target))
                Console.WriteLine($"would replace symlink {relativePath} → {target}");

            Console.WriteLine(UnifiedDiff.Format(relativePath, existing, result.ProposedContent![path]));
        }

        PrintPaths("foreign (hand-edited; use --force to adopt)", result.Foreign, symlinks);

        return ExitCodes.Ok;
    }

    // For CI (docs/PLAN.md §B4 "--check ... exit 2 on hand-edited/stale/missing"): distinguishing
    // "missing" from "stale" only needs a File.Exists check here, so SyncResult does not need its own
    // separate list for it.
    private static int PrintCheck(SyncResult result)
    {
        IReadOnlyDictionary<string, string> symlinks = result.Symlinks ?? new Dictionary<string, string>();

        // A dangling link fails File.Exists but is not missing: something is there, and sync would replace it.
        foreach (string path in result.Written)
        {
            if (symlinks.TryGetValue(path, out string? target))
                Console.WriteLine($"stale: {path} (symlink → {target})");
            else
                Console.WriteLine($"{(File.Exists(path) ? "stale" : "missing")}: {path}");
        }

        PrintPaths("foreign (hand-edited; use --force to adopt)", result.Foreign, symlinks);

        return result.Written.Count > 0 || result.Foreign.Count > 0 ? ExitCodes.Usage : ExitCodes.Ok;
    }

    private static void PrintPaths(string label, IReadOnlyList<string> paths, IReadOnlyDictionary<string, string>? symlinks = null)
    {
        if (paths.Count == 0)
            return;

        Console.WriteLine($"{label}:");
        foreach (string path in paths)
            Console.WriteLine(symlinks is not null && symlinks.TryGetValue(path, out string? target)
                ? $"  {path} (symlink → {target})"
                : $"  {path}");
    }
}
