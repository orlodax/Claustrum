using System.CommandLine;
using System.Text.Json;
using Claustrum.Casts;
using Claustrum.Core.Config;
using Claustrum.Core.Json;
using Claustrum.Core.Model;
using Claustrum.Delegation;

namespace Claustrum.Cli;

// docs/PLAN.md §A5 "run" verb. Pipeline: load config -> RoleRenderer.Render(role, tier,
// backend-or-default, cwd) -> Config.Resolve with flag overrides -> Runner.RunAsync. Exit codes are
// the fixed §A5 table (ExitCodes), not System.CommandLine's own.
public static class RunCommand
{
    // §A2: 200 KB on the CLI door, 64 KB on MCP. Internal because `coordinate` is the same door.
    internal const int CliDiffCapBytes = 200 * 1024;

    public static Command Build()
    {
        Argument<string> role = new("role") { Description = "Role to run (see `claustrum roles list`)." };
        Option<string?> brief = new("--brief") { Description = "Brief text." };
        Option<string?> briefFile = new("--brief-file") { Description = "Path to a brief file, or '-' for stdin." };
        Option<string?> cwd = new("--cwd") { Description = "Working directory (default: current directory)." };
        Option<string?> backend = new("--backend") { Description = "Backend override (default: 'claude')." };
        Option<string?> model = new("--model") { Description = "Model override." };
        Option<string?> effort = new("--effort") { Description = "Effort override." };
        Option<string?> tier = new("--tier") { Description = "Role tier (default: the cast's, else 'high')." };
        tier.AcceptOnlyFromAmong("high", "xhigh", "max");
        Option<string?> permission = new("--permission") { Description = "Permission level override." };
        permission.AcceptOnlyFromAmong("readonly", "shell", "edit", "edit+shell", "full");
        Option<string[]> deny = new("--deny") { Description = "Extra deny pattern (repeatable)." };
        Option<decimal?> budget = new("--budget") { Description = "Budget cap in USD." };
        Option<string?> cast = new("--cast") { Description = "Cast name (default: .claustrum/casts/default.json if present)." };
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
            deny, budget, timeout, resume, file, env, cast, json, stream, raw,
        };

        command.SetAction(async parseResult => await ExecuteAsync(
            parseResult.GetRequiredValue(role),
            parseResult.GetValue(brief),
            parseResult.GetValue(briefFile),
            parseResult.GetValue(cwd),
            parseResult.GetValue(tier),
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
            parseResult.GetValue(cast),
            parseResult.GetValue(json),
            parseResult.GetValue(stream),
            parseResult.GetValue(raw)));

        return command;
    }

    private static async Task<int> ExecuteAsync(
        string roleName, string? briefText, string? briefFilePath, string? cwdOption, string? tierFlag,
        ConfigOverrides overrides, string? resumeSession, string[] attachFiles, string[] envEntries,
        string? castName, bool jsonMode, bool streamMode, bool rawMode)
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

            // docs/PLAN.md §D1: explicit --cast wins; otherwise a repo's default.json applies itself
            // without being asked. A cast's role entry only fills gaps --backend/--model/--tier left
            // open; its budget_usd (even null, meaning unlimited) is authoritative over
            // claustrum.json's default unless --budget was given (CastApplication.Resolve, shared
            // with MCP delegate/delegate_async).
            (string tier, ConfigOverrides resolvedOverrides, CastBudget? castBudget, int? maxParallel, string? resolvedCastName) = CastApplication.Resolve(cwd, roleName, castName, tierFlag, overrides);

            DelegateRequest request = new(
                Role: roleName,
                Brief: brief,
                Cwd: cwd,
                Tier: tier,
                Overrides: resolvedOverrides,
                ResumeSession: resumeSession,
                AttachFiles: attachFiles,
                Env: ParseEnv(envEntries),
                Stream: streamMode,
                DiffCapBytes: CliDiffCapBytes,
                CastBudget: castBudget,
                MaxParallel: maxParallel,
                CastName: resolvedCastName,
                OnStreamLine: streamMode ? Console.Error.WriteLine : null);

            RunResult result = await DelegateEngine.RunAsync(request, cts.Token);
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

    // §B3's three ways in live in BriefSource, shared with `coordinate`. `run` is the door where a
    // brief is mandatory: no source, an empty file or an empty stdin all land on the same refusal,
    // before Config/RoleRenderer/Runner touch anything (review finding #1).
    private static string ResolveBrief(string? briefText, string? briefFilePath) =>
        BriefSource.TryResolve(briefText, briefFilePath, allowBareStdin: true)
            ?? throw new CliUsageException("no brief given");

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

    /// <summary>docs/PLAN.md §A5's fixed status → exit code table, shared with `coordinate`.</summary>
    public static int ExitCodeFor(RunStatus status) => status switch
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
