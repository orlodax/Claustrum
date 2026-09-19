using System.CommandLine;
using System.Text.Json;
using Claustrum.Core.Backends;
using Claustrum.Core.Config;

namespace Claustrum.Cli;

// docs/PLAN.md §A5/§B6 `claustrum backends list|doctor [name] [--probe]`; doctor also prints the
// merged config with the winning layer per key (Config.Origins), per the builder brief item 3.
// Bare `doctor` is just `binary` (path/version/problems, docs/PLAN.md §B6's first bullet); `--probe`
// adds `auth`/`mcp`/`os` — the fourth bullet, `probe` itself (an actual paid 1-token round trip per
// backend), is deliberately not implemented yet (NOTES.md "doctor --probe").
public static class BackendsCommands
{
    public static Command Build()
    {
        Command list = new("list", "List registered backends.");
        list.SetAction(_ => List());

        Argument<string?> name = new("name") { Description = "Only check this backend.", Arity = ArgumentArity.ZeroOrOne };
        Option<bool> probe = new("--probe") { Description = "Also check auth presence, MCP registration, and OS/path mismatches." };
        Command doctor = new("doctor", "Check backend availability and print the merged config.") { name, probe };
        doctor.SetAction(async parseResult => await DoctorAsync(parseResult.GetValue(name), parseResult.GetValue(probe)));

        return new Command("backends", "Inspect registered backends.") { list, doctor };
    }

    private static int List()
    {
        foreach (IBackend backend in AppServices.Backends.All)
            Console.WriteLine(backend.Name);

        return ExitCodes.Ok;
    }

    private static async Task<int> DoctorAsync(string? name, bool probe)
    {
        IReadOnlyCollection<IBackend> targets = AppServices.Backends.All;
        if (name is not null)
        {
            if (!AppServices.Backends.TryGet(name, out IBackend? backend))
            {
                Console.Error.WriteLine($"unknown backend '{name}'");
                return ExitCodes.Usage;
            }

            targets = [backend];
        }

        string cwd = Environment.CurrentDirectory;
        Config config = Config.Load(AppServices.Platform, cwd);

        foreach (IBackend backend in targets)
        {
            BackendConfig? backendConfig = config.Merged.Backends?.GetValueOrDefault(backend.Name);
            Doctor doctor = await backend.DetectAsync(backendConfig, CancellationToken.None);
            Console.WriteLine($"{backend.Name}:");
            Console.WriteLine($"  found:   {doctor.Found}");
            Console.WriteLine($"  path:    {doctor.Path ?? "-"}");
            Console.WriteLine($"  version: {doctor.Version ?? "-"}");
            foreach (string problem in doctor.Problems)
                Console.WriteLine($"  problem: {problem}");

            if (probe)
            {
                Console.WriteLine($"  auth:    {AuthStatusFor(backend.Name)}");
                Console.WriteLine($"  os:      {OsStatusFor(doctor.Path, cwd)}");
            }
        }

        if (probe)
        {
            Console.WriteLine();
            Console.WriteLine("mcp:");
            Console.WriteLine($"  .mcp.json:        {DescribeMcpFile(Path.Combine(cwd, ".mcp.json"), "mcpServers")}");
            Console.WriteLine($"  .vscode/mcp.json: {DescribeMcpFile(Path.Combine(cwd, ".vscode", "mcp.json"), "servers")}");
        }

        Console.WriteLine();
        Console.WriteLine("merged config:");
        PrintMergedConfig(config);

        return ExitCodes.Ok;
    }

    // docs/PLAN.md §B6: "env key or login file present — value never printed". Only the env-var half
    // is checked: this repo's own confirmed variable names (EnvAllowList.cs, and copilot's own
    // documented GH_TOKEN/GITHUB_TOKEN precedence — NOTES.md "The copilot backend") are trustworthy;
    // a backend's actual login-file path was never independently confirmed for any of the four (only
    // opencode's and copilot's own CLI *behavior* were verified, not their credential storage), so
    // guessing one risks a wrong "not set" for someone who is, in fact, logged in.
    private static string AuthStatusFor(string backendName)
    {
        string[] relevantVars = backendName switch
        {
            "claude" => ["ANTHROPIC_API_KEY"],
            "opencode" => ["ANTHROPIC_API_KEY", "OPENROUTER_API_KEY", "OPENCODE_API_KEY"],
            "copilot" => ["COPILOT_GITHUB_TOKEN", "GH_TOKEN", "GITHUB_TOKEN"],
            "cursor" => ["CURSOR_API_KEY"],
            "api" => ["OPENROUTER_API_KEY", "ANTHROPIC_API_KEY"],
            _ => [],
        };

        if (relevantVars.Length == 0)
            return "unknown (no known env var for this backend)";

        bool present = relevantVars.Any(variable => !string.IsNullOrEmpty(AppServices.Platform.GetEnvironmentVariable(variable)));
        return present
            ? "present (env var set — a login file may also work even if not)"
            : "not set (env var absent; a login file may still work — not checked)";
    }

    // docs/PLAN.md §A4/§B6: "Claustrum spawns backends on the OS it runs on ... doctor warns when a
    // backend resolved from WSL is a /mnt/c/... Windows exe." Generalized to any path/cwd mismatch
    // across the /mnt/ boundary, not just specifically-Windows-looking binaries under it.
    private static string OsStatusFor(string? binaryPath, string cwd)
    {
        if (binaryPath is null)
            return "n/a (binary not found)";

        bool pathUnderMnt = binaryPath.StartsWith("/mnt/", StringComparison.OrdinalIgnoreCase);
        bool cwdUnderMnt = cwd.StartsWith("/mnt/", StringComparison.OrdinalIgnoreCase);
        return pathUnderMnt == cwdUnderMnt
            ? "ok"
            : $"warning: binary at '{binaryPath}' and cwd '{cwd}' mix a Windows mount and a native path";
    }

    // docs/PLAN.md §B6: "mcp (which config files register claustrum)". Tolerant of JSONC (VS Code
    // documents .vscode/*.json as comments+trailing-commas-allowed, same as McpConfigSync's own
    // ParseExisting), but only reads — this command never writes, that is `sync`'s job.
    private static string DescribeMcpFile(string path, string sectionKey)
    {
        if (!File.Exists(path))
            return "not present";

        JsonDocumentOptions options = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path), options);
            bool registersClaustrum = document.RootElement.TryGetProperty(sectionKey, out JsonElement section)
                && section.ValueKind == JsonValueKind.Object
                && section.TryGetProperty("claustrum", out _);

            return registersClaustrum ? "registers claustrum" : "present, does not register claustrum";
        }
        catch (JsonException)
        {
            return "present, could not be parsed";
        }
    }

    private static void PrintMergedConfig(Config config)
    {
        foreach (KeyValuePair<string, string> entry in config.Merged.Models ?? [])
            PrintLine(config, $"models.{entry.Key}", entry.Value);

        foreach ((string roleName, RoleSettings settings) in config.Merged.Roles ?? [])
        {
            if (settings.Model is not null)
                PrintLine(config, $"roles.{roleName}.model", settings.Model);
            if (settings.Effort is not null)
                PrintLine(config, $"roles.{roleName}.effort", settings.Effort);
            if (settings.Permission is not null)
                PrintLine(config, $"roles.{roleName}.permission", settings.Permission);
            if (settings.Deny is { Length: > 0 })
                PrintLine(config, $"roles.{roleName}.deny", string.Join(", ", settings.Deny));
        }

        foreach ((string backendName, BackendConfig backendConfig) in config.Merged.Backends ?? [])
        {
            if (backendConfig.Path is not null)
                PrintLine(config, $"backends.{backendName}.path", backendConfig.Path);
            if (backendConfig.Injection is not null)
                PrintLine(config, $"backends.{backendName}.injection", backendConfig.Injection);
        }

        if (config.Merged.Defaults is { } defaults)
        {
            if (defaults.TimeoutSeconds is { } timeoutSeconds)
                PrintLine(config, "defaults.timeout_seconds", timeoutSeconds.ToString());
            if (defaults.BudgetUsd is { } budgetUsd)
                PrintLine(config, "defaults.budget_usd", budgetUsd.ToString());
            if (defaults.EnvPassthrough is { } envPassthrough)
                PrintLine(config, "defaults.env_passthrough", envPassthrough);
        }

        if (config.Merged.Jobs?.KeepLast is { } keepLast)
            PrintLine(config, "jobs.keep_last", keepLast.ToString());
    }

    private static void PrintLine(Config config, string key, string value)
    {
        string layer = config.Origins.TryGetValue(key, out ConfigLayer found) ? found.ToString() : nameof(ConfigLayer.Builtin);
        Console.WriteLine($"  {key} = {value}  [{layer}]");
    }
}
