using System.CommandLine;
using Claustrum.Roles.Sync;

namespace Claustrum.Cli;

// docs/PLAN.md §A5/§B4 `claustrum sync [--only claude] [--roles a,b] [--global] [--force]`. Only the
// `claude` harness is implemented in M1; any other `--only` value is a usage error (exit 2).
public static class SyncCommand
{
    public static Command Build()
    {
        Option<string?> only = new("--only") { Description = "Comma-separated harnesses to sync (only 'claude' exists)." };
        Option<string?> roles = new("--roles") { Description = "Comma-separated role names (default: every role)." };
        Option<bool> global = new("--global") { Description = "Write to the user-global agent directories instead of the repo." };
        Option<bool> force = new("--force") { Description = "Overwrite hand-edited files that lack the claustrum:generated marker." };

        Command command = new("sync", "Render the role library into a coding-harness's native files.") { only, roles, global, force };
        command.SetAction(parseResult => Execute(
            parseResult.GetValue(only),
            parseResult.GetValue(roles),
            parseResult.GetValue(global),
            parseResult.GetValue(force)));

        return command;
    }

    private static int Execute(string? only, string? rolesOption, bool global, bool force)
    {
        string[] harnesses = only is { Length: > 0 } ? only.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries) : ["claude"];
        string[] unsupported = [.. harnesses.Where(h => h != "claude")];
        if (unsupported.Length > 0)
        {
            Console.Error.WriteLine($"not yet supported: {string.Join(", ", unsupported)} (only 'claude' exists in M1)");
            return ExitCodes.Usage;
        }

        string[]? roleFilter = rolesOption is { Length: > 0 }
            ? rolesOption.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            : null;

        ClaudeSync sync = new(CliServices.RoleLibrary, CliServices.RoleRenderer);
        SyncResult result = sync.Sync(Environment.CurrentDirectory, roleFilter, global, force);

        PrintPaths("written", result.Written);
        PrintPaths("skipped (already up to date)", result.Skipped);
        PrintPaths("foreign (hand-edited; use --force to adopt)", result.Foreign);

        return ExitCodes.Ok;
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
