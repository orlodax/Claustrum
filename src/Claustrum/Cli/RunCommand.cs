using System.CommandLine;
using System.Text.Json;
using Claustrum.Core;
using Claustrum.Core.Config;
using Claustrum.Core.Json;
using Claustrum.Core.Model;

namespace Claustrum.Cli;

// docs/PLAN.md §A5 "run" verb. Pipeline: load config -> RoleRenderer.Render(role, tier,
// backend-or-default, cwd) -> Config.Resolve with flag overrides -> Runner.RunAsync. Exit codes are
// the fixed §A5 table (ExitCodes), not System.CommandLine's own.
public static class RunCommand
{
    private const int CliDiffCapBytes = 200 * 1024; // §A2: 200 KB on the CLI door, 64 KB on MCP.
    private const int DefaultTimeoutSeconds = 1800;

    public static Command Build()
    {
        Argument<string> role = new("role") { Description = "Role to run (see `claustrum roles list`)." };
        Option<string?> brief = new("--brief") { Description = "Brief text." };
        Option<string?> briefFile = new("--brief-file") { Description = "Path to a brief file, or '-' for stdin." };
        Option<string?> cwd = new("--cwd") { Description = "Working directory (default: current directory)." };
        Option<string?> backend = new("--backend") { Description = "Backend override (default: 'claude')." };
        Option<string?> model = new("--model") { Description = "Model override." };
        Option<string?> effort = new("--effort") { Description = "Effort override." };
        Option<string> tier = new("--tier") { Description = "Role tier.", DefaultValueFactory = _ => "high" };
        tier.AcceptOnlyFromAmong("high", "xhigh", "max");
        Option<string?> permission = new("--permission") { Description = "Permission level override." };
        permission.AcceptOnlyFromAmong("readonly", "edit", "edit+shell", "full");
        Option<string[]> deny = new("--deny") { Description = "Extra deny pattern (repeatable)." };
        Option<decimal?> budget = new("--budget") { Description = "Budget cap in USD." };
        Option<int?> timeout = new("--timeout") { Description = "Timeout in seconds." };
        Option<string?> resume = new("--resume") { Description = "Backend session id to resume." };
        Option<string[]> file = new("--file") { Description = "Attach a file (repeatable)." };
        Option<string[]> env = new("--env") { Description = "Extra env var K=V (repeatable)." };
        Option<bool> json = new("--json") { Description = "Emit exactly one RunResult JSON document on stdout." };
        Option<bool> stream = new("--stream") { Description = "Echo backend output to stderr as it runs." };
        Option<bool> raw = new("--raw") { Description = "Include the backend's raw response in JSON output." };

        Command command = new("run", "Delegate a task to a role.")
        {
            role, brief, briefFile, cwd, backend, model, effort, tier, permission,
            deny, budget, timeout, resume, file, env, json, stream, raw,
        };

        command.SetAction(async parseResult => await ExecuteAsync(
            parseResult.GetRequiredValue(role),
            parseResult.GetValue(brief),
            parseResult.GetValue(briefFile),
            parseResult.GetValue(cwd),
            parseResult.GetValue(tier) ?? "high",
            new ConfigOverrides(
                Backend: parseResult.GetValue(backend),
                Model: parseResult.GetValue(model),
                Effort: parseResult.GetValue(effort),
                Permission: parseResult.GetValue(permission),
                Deny: parseResult.GetValue(deny),
                BudgetUsd: parseResult.GetValue(budget),
                TimeoutSeconds: parseResult.GetValue(timeout)),
            parseResult.GetValue(resume),
            parseResult.GetValue(file) ?? [],
            parseResult.GetValue(env) ?? [],
            parseResult.GetValue(json),
            parseResult.GetValue(stream),
            parseResult.GetValue(raw)));

        return command;
    }

    private static async Task<int> ExecuteAsync(
        string roleName, string? briefText, string? briefFilePath, string? cwdOption, string tierValue,
        ConfigOverrides overrides, string? resumeSession, string[] attachFiles, string[] envEntries,
        bool jsonMode, bool streamMode, bool rawMode)
    {
        string cwd = Path.GetFullPath(cwdOption ?? Environment.CurrentDirectory);

        using CancellationTokenSource cts = new();

        // Let Runner finish the process kill and report Cancelled, not an abrupt process exit.
        void OnCancel(object? sender, ConsoleCancelEventArgs e)
        {
            e.Cancel = true;
            cts.Cancel();
        }

        Console.CancelKeyPress += OnCancel;

        try
        {
            string brief = ResolveBrief(briefText, briefFilePath);

            // Config first, then the harness the role's tier model resolves to (--backend still wins),
            // then Render — Render must already know the harness it will run on (review finding #6),
            // not the "claude" placeholder this used to render against regardless of the real target.
            Config config = Config.Load(CliServices.Platform, cwd);
            string tierModelClass = CliServices.RoleRenderer.TierModelClass(roleName, tierValue, cwd);
            string harness = config.ResolveBackend(roleName, tierModelClass, overrides);

            RenderedRole rendered = CliServices.RoleRenderer.Render(roleName, tierValue, harness, cwd);
            ResolvedRole resolved = config.Resolve(rendered, overrides);

            decimal? budgetUsd = overrides.BudgetUsd ?? config.Merged.Defaults?.BudgetUsd;
            int timeoutSeconds = overrides.TimeoutSeconds ?? config.Merged.Defaults?.TimeoutSeconds ?? DefaultTimeoutSeconds;
            PermissionPolicy? requestPermission = overrides.Permission is { } permissionValue
                ? new PermissionPolicy(RequirePermissionLevel(permissionValue), overrides.Deny ?? [])
                : null;

            RunRequest request = new(
                Role: roleName,
                Brief: brief,
                BriefFile: null,
                Cwd: cwd,
                Backend: overrides.Backend,
                Model: overrides.Model,
                Effort: overrides.Effort,
                Permission: requestPermission,
                BudgetUsd: budgetUsd,
                Timeout: TimeSpan.FromSeconds(timeoutSeconds),
                ResumeSession: resumeSession,
                AttachFiles: attachFiles,
                Env: ParseEnv(envEntries),
                Stream: streamMode);

            BackendConfig? backendConfig = null;
            config.Merged.Backends?.TryGetValue(resolved.Backend, out backendConfig);
            RunOptions options = new(
                DiffByteCapBytes: CliDiffCapBytes,
                BackendConfig: backendConfig,
                EnvPassthroughAll: config.Merged.Defaults?.EnvPassthrough == "all",
                OnStreamLine: streamMode ? line => Console.Error.WriteLine(line) : null);

            RunResult result = await CliServices.Runner.RunAsync(request, resolved, options, cts.Token);
            RunResult output = rawMode ? result : result with { Raw = null };

            if (jsonMode)
                Console.WriteLine(JsonSerializer.Serialize(output, ClaustrumJsonContext.Default.RunResult));
            else
                PrintHuman(output);

            return ExitCodeFor(output.Status);
        }
        // Everything else (ConfigException, RoleRenderException, CliUsageException,
        // BlindGateException, BackendNotFoundException, ...) is intentionally not caught here —
        // Program.cs's top-level ExceptionBoundary maps those uniformly for every verb, not just
        // `run` (review finding #1). OperationCanceledException stays local: it needs exit 130, which
        // the boundary's generic "anything else" branch does not know about.
        catch (OperationCanceledException)
        {
            return ExitCodes.Cancelled;
        }
        finally
        {
            Console.CancelKeyPress -= OnCancel;
        }
    }

    // §B3 lists three ways in: `--brief`, `--brief-file <path|->`, or bare stdin. Runner.ResolveBrief
    // only understands Brief/BriefFile-as-a-real-path, so the CLI reads the text itself and always
    // hands Runner a resolved Brief string.
    private static string ResolveBrief(string? briefText, string? briefFilePath)
    {
        if (briefText is { Length: > 0 } && briefFilePath is { Length: > 0 })
            throw new CliUsageException("use either --brief or --brief-file, not both");

        if (briefText is { Length: > 0 })
            return briefText;

        // A brief-file path or redirected stdin that reads back empty falls through to the same "no
        // brief given" as no source at all — checked here, before Config/RoleRenderer/Runner touch
        // anything, so an empty brief never reaches a spawned process (review finding #1).
        string brief = briefFilePath is { Length: > 0 }
            ? briefFilePath == "-" ? Console.In.ReadToEnd() : File.ReadAllText(briefFilePath)
            : Console.IsInputRedirected ? Console.In.ReadToEnd() : "";

        return brief is { Length: > 0 }
            ? brief
            : throw new CliUsageException("no brief given");
    }

    private static Dictionary<string, string> ParseEnv(string[] entries)
    {
        Dictionary<string, string> env = [];
        foreach (string entry in entries)
        {
            int separator = entry.IndexOf('=');
            if (separator < 0)
                throw new CliUsageException($"--env expects K=V, got '{entry}'");
            env[entry[..separator]] = entry[(separator + 1)..];
        }

        return env;
    }

    // AcceptOnlyFromAmong on the --permission option already rejects anything else during parsing;
    // this only turns the validated string back into the enum, so failure here is unreachable.
    private static PermissionLevel RequirePermissionLevel(string value) =>
        PermissionLevelParser.TryParse(value, out PermissionLevel level)
            ? level
            : throw new InvalidOperationException($"unreachable: '--permission' accepted invalid value '{value}'");

    private static int ExitCodeFor(RunStatus status) => status switch
    {
        RunStatus.Success => ExitCodes.Ok,
        RunStatus.Failed => ExitCodes.BackendFailure,
        RunStatus.Timeout => ExitCodes.Timeout,
        RunStatus.Cancelled => ExitCodes.Cancelled,
        RunStatus.BackendMissing => ExitCodes.BackendMissing,
        RunStatus.BudgetExceeded => ExitCodes.Budget,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };

    private static void PrintHuman(RunResult result)
    {
        Console.WriteLine($"status: {result.Status} (exit {ExitCodeFor(result.Status)})");

        if (result.ChangedFiles.Length > 0)
        {
            Console.WriteLine("changed files:");
            foreach (ChangedFile file in result.ChangedFiles)
                Console.WriteLine($"  {KindLetter(file.Kind)} {file.Path}");
        }

        if (result.FinalMessage.Length > 0)
        {
            Console.WriteLine();
            Console.WriteLine(result.FinalMessage);
        }

        if (result.Error is { Length: > 0 } error)
            Console.Error.WriteLine($"error: {error}");
    }

    private static char KindLetter(ChangeKind kind) => kind switch
    {
        ChangeKind.Added => 'A',
        ChangeKind.Modified => 'M',
        ChangeKind.Deleted => 'D',
        _ => '?',
    };
}
