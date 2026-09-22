using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using Claustrum.Coordination;
using Claustrum.Core.Config;
using Claustrum.Core.Jobs;
using Claustrum.Core.Json;
using Claustrum.Core.Model;
using Claustrum.Delegation;

namespace Claustrum.Cli;

// docs/PLAN.md §D3/§D5 `claustrum coordinate --cast <name> [--issues …|--brief-file …]`: the
// spawned architect. It is `run architect` with three things added — the cast in the system body,
// the job id exported as the children's budget tree, and `gh` issues folded into the brief — so the
// verb is a thin shell over CoordinateEngine.PlanAsync + the same DelegateEngine `run` uses.
public static class CoordinateCommand
{
    public static Command Build()
    {
        Option<string?> cast = new("--cast") { Description = "Cast name (default: .claustrum/casts/default.json)." };
        Option<string?> issues = new("--issues") { Description = "Comma-separated GitHub issue numbers to import as the task, e.g. 12,13." };
        Option<string?> brief = new("--brief") { Description = "Task text, instead of --issues." };
        Option<string?> briefFile = new("--brief-file") { Description = "Path to a task file, or '-' for stdin." };
        Option<string?> cwd = new("--cwd") { Description = "Working directory (default: current directory)." };
        Option<string?> tier = new("--tier") { Description = "Architect tier (default: the cast's, else 'high')." };
        tier.AcceptOnlyFromAmong("high", "xhigh", "max");
        Option<string?> model = new("--model") { Description = "Architect model override." };
        Option<decimal?> budget = new("--budget") { Description = "Budget cap in USD for the architect's own run." };
        Option<int?> timeout = new("--timeout") { Description = "Timeout in seconds." };
        Option<bool> json = new("--json") { Description = "Emit exactly one RunResult JSON document on stdout." };
        Option<bool> stream = new("--stream") { Description = "Echo the architect's output to stderr as it runs." };
        Option<bool> raw = new("--raw") { Description = "Include the backend's raw response in JSON output." };

        Command command = new("coordinate", "Run the cast's architect headlessly over issues or a brief (docs/PLAN.md §D3).")
        {
            cast, issues, brief, briefFile, cwd, tier, model, budget, timeout, json, stream, raw,
        };

        command.SetAction(async parseResult => await ExecuteAsync(
            parseResult.GetValue(cast),
            parseResult.GetValue(issues),
            parseResult.GetValue(brief),
            parseResult.GetValue(briefFile),
            parseResult.GetValue(cwd),
            parseResult.GetValue(tier),
            new ConfigOverrides(
                Model: parseResult.GetValue(model),
                BudgetUsd: parseResult.GetValue(budget),
                TimeoutSeconds: parseResult.GetValue(timeout)),
            parseResult.GetValue(json),
            parseResult.GetValue(stream),
            parseResult.GetValue(raw)));

        return command;
    }

    private static async Task<int> ExecuteAsync(
        string? castName, string? issuesCsv, string? briefText, string? briefFilePath, string? cwdOption,
        string? tierFlag, ConfigOverrides overrides, bool jsonMode, bool streamMode, bool rawMode)
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
            CoordinateRequest request = new(
                Cwd: cwd,
                CastName: castName,
                Issues: ParseIssues(issuesCsv),
                // No bare stdin here: `--issues` is the other way to state the task, and a stdin
                // nobody closes would hang the verb (BriefSource). `--brief-file -` still opts in.
                Brief: BriefSource.TryResolve(briefText, briefFilePath, allowBareStdin: false),
                TierFlag: tierFlag,
                Overrides: overrides,
                Stream: streamMode,
                DiffCapBytes: RunCommand.CliDiffCapBytes,
                OnStreamLine: streamMode ? Console.Error.WriteLine : null);

            // Everything that can be refused happens before the job directory exists — bad flags, a
            // missing cast, a `gh` that failed — because JobDirectory.Create also prunes the job
            // store, and a job minted for a run that never starts stays `pending` forever.
            CoordinatePlan plan = await CoordinateEngine.PlanAsync(request, new GhIssueSource(AppServices.Platform), cts.Token);

            // The job exists before the request does: its id is the budget tree every child the
            // architect spawns will join (docs/PLAN.md §D3), and the appendix prints it.
            JobPaths job = JobDirectory.Create(AppServices.Platform);

            RunResult result = await DelegateEngine.RunAsync(plan.ToDelegateRequest(job), job, cts.Token);
            RunResult output = rawMode ? result : result with { Raw = null };

            // Only a capped cast has a ledger at all (DelegateEngine builds no JobTreeBudget without
            // one), so this decides between two different true sentences, not a number and its zero.
            bool capped = plan.Cast.BudgetUsd is not null;

            if (jsonMode)
            {
                Console.WriteLine(JsonSerializer.Serialize(output, ClaustrumJsonContext.Default.RunResult));

                // stdout stays exactly one RunResult document, so the tree's numbers go to stderr —
                // they are the other half of what this run cost (PrintHumanAsync).
                await WriteTreeCostAsync(job, Console.Error, capped);
            }
            else
                await PrintHumanAsync(output, job, capped);

            return RunCommand.ExitCodeFor(output.Status);
        }
        catch (OperationCanceledException)
        {
            return ExitCodes.Cancelled;
        }
        finally
        {
            Console.CancelKeyPress -= OnCancel;
        }
    }

    private static int[] ParseIssues(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv))
            return [];

        List<int> numbers = [];
        foreach (string entry in csv.Split(','))
        {
            if (!int.TryParse(entry.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int number) || number <= 0)
                throw new CliUsageException($"--issues expects comma-separated issue numbers, got '{entry.Trim()}'");

            numbers.Add(number);
        }

        return [.. numbers];
    }

    private static async Task PrintHumanAsync(RunResult result, JobPaths job, bool capped)
    {
        Console.WriteLine($"status: {result.Status} (exit {RunCommand.ExitCodeFor(result.Status)})");
        Console.WriteLine($"job:    {job.Id}");
        Console.WriteLine($"tree:   claustrum jobs budget {job.Id}");
        Console.WriteLine($"logs:   claustrum jobs logs {job.Id}");

        // Two amounts, always both: the architect is not a member of its own tree, so its own cost
        // and its children's ledger totals are separate spends of the same cast budget, and printing
        // both is what keeps the doubling visible (NOTES.md "coordinate: a spawned architect…").
        Console.WriteLine($"architect cost {(result.CostUsd is { } cost ? BudgetLedger.Dollars(cost) : "-")}");
        await WriteTreeCostAsync(job, Console.Out, capped);

        if (result.FinalMessage.Length > 0)
        {
            Console.WriteLine();
            Console.WriteLine(result.FinalMessage);
        }

        if (result.Error is { Length: > 0 } error)
            Console.Error.WriteLine($"error: {error}");
    }

    // The children's spend. The ledger is a file other processes hold a lock on, so a run that has
    // already finished must keep its own exit code whatever this read does (the same three
    // exceptions DelegateEngine.TryAdmitAsync swallows).
    // ⚠ An uncapped cast writes no ledger entries at all, so `$0.00` would be a measurement nobody
    // took — it says `unlimited` instead, and `jobs budget <id>` is empty for the same reason.
    private static async Task WriteTreeCostAsync(JobPaths job, TextWriter writer, bool capped)
    {
        if (!capped)
        {
            writer.WriteLine("tree: unlimited (children not accounted)");
            return;
        }

        try
        {
            decimal spent = 0m;
            decimal reserved = 0m;

            // No ledger directory means no child ever ran — $0.00, and no lock taken to learn it.
            if (Directory.Exists(BudgetLedger.DirectoryFor(AppServices.Platform, job.Id)))
            {
                BudgetLedgerState ledger = await BudgetLedger.ReadAsync(AppServices.Platform, job.Id);
                (spent, reserved) = (ledger.Spent, ledger.Reserved);
            }

            writer.WriteLine($"tree spent {BudgetLedger.Dollars(spent)}, reserved {BudgetLedger.Dollars(reserved)}");
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            writer.WriteLine($"tree: ledger unavailable ({ex.Message})");
        }
    }
}
