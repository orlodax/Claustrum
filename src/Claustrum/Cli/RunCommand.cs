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

    // Both framework defaults, looked up at runtime: they are localised resources, not constants.
    private static readonly string[] defaultCancelMessages = [new OperationCanceledException().Message, new TaskCanceledException().Message];

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
        Option<string?> branch = new("--branch")
        {
            Description = "Run isolated in a new worktree checked out on this existing branch, e.g. claustrum/<job> to continue a reviewed builder's work.",
        };
        Option<int?> timeout = new("--timeout") { Description = "Timeout in seconds." };
        Option<string?> resume = new("--resume") { Description = "Backend session id to resume." };
        Option<string[]> file = new("--file") { Description = "Attach a file (repeatable)." };
        Option<string[]> env = new("--env") { Description = "Extra env var K=V (repeatable)." };
        Option<bool> json = new("--json") { Description = "Emit exactly one RunResult JSON document on stdout." };
        Option<string?> stream = new("--stream")
        {
            Description = "Stream output to stderr: 'readable' (default) formats progress lines, 'raw' echoes the raw backend JSON.",
            Arity = ArgumentArity.ZeroOrOne,
        };
        stream.AcceptOnlyFromAmong("readable", "raw");
        Option<bool> raw = new("--raw") { Description = "Include the backend's raw response in JSON output." };

        Command command = new("run", "Delegate a task to a role.")
        {
            role, brief, briefFile, cwd, backend, model, effort, tier, permission,
            deny, budget, timeout, resume, file, env, cast, branch, json, stream, raw,
        };

        command.SetAction(async parseResult =>
        {
            // Presence, not value: a bare `--stream` parses to a null value and must still mean 'readable'.
            string? streamValue = parseResult.GetValue(stream);
            bool streamMode = parseResult.GetResult(stream) is not null;
            bool streamRaw = streamValue == "raw";
            await ExecuteAsync(
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
                parseResult.GetValue(branch),
                parseResult.GetValue(json),
                streamMode, streamRaw,
                parseResult.GetValue(raw));
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        string roleName, string? briefText, string? briefFilePath, string? cwdOption, string? tierFlag,
        ConfigOverrides overrides, string? resumeSession, string[] attachFiles, string[] envEntries,
        string? castName, string? branch, bool jsonMode, bool streamMode, bool streamRaw, bool rawMode)
    {
        string cwd = Path.GetFullPath(cwdOption ?? Environment.CurrentDirectory);

        // Before the handler: it swallows the first signal, and a stdin read blocked here listens on no token.
        string brief = ResolveBrief(briefText, briefFilePath);

        // The first Ctrl-C, SIGTERM or SIGHUP lets Runner kill the backend and write the `cancelled` receipt; a second Ctrl-C or SIGTERM exits.
        using CancelSignalHandler cancel = new();

        try
        {
            // docs/PLAN.md §D1: explicit --cast wins; otherwise a repo's default.json applies itself
            // without being asked. A cast's role entry only fills gaps --backend/--model/--tier left
            // open; its budget_usd (even null, meaning unlimited) is authoritative over
            // claustrum.json's default unless --budget was given (CastApplication.Resolve, shared
            // with MCP delegate/delegate_async).
            CastApplicationResult castResult = CastApplication.Resolve(cwd, roleName, castName, tierFlag, overrides);
            string tier = castResult.Tier;
            ConfigOverrides resolvedOverrides = castResult.Overrides;
            CastBudget? castBudget = castResult.CastBudget;
            int? maxParallel = castResult.MaxParallel;
            string? resolvedCastName = castResult.CastName;
            string[] castWarnings = castResult.Warnings;

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
                StreamRaw: streamRaw,
                DiffCapBytes: CliDiffCapBytes,
                CastBudget: castBudget,
                MaxParallel: maxParallel,
                CastName: resolvedCastName,
                OnStreamLine: streamMode ? SafeConsole.Error : null,
                Branch: branch is { Length: > 0 } ? branch : null,
                PreRunWarnings: castWarnings);

            // Prepared here, not inside RunAsync(request), so the warnings reach stderr before the backend starts
            // — in --json mode too, where PrintHuman never runs (F14).
            PreparedDelegation prepared = DelegateEngine.Prepare(request);
            string[] printedWarnings = prepared.Options.PreRunWarnings ?? [];
            foreach (string warning in printedWarnings)
                SafeConsole.Error($"warning: {warning}");

            RunResult result;
            int? refusedExit = null;
            try
            {
                result = await DelegateEngine.RunAsync(prepared, job: null, cancel.Token);
            }
            catch (MachineryRefusedException ex)
            {
                // #104: a usage refusal (exit 2), whose failed receipt is still the document --json readers parse.
                (result, refusedExit) = (ex.Receipt, ExitCodes.Usage);
            }

            // The receipt is on disk: a Windows console close need wait no longer.
            cancel.Settled();

            RunResult output = rawMode ? result : result with { Raw = null };

            int exitCode = refusedExit ?? ExitCodeFor(output.Status);
            if (jsonMode)
                SafeConsole.Out(JsonSerializer.Serialize(output, ClaustrumJsonContext.Default.RunResult));
            else
                PrintHuman(output, exitCode, printedWarnings);

            return exitCode;
        }
        // Everything else (ConfigException, RoleRenderException, CliUsageException,
        // BlindGateException, BackendNotFoundException, ...) is intentionally not caught here —
        // Program.cs's top-level ExceptionBoundary maps those uniformly for every verb, not just
        // `run` (review finding #1). OperationCanceledException stays local: it needs exit 130, which
        // the boundary's generic "anything else" branch does not know about.
        catch (OperationCanceledException ex)
        {
            cancel.Settled();
            return Cancelled(ex);
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

    // Since #94 (2026-10-13) a cancelled add's "left behind: …" message is the receipt's `error`, printed by
    // PrintHuman; this sees only cancels raised before a job exists (`coordinate`'s planning). A message
    // beyond the framework's default is printed, a plain Ctrl-C stays silent. Shared with `coordinate`.
    internal static int Cancelled(OperationCanceledException ex)
    {
        if (!defaultCancelMessages.Contains(ex.Message, StringComparer.Ordinal))
            SafeConsole.Error($"warning: {ex.Message}");
        return ExitCodes.Cancelled;
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

    private static void PrintHuman(RunResult result, int exitCode, string[] printedWarnings)
    {
        SafeConsole.Out($"status: {result.Status} (exit {exitCode})");

        // An isolated run's work is on its branch, not in the directory the reader is looking at (F3).
        if (result.Branch is { } branch)
            SafeConsole.Out($"branch: {branch}");
        if (result.Worktree is { } worktree)
            SafeConsole.Out($"worktree: {worktree}");
        if (result.Commit is { } commit)
            SafeConsole.Out($"commit: {commit}");

        if (result.ChangedFiles.Length > 0)
        {
            SafeConsole.Out("changed files:");
            foreach (ChangedFile file in result.ChangedFiles)
                SafeConsole.Out($"  {KindLetter(file.Kind)} {file.Path}");
        }

        if (result.FinalMessage.Length > 0)
        {
            SafeConsole.Out();
            SafeConsole.Out(result.FinalMessage);
        }

        // A "work left uncommitted" one says the branch is missing work; --json readers have warnings[].
        // The ones printed before the run are not printed again.
        foreach (string warning in result.Warnings.Where(warning => !printedWarnings.Contains(warning)))
            SafeConsole.Error($"warning: {warning}");

        if (result.Error is { Length: > 0 } error)
            SafeConsole.Error($"error: {error}");
    }

    private static char KindLetter(ChangeKind kind) => kind switch
    {
        ChangeKind.Added => 'A',
        ChangeKind.Modified => 'M',
        ChangeKind.Deleted => 'D',
        _ => '?',
    };
}
