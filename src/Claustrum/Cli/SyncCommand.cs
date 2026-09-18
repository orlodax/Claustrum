using System.CommandLine;
using Claustrum.Roles.Sync;

namespace Claustrum.Cli;

// docs/PLAN.md §A5/§B4 `claustrum sync [--only claude,opencode] [--roles a,b] [--global] [--force]
// [--check|--dry-run]`. Bare `sync` (no --only) still targets only `claude`, matching M1/M2's
// documented default; `--only` accepts any of supportedHarnesses, any other value is a usage error
// (exit 2). cursor/copilot have no Sync implementation yet (fixture-only backends per NOTES.md).
public static class SyncCommand
{
    private static readonly string[] supportedHarnesses = ["claude", "opencode"];

    public static Command Build()
    {
        Option<string?> only = new("--only") { Description = "Comma-separated harnesses to sync (claude, opencode)." };
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

    private static SyncResult SyncOne(string harness, string[]? roleFilter, bool global, bool force, SyncMode mode) => harness switch
    {
        "claude" => new ClaudeSync(AppServices.RoleLibrary, AppServices.RoleRenderer, AppServices.Platform.HomeDirectory)
            .Sync(Environment.CurrentDirectory, roleFilter, global, force, mode),
        "opencode" => new OpencodeSync(AppServices.RoleLibrary, AppServices.RoleRenderer, AppServices.Platform.HomeDirectory)
            .Sync(Environment.CurrentDirectory, roleFilter, global, force, mode),
        _ => throw new ArgumentOutOfRangeException(nameof(harness), harness, "not in supportedHarnesses"),
    };

    // Multiple --only harnesses report as one combined result: each list is a simple concatenation
    // (there is no overlap in the paths two different harnesses touch), and ProposedContent (DryRun
    // only) merges by path since no two harnesses ever propose the same one.
    private static SyncResult MergeResults(IEnumerable<SyncResult> results)
    {
        List<string> written = [];
        List<string> skipped = [];
        List<string> foreign = [];
        Dictionary<string, string>? proposedContent = null;

        foreach (SyncResult result in results)
        {
            written.AddRange(result.Written);
            skipped.AddRange(result.Skipped);
            foreign.AddRange(result.Foreign);
            if (result.ProposedContent is null)
                continue;

            proposedContent ??= [];
            foreach (KeyValuePair<string, string> entry in result.ProposedContent)
                proposedContent[entry.Key] = entry.Value;
        }

        return new SyncResult(written, skipped, foreign, proposedContent);
    }

    private static int PrintWrite(SyncResult result)
    {
        PrintPaths("written", result.Written);
        PrintPaths("skipped (already up to date)", result.Skipped);
        PrintPaths("foreign (hand-edited; use --force to adopt)", result.Foreign);

        return ExitCodes.Ok;
    }

    private static int PrintDryRun(SyncResult result)
    {
        foreach (string path in result.Written)
        {
            string? existing = File.Exists(path) ? File.ReadAllText(path) : null;
            string relativePath = Path.GetRelativePath(Environment.CurrentDirectory, path);
            Console.WriteLine(UnifiedDiff.Format(relativePath, existing, result.ProposedContent![path]));
        }

        PrintPaths("foreign (hand-edited; use --force to adopt)", result.Foreign);

        return ExitCodes.Ok;
    }

    // For CI (docs/PLAN.md §B4 "--check ... exit 2 on hand-edited/stale/missing"): distinguishing
    // "missing" from "stale" only needs a File.Exists check here, so SyncResult does not need its own
    // separate list for it.
    private static int PrintCheck(SyncResult result)
    {
        foreach (string path in result.Written)
            Console.WriteLine($"{(File.Exists(path) ? "stale" : "missing")}: {path}");

        PrintPaths("foreign (hand-edited; use --force to adopt)", result.Foreign);

        return result.Written.Count > 0 || result.Foreign.Count > 0 ? ExitCodes.Usage : ExitCodes.Ok;
    }

    private static void PrintPaths(string label, IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
            return;

        Console.WriteLine($"{label}:");
        foreach (string path in paths)
            Console.WriteLine($"  {path}");
    }
}
