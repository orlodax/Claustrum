using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using Claustrum.Core.Backends;
using Claustrum.Core.Config;
using Claustrum.Core.Process;

namespace Claustrum.Cli;

// docs/PLAN.md §A5/§B6 `claustrum backends list|doctor [name] [--probe]`; doctor also prints the
// merged config with the winning layer per key (Config.Origins), per the builder brief item 3.
// Bare `doctor` is just `binary` (path/version/problems, docs/PLAN.md §B6's first bullet); `--probe`
// adds `auth`/`mcp`/`os` plus `probe` itself — a real paid 1-token round trip per installed backend
// (DoctorProbe, issue #12), skippable with CLAUSTRUM_SKIP_PROBE so the free checks stay free.
public static class BackendsCommands
{
    private static readonly TimeSpan ghVersionTimeout = TimeSpan.FromSeconds(10);

    public static Command Build()
    {
        Command list = new("list", "List registered backends.");
        list.SetAction(_ => List());

        Argument<string?> name = new("name") { Description = "Only check this backend.", Arity = ArgumentArity.ZeroOrOne };
        Option<bool> probe = new("--probe")
        {
            Description = "Also check auth presence, MCP registration, and OS/path mismatches. One minimal paid request per installed backend; set CLAUSTRUM_SKIP_PROBE=1 to skip the paid request.",
        };
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
        bool probeSkipped = DoctorProbe.IsSkipped(AppServices.Platform);

        // Said up front, before any money is spent: `--probe` is the one diagnostic that costs.
        if (probe)
        {
            Console.WriteLine(probeSkipped
                ? "probe: skipped for every backend (CLAUSTRUM_SKIP_PROBE set; no paid request made)"
                : $"probe: one minimal paid request per installed backend (cap ${DoctorProbe.ProbeBudgetUsd.ToString("0.00", CultureInfo.InvariantCulture)} each; role doctor-probe under ~/.claustrum/jobs)");
            Console.WriteLine();
        }

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

            // Printed after the problems and kept out of them on purpose: ProbeLineAsync below keys
            // the paid-probe skip on Problems alone (issue #17).
            foreach (string advisory in doctor.Advisories)
                Console.WriteLine($"  advisory: {advisory}");

            if (probe)
            {
                Console.WriteLine($"  auth:    {AuthStatusFor(backend.Name)}");
                Console.WriteLine($"  os:      {OsStatusFor(doctor.Path, cwd)}");
                Console.WriteLine($"  probe:   {await ProbeLineAsync(backend, doctor, config, probeSkipped)}");
            }
        }

        // Only for a bare `doctor`: `doctor <name>` is a question about that one backend, and `gh` is
        // not one of them.
        if (name is null)
            await PrintGhAsync();

        if (probe)
        {
            Console.WriteLine();
            Console.WriteLine("mcp:");
            Console.WriteLine($"  .mcp.json:        {DescribeMcpFile(Path.Combine(cwd, ".mcp.json"), "mcpServers")}");
            Console.WriteLine($"  .vscode/mcp.json: {DescribeMcpFile(Path.Combine(cwd, ".vscode", "mcp.json"), "servers")}");
            Console.WriteLine($"  .cursor/mcp.json: {DescribeMcpFile(Path.Combine(cwd, ".cursor", "mcp.json"), "mcpServers")}");

            // opencode registers MCP servers under its own top-level "mcp" key in the same file that
            // holds the rest of its settings, and accepts either extension. doctor only reads it:
            // `sync --only opencode` is what writes the claustrum entry there (OpencodeSync, #15).
            string opencodeConfig = OpencodeConfigPath(cwd);
            string opencodeLabel = Path.GetFileName(opencodeConfig) + ":";
            Console.WriteLine($"  {opencodeLabel,-18}{DescribeMcpFile(opencodeConfig, "mcp")}");
        }

        Console.WriteLine();
        Console.WriteLine("merged config:");
        PrintMergedConfig(config);

        return ExitCodes.Ok;
    }

    // docs/PLAN.md §D3: "`gh` presence is a `doctor` check" — `claustrum coordinate --issues` shells
    // out to it. Free, unlike `--probe`: locating a binary costs nothing, so a bare `doctor` always
    // prints it.
    private static async Task PrintGhAsync()
    {
        Doctor gh = await VersionProbe.RunAsync("gh", ["--version"], config: null, AppServices.Platform, CancellationToken.None, ghVersionTimeout);

        Console.WriteLine();
        Console.WriteLine("gh:");
        Console.WriteLine($"  found:   {gh.Found}");
        Console.WriteLine($"  path:    {gh.Path ?? "-"}");

        // `gh --version` answers on two lines ("gh version 2.x (date)" then a release URL);
        // VersionProbe keeps stdout whole, so the second line is dropped here rather than there.
        Console.WriteLine($"  version: {gh.Version?.Split('\n')[0].TrimEnd() ?? "-"}");

        if (!gh.Found)
            Console.WriteLine("  problem: gh not on PATH — `claustrum coordinate --issues` needs it");
    }

    // docs/PLAN.md §B6's `probe` bullet. Order matters: the owner's own skip flag first, then the
    // binary, then a blocker this backend's own doctor already named (the api backend's "neither
    // OPENROUTER_API_KEY nor ANTHROPIC_API_KEY is set") — only a backend past all three is worth
    // paying for. Exit code stays 0 either way: doctor reports, it does not fail.
    private static async Task<string> ProbeLineAsync(IBackend backend, Doctor doctor, Config config, bool probeSkipped)
    {
        if (probeSkipped)
            return "skipped (CLAUSTRUM_SKIP_PROBE set)";

        if (!doctor.Found)
            return "skipped (binary not found)";

        if (doctor.Problems.FirstOrDefault() is { } problem)
            return $"skipped ({problem})";

        ProbeOutcome outcome = await DoctorProbe.RunAsync(backend, config, AppServices.Runner, CancellationToken.None);
        return outcome.Line;
    }

    // opencode reads either extension; the .jsonc is only named when there is no .json, so the line
    // always describes the file that actually decides.
    private static string OpencodeConfigPath(string cwd)
    {
        string jsonPath = Path.Combine(cwd, "opencode.json");
        string jsoncPath = Path.Combine(cwd, "opencode.jsonc");
        return !File.Exists(jsonPath) && File.Exists(jsoncPath) ? jsoncPath : jsonPath;
    }

    // docs/PLAN.md §B6: "env key or login file present — value never printed". The env-var half is
    // this repo's own confirmed names: copilot's three from EnvAllowList.cs and the `copilot help
    // environment` documents in precedence order, opencode's three verbatim from the models.dev
    // `env` entries it reads (`opencode auth list` here: "OpenRouter  OPENROUTER_API_KEY
    // environment"). The login-file half is answered only where the store was actually looked at on
    // a real install — copilot's and opencode's, both 2026-09-22 — because guessing a path risks a
    // wrong "not set" for someone who is, in fact, logged in.
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

        if (relevantVars.Any(variable => !string.IsNullOrEmpty(AppServices.Platform.GetEnvironmentVariable(variable))))
            return "present (env var set — a login file may also work even if not)";

        return LoginFileStatusFor(backendName) ?? "not set (env var absent; a login file may still work — not checked)";
    }

    // Null means "this code does not know where that backend keeps a login", which is still true of
    // claude and cursor; the three answers below were each read off a real install. Nothing here
    // throws — doctor must still print its remaining lines.
    private static string? LoginFileStatusFor(string backendName) => backendName switch
    {
        // `api` spawns curl with the key straight from the environment: there is no store to look in.
        "api" => "not set (env var absent; this backend has no login file — it is a direct HTTPS call)",
        "opencode" => OpencodeLoginFileStatus(),
        "copilot" => CopilotLoginFileStatus(),
        _ => null,
    };

    // opencode 2.0.12 has no `auth.json`: the v1 path is empty on a real install, and 2.x keeps
    // credentials in a SQLite `opencode.db` this AOT binary will not take a dependency on to read.
    private static string OpencodeLoginFileStatus()
    {
        string dataHome = AppServices.Platform.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } relocated
            ? relocated
            : Path.Combine(AppServices.Platform.HomeDirectory, ".local", "share");
        string legacyAuth = Path.Combine(dataHome, "opencode", "auth.json");
        return File.Exists(legacyAuth)
            ? $"present (login file at {legacyAuth} — no env var set; contents not read)"
            : $"not set (env var absent; {legacyAuth} not present, and 2.x keeps credentials in opencode.db, which this does not read)";
    }

    // `~/.copilot/config.json` (relocated by COPILOT_HOME) keeps a non-empty `loggedInUsers` array on
    // a logged-in install — read from a real 1.0.87 one, which is what separates this from the
    // guessed paths the other backends deliberately do not check. Comments are legal in the file
    // ("This file is managed automatically"), hence CommentHandling.Skip.
    private static string CopilotLoginFileStatus()
    {
        string copilotHome = AppServices.Platform.GetEnvironmentVariable("COPILOT_HOME") is { Length: > 0 } relocated
            ? relocated
            : Path.Combine(AppServices.Platform.HomeDirectory, ".copilot");
        string path = Path.Combine(copilotHome, "config.json");
        if (!File.Exists(path))
            return $"not set (env var absent; {path} not present)";

        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
            // TryGetProperty throws InvalidOperationException on a root that is not an object, so the
            // kind is checked first: a stray array or string is unreadable, not a crash.
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return $"not set (env var absent; {path} present but unreadable)";

            bool loggedIn = document.RootElement.TryGetProperty("loggedInUsers", out JsonElement users)
                && users.ValueKind == JsonValueKind.Array
                && users.GetArrayLength() > 0;

            return loggedIn
                ? $"present (logged in, {path} — no env var set)"
                : $"not set (env var absent; {path} lists no logged-in user)";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return $"not set (env var absent; {path} present but unreadable)";
        }
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
