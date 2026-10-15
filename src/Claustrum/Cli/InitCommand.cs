using System.CommandLine;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Claustrum.Casts;
using Claustrum.Core.Config;
using Claustrum.Core.IO;
using Claustrum.Core.Platform;
using Claustrum.Roles.Sync;

namespace Claustrum.Cli;

// #122: `claustrum init` now handles model alias questions and writes them to the user config
// (~/.config/claustrum/config.json), never to claustrum.json. `init questions --json` prints one
// question per built-in alias the user config does not yet define; `init --answers <file>` merges
// the answers (and only missing ones) into the user config. Scaffold, sync, gitignore and AGENTS.md
// pointer remain, with updated messaging saying casts are per-developer and git-ignored.
public static class InitCommand
{
    // #122: casts/ is now per-developer and git-ignored, like worktrees/briefs/locks.
    private const string GitignoreBlock = """
        # Claustrum: per-developer casts, per-job worktrees, drafted briefs and concurrency locks.
        .claustrum/casts/
        .claustrum/worktrees/
        .claustrum/briefs/
        .claustrum/locks/
        """;

    // Per-line idempotency: the new block has casts/ first, then the three old ones.
    private static readonly string[] gitignoreEntries =
        [".claustrum/casts/", ".claustrum/worktrees/", ".claustrum/briefs/", ".claustrum/locks/"];

    private static readonly string[] scaffoldDirectories = ["casts", "briefs", "worktrees"];

    private const string AgentsMdMarker = "## Claustrum delegation";

    private const string AgentsMdBlock = """

        ## Claustrum delegation
        This repo uses Claustrum for harness-neutral agent delegation. The cast (`.claustrum/casts/`)
        is per-developer and git-ignored; the model aliases live in the user config
        (`~/.config/claustrum/config.json`). Run `claustrum cast questions --json` once to set up a
        cast, then `claustrum run <role> --brief-file <path>` (or the `delegate` MCP tool) to delegate
        a task. See https://github.com/orlodax/Claustrum.
        """;

    // The built-in model aliases from Config.BuiltInDefaults, with descriptions for the prompts.
    private static readonly Dictionary<string, string> builtInClasses = new(StringComparer.Ordinal)
    {
        ["frontier-reasoning"] = "architect's reasoning model (deepest thinking, highest cost)",
        ["frontier-coding"] = "builder's / reviewer's main model (strong coding, high cost)",
        ["standard-coding"] = "standard coding model (budget-friendly, good quality)",
        ["cheap-coding"] = "cheap model for routine tasks (lowest cost coding)",
        ["fast"] = "fast model for quick lookups and small tasks (lowest latency)",
    };

    public static Command Build()
    {
        Option<bool> all = new("--all") { Description = "Sync every supported harness, not just the ones this repo already uses." };
        Option<bool> questionsJson = new("--json") { Description = "Emit the alias questionnaire as one JSON document." };
        Option<string?> answersPath = new("--answers") { Description = "Path to a JSON file mapping alias -> 'backend:model-id'." };

        Command questions = new("questions", "List the alias questionnaire: one question per built-in class the user config does not define.") { questionsJson };
        questions.SetAction(parseResult => Questions(parseResult.GetValue(questionsJson)));

        Command command = new("init", "Scaffold Claustrum in the current repo: config aliases (user config), .claustrum/, and sync for the harnesses in use.")
        {
            all, answersPath, questions,
        };
        command.SetAction(parseResult => Execute(parseResult.GetValue(all), parseResult.GetValue(answersPath)));
        return command;
    }

    private static int Questions(bool jsonMode)
    {
        string cwd = Environment.CurrentDirectory;
        Config config = Config.Load(AppServices.Platform, cwd);
        HashSet<string> definedAliases = DefinedAliases(config);
        string userPath = config.UserConfigPath;

        List<CastQuestion> questions = [];
        foreach ((string alias, string defaultValue) in BuiltInDefaults())
        {
            // Only ask about aliases neither the user config nor the repo's claustrum.json defines.
            if (definedAliases.Contains(alias))
                continue;

            // Options: built-in default target first, then distinct targets from aliases already
            // defined in the user config or the repo's claustrum.json (not built-in ones).
            HashSet<string> options = [defaultValue];
            foreach (string defined in definedAliases)
                options.Add(config.Merged.Models?.GetValueOrDefault(defined) ?? defaultValue);

            string prompt = $"{alias}: {builtInClasses.GetValueOrDefault(alias, "model alias")}";
            questions.Add(new CastQuestion(Key: alias, Prompt: prompt, Options: [.. options], AllowNotNeeded: false, AllowFreeForm: true));
        }

        if (jsonMode)
        {
            InitQuestionsResult result = new([.. questions], userPath);
            Console.WriteLine(JsonSerializer.Serialize(result, InitQuestionsJsonContext.Default.InitQuestionsResult));
            return ExitCodes.Ok;
        }

        foreach (CastQuestion question in questions)
        {
            Console.WriteLine($"{question.Key}: {question.Prompt}");
            Console.WriteLine($"  options: {string.Join(", ", question.Options)}");
        }

        return ExitCodes.Ok;
    }

    private static int Execute(bool all, string? answersPath)
    {
        string cwd = Environment.CurrentDirectory;
        SyncRoot root = SyncRoot.Repo(cwd);

        // Before the first write, so a malformed claustrum.json or an alias cycle is exit 2 with nothing created.
        Config config = LoadConfig(cwd);

        // --- Alias questions / answers ---
        string userPath = config.UserConfigPath;
        int missingCount = CountMissingAliases(config);

        if (answersPath is { Length: > 0 })
        {
            string? error = WriteAnswers(userPath, answersPath, AppServices.Platform, config);
            if (error is not null)
            {
                Console.Error.WriteLine($"error: {error}");
                return ExitCodes.Usage;
            }

            // Reloaded before syncing: the agents are rendered with the aliases just written, and a partial
            // answers file is not an initialized repo (2026-10-15, review 2).
            config = LoadConfig(cwd);
            missingCount = CountMissingAliases(config);
        }
        else if (missingCount > 0 && !Console.IsInputRedirected)
        {
            // Ask on the terminal; an invalid answer is an error (exit 2), not asked again.
            string? writeError = AskAndWriteMissingAliasesInteractive(userPath, config);
            if (writeError is not null)
            {
                Console.Error.WriteLine($"error: {writeError}");
                return ExitCodes.Usage;
            }

            config = LoadConfig(cwd);
            missingCount = CountMissingAliases(config);
        }

        if (missingCount > 0)
        {
            Console.Error.WriteLine($"model aliases: {missingCount} missing from {userPath}; answer `claustrum init questions --json` and run `claustrum init --answers <file>`");
            return ExitCodes.Usage;
        }

        // --- Scaffold ---
        string? scaffoldEscape = Scaffold(root, cwd);
        if (scaffoldEscape is not null)
            Console.WriteLine($".claustrum/: {RefusedStatus(scaffoldEscape)}");

        // --- claustrum.json: never written (2026-10-15). An empty `{}` made every clone pass RequireInitialized
        // with no user config, defeating the gate; aliases are per-developer.
        string configPath = Path.Combine(cwd, "claustrum.json");
        string? configEscape = Refusal(root, configPath);
        string? configUnreadable = configEscape is null ? ConfigRefusal(configPath) : null;

        string configStatus;
        if (configEscape is not null)
            configStatus = RefusedStatus(configEscape);
        else if (configUnreadable is not null)
            configStatus = $"refused ({configUnreadable})";
        else if (File.Exists(configPath))
            configStatus = "already present, left unchanged";
        else
            configStatus = $"not written (aliases are per-developer, in {userPath})";
        Console.WriteLine($"claustrum.json: {configStatus}");

        // --- .gitignore ---
        string gitignorePath = Path.Combine(cwd, ".gitignore");
        string? gitignoreEscape = Refusal(root, gitignorePath);
        string? gitignoreUnreadable = null;
        bool updatedGitignore = gitignoreEscape is null && EnsureGitignoreEntries(gitignorePath, out gitignoreUnreadable);

        string gitignoreStatus;
        if (gitignoreEscape is not null)
            gitignoreStatus = RefusedStatus(gitignoreEscape);
        else if (gitignoreUnreadable is not null)
            gitignoreStatus = $"refused ({gitignoreUnreadable})";
        else if (updatedGitignore)
            gitignoreStatus = "updated";
        else
            gitignoreStatus = "already up to date";
        Console.WriteLine($".gitignore: {gitignoreStatus}");

        // -- Check tracked casts ---
        string? trackedCasts = CheckTrackedCasts(cwd);
        if (trackedCasts is not null)
            Console.Error.WriteLine(trackedCasts);

        // --- Sync harnesses ---
        string[] harnesses = all ? ["claude", "opencode", "copilot", "cursor"] : DetectHarnesses(cwd);
        bool refused = scaffoldEscape is not null || configEscape is not null || configUnreadable is not null || gitignoreEscape is not null || gitignoreUnreadable is not null;
        foreach (string harness in harnesses)
            refused |= SyncHarness(harness, cwd, config);

        // --- AGENTS.md pointer ---
        string agentsPath = Path.Combine(cwd, "AGENTS.md");
        string? agentsEscape = Refusal(root, agentsPath);
        string? agentsUnreadable = null;
        bool appendedAgentsMd = agentsEscape is null && AppendAgentsMdPointer(agentsPath, out agentsUnreadable);

        string agentsStatus;
        if (agentsEscape is not null)
            agentsStatus = RefusedStatus(agentsEscape);
        else if (agentsUnreadable is not null)
            agentsStatus = $"refused ({agentsUnreadable})";
        else if (appendedAgentsMd)
            agentsStatus = "pointer appended";
        else
            agentsStatus = "unchanged";
        Console.WriteLine($"AGENTS.md: {agentsStatus}");
        refused |= agentsEscape is not null || agentsUnreadable is not null;

        return refused ? ExitCodes.Usage : ExitCodes.Ok;
    }

    // Reads answers from a JSON file, validates them, and merges missing ones into the user config.
    // Returns null on success, or an error message.
    private static string? WriteAnswers(string userPath, string answersPath, IPlatform platform, Config config)
    {
        // Read the answers file
        Dictionary<string, string> answers;
        try
        {
            // PlainFile, not File.ReadAllText: a FIFO there would block forever.
            if (!PlainFile.TryReadAllText(answersPath, out string? json, out string? readRefusal))
                return $"'{answersPath}': {readRefusal}";

            answers = JsonSerializer.Deserialize(json, InitQuestionsJsonContext.Default.DictionaryStringString)
                ?? throw new CastException($"'{answersPath}' does not contain a JSON object");
        }
        catch (JsonException ex)
        {
            return $"'{answersPath}': {ex.Message}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"'{answersPath}': {ex.Message}";
        }

        // Validate every answer, defined already or not: backend part must be a registered backend name;
        // unknown alias key = error. (Defined-ness is the merge's business — MergeIntoUserConfig skips those.)
        foreach ((string alias, string value) in answers)
        {
            if (!BuiltInDefaults().ContainsKey(alias))
                return $"unknown alias '{alias}' in answers (valid keys: {string.Join(", ", BuiltInDefaults().Keys)})";

            // Validate the value: must be "backend:model-id"
            int colon = value.IndexOf(':');
            string backendPart = colon < 0 ? "claude" : value[..colon];
            if (!AppServices.Backends.All.Any(b => b.Name == backendPart))
                return $"alias '{alias}': unknown backend '{backendPart}' in '{value}'";

            // backend part is known — value is OK
        }

        // Merge only missing aliases into user config
        return MergeIntoUserConfig(userPath, answers, config, platform);
    }

    // Merges only the aliases that are not already defined in the user config layer.
    // Returns null on success, or an error message.
    private static string? MergeIntoUserConfig(string userPath, Dictionary<string, string> answers, Config config, IPlatform platform)
    {
        IReadOnlySet<string> userDefined = config.UserModelAliases;

        // Filter to only missing aliases
        Dictionary<string, string> toWrite = [];
        foreach ((string alias, string value) in answers)
        {
            if (!userDefined.Contains(alias))
                toWrite[alias] = value;
        }

        if (toWrite.Count == 0)
            return null; // nothing to write

        // Merged at the JSON level, not through ConfigDocument: a key Claustrum does not model ($schema, a note)
        // is the user's, and re-serializing the typed document dropped it (2026-10-15, review of #127).
        JsonObject root = [];
        if (platform.FileExists(userPath))
        {
            string? existingJson;
            try
            {
                existingJson = platform.ReadRegularFileText(userPath, out string? readRefusal);
                if (existingJson is null)
                    return $"'{userPath}': {readRefusal}";
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                return $"'{userPath}': cannot be read: {ex.Message}";
            }

            try
            {
                root = JsonNode.Parse(existingJson) as JsonObject ?? throw new CastException($"'{userPath}' does not contain a JSON object");
            }
            catch (JsonException ex)
            {
                return $"'{userPath}': {ex.Message}";
            }
            catch (CastException ex)
            {
                return ex.Message;
            }
        }

        JsonObject models;
        switch (root["models"])
        {
            case JsonObject existing:
                models = existing;
                break;
            case null:
                models = [];
                root["models"] = models;
                break;
            default:
                return $"'{userPath}': \"models\" is not a JSON object";
        }

        // The user file's own entry is never overwritten.
        foreach ((string alias, string value) in toWrite)
        {
            if (!models.ContainsKey(alias))
                models[alias] = value;
        }

        using MemoryStream buffer = new();
        using (Utf8JsonWriter writer = new(buffer, new JsonWriterOptions { Indented = true, NewLine = "\n" }))
            root.WriteTo(writer);

        string json = Encoding.UTF8.GetString(buffer.ToArray()) + "\n";

        // Through a symlinked config (dotfiles) the rename lands on the target, not on the link.
        if (Path.GetDirectoryName(userPath) is { Length: > 0 } userDir)
            Directory.CreateDirectory(userDir);

        FileInfo userFile = new(userPath);
        string target = userFile.LinkTarget is not null ? userFile.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? userPath : userPath;
        if (Path.GetDirectoryName(target) is { Length: > 0 } targetDir)
            Directory.CreateDirectory(targetDir);

        return WriteAtomically(target, json);
    }

    // A temp file beside the config, then a rename over it: a crash or a full disk leaves the old file whole, never a
    // truncated one. CreateNew (O_EXCL) refuses a planted link at the temp name.
    private static string? WriteAtomically(string path, string content)
    {
        string tmp = Path.Combine(Path.GetDirectoryName(path) ?? "", $".{Path.GetFileName(path)}.{Path.GetRandomFileName()}.claustrum-tmp");
        try
        {
            using (FileStream stream = new(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                if (!OperatingSystem.IsWindows() && File.Exists(path))
                    File.SetUnixFileMode(tmp, File.GetUnixFileMode(path));

                stream.Write(new UTF8Encoding(false).GetBytes(content));
            }

            File.Move(tmp, path, overwrite: true);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (File.Exists(tmp))
                File.Delete(tmp);

            return $"'{path}': {ex.Message}";
        }
    }

    // Ask missing aliases interactively on the terminal (synchronous, like cast new's AskUntilSupported).
    private static string? AskAndWriteMissingAliasesInteractive(string userPath, Config config)
    {
        HashSet<string> defined = DefinedAliases(config);

        Dictionary<string, string> answers = [];
        foreach ((string alias, string defaultValue) in BuiltInDefaults())
        {
            if (defined.Contains(alias))
                continue;

            Console.Error.WriteLine($"{alias}: {builtInClasses.GetValueOrDefault(alias, "model alias")}");
            Console.Write($"{alias} [{defaultValue}]> ");
            string? answer = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(answer))
                answer = defaultValue;

            // Validate: backend part must be a registered backend name
            int colon = answer.IndexOf(':');
            string backendPart = colon < 0 ? "claude" : answer[..colon];
            if (!AppServices.Backends.All.Any(b => b.Name == backendPart))
                return $"alias '{alias}': unknown backend '{backendPart}' in '{answer}'";

            answers[alias] = answer;
        }

        return MergeIntoUserConfig(userPath, answers, config, AppServices.Platform);
    }

    private static Dictionary<string, string> BuiltInDefaults() => new(StringComparer.Ordinal)
    {
        ["frontier-reasoning"] = "claude:opus",
        ["frontier-coding"] = "claude:opus",
        ["standard-coding"] = "claude:sonnet",
        ["cheap-coding"] = "claude:haiku",
        ["fast"] = "claude:haiku",
    };

    // Missing means defined by neither layer: a committed claustrum.json alias already wins at run time.
    private static HashSet<string> DefinedAliases(Config config) => [.. config.UserModelAliases, .. config.RepoModelAliases];

    private static int CountMissingAliases(Config config)
    {
        HashSet<string> defined = DefinedAliases(config);
        return BuiltInDefaults().Keys.Count(alias => !defined.Contains(alias));
    }

    // Creates the three directories, or none: null when they now exist, otherwise the real path of the first
    // one that leaves the repo (`.claustrum` itself linked out makes all three do so), with nothing created.
    private static string? Scaffold(SyncRoot root, string cwd)
    {
        string[] directories = [.. scaffoldDirectories.Select(name => Path.Combine(cwd, ".claustrum", name))];
        string? escapesTo = directories.Select(directory => Refusal(root, directory)).FirstOrDefault(real => real is not null);
        if (escapesTo is not null)
            return escapesTo;

        foreach (string directory in directories)
            Directory.CreateDirectory(directory);

        return null;
    }

    // The real path when `path` resolves outside the repo, else null. Checked on the full path, leaf
    // included, because File.WriteAllText and AppendAllText follow a leaf link, so the bytes land at its target.
    private static string? Refusal(SyncRoot root, string path) => root.Escapes(path) ? SyncRoot.Resolve(path) : null;

    private static string RefusedStatus(string escapesTo) => $"refused (resolves outside the repo → {escapesTo})";

    private static string[] DetectHarnesses(string cwd)
    {
        List<string> harnesses = ["claude"];
        if (Directory.Exists(Path.Combine(cwd, ".opencode"))
            || File.Exists(Path.Combine(cwd, "opencode.json"))
            || File.Exists(Path.Combine(cwd, "opencode.jsonc")))
            harnesses.Add("opencode");
        if (Directory.Exists(Path.Combine(cwd, ".github")))
            harnesses.Add("copilot");
        if (Directory.Exists(Path.Combine(cwd, ".cursor")))
            harnesses.Add("cursor");

        return [.. harnesses];
    }

    private static Config LoadConfig(string cwd)
    {
        Config config = Config.Load(AppServices.Platform, cwd);
        SyncSettings.ValidateModels(config, AppServices.RoleLibrary, null, cwd);
        return config;
    }

    // True when sync refused a target for resolving outside the repo (issue #95): init finishes, then exits 2.
    private static bool SyncHarness(string harness, string cwd, Config config)
    {
        SyncResult result = harness switch
        {
            "claude" => new ClaudeSync(AppServices.RoleLibrary, AppServices.RoleRenderer, AppServices.Platform.HomeDirectory).Sync(cwd, config: config),
            "opencode" => new OpencodeSync(AppServices.RoleLibrary, AppServices.RoleRenderer, AppServices.Platform.HomeDirectory).Sync(cwd, config: config),
            "copilot" => new CopilotSync(AppServices.RoleLibrary, AppServices.RoleRenderer, AppServices.Platform.HomeDirectory).Sync(cwd, config: config),
            "cursor" => new CursorSync(AppServices.RoleLibrary, AppServices.RoleRenderer, AppServices.Platform.HomeDirectory).Sync(cwd, config: config),
            _ => throw new ArgumentOutOfRangeException(nameof(harness), harness, "not a supported harness"),
        };

        IReadOnlyDictionary<string, string> refused = result.Refused ?? new Dictionary<string, string>();
        string refusedNote = refused.Count > 0 ? $", {refused.Count} refused (outside the repo, or a pipe, socket or device)" : "";
        Console.WriteLine($"{harness}: {result.Written.Count} written, {result.Skipped.Count} skipped, {result.Foreign.Count} foreign{refusedNote}");
        foreach (KeyValuePair<string, string> entry in refused)
            Console.WriteLine($"  {entry.Key} → {entry.Value}");

        return refused.Count > 0;
    }

    // An existing claustrum.json is only checked for being a regular file: a pipe or device is never opened.
    private static string? ConfigRefusal(string path)
    {
        if (!File.Exists(path))
            return null;

        try
        {
            using FileStream? existing = PlainFile.OpenRead(path, out string? refusal);
            return refusal;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            return $"cannot be read: {e.Message}";
        }
    }

    private static bool EnsureGitignoreEntries(string path, out string? unreadable)
    {
        unreadable = null;
        if (!File.Exists(path))
        {
            return PlainFile.TryWriteAllText(path, GitignoreBlock + "\n", FileMode.Append, out unreadable);
        }

        if (!PlainFile.TryReadAllText(path, out string? existing, out unreadable))
            return false;

        string[] missing = [.. gitignoreEntries.Where(entry => !existing.Contains(entry, StringComparison.Ordinal))];
        if (missing.Length == 0)
            return false;

        string block = missing.Length == gitignoreEntries.Length
            ? GitignoreBlock
            : "# Claustrum (added by `claustrum init`).\n" + string.Join('\n', missing);

        return PlainFile.TryWriteAllText(path, (existing.EndsWith('\n') ? "" : "\n") + "\n" + block + "\n", FileMode.Append, out unreadable);
    }

    private static bool AppendAgentsMdPointer(string path, out string? unreadable)
    {
        unreadable = null;
        if (!File.Exists(path))
            return false;

        if (!PlainFile.TryReadAllText(path, out string? existing, out unreadable))
            return false;

        if (existing.Contains(AgentsMdMarker, StringComparison.Ordinal))
            return false;

        return PlainFile.TryWriteAllText(path, (existing.EndsWith('\n') ? "" : "\n") + AgentsMdBlock + "\n", FileMode.Append, out unreadable);
    }

    // Check whether git tracks any files under .claustrum/casts/
    private static string? CheckTrackedCasts(string cwd)
    {
        try
        {
            string gitDir = Path.Combine(cwd, ".git");
            if (!Directory.Exists(gitDir) && !File.Exists(gitDir))
                return null;

            ProcessStartInfo psi = new("git")
            {
                WorkingDirectory = cwd,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("ls-files");
            psi.ArgumentList.Add(".claustrum/casts");

            using Process? process = Process.Start(psi);
            if (process is null)
                return null;

            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);

            if (process.ExitCode != 0)
                return null;

            string[] lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (lines.Length == 0)
                return null;

            return $".claustrum/casts/: {lines.Length} cast(s) still tracked; the new ignore rule does not untrack them, run `git rm --cached -r .claustrum/casts` if they are yours alone";
        }
        catch
        {
            return null;
        }
    }
}
