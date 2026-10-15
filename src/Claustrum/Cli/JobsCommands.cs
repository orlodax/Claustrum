using System.CommandLine;
using System.Text;
using System.Text.Json;
using Claustrum.Coordination;
using Claustrum.Core.Git;
using Claustrum.Core.Jobs;
using Claustrum.Roles.Sync;

namespace Claustrum.Cli;

// docs/PLAN.md §A5 `claustrum jobs list [--last N]|show <id>|logs <id> [--stderr]|clean`, reading
// `~/.claustrum/jobs` (`CLAUSTRUM_HOME` honoured via JobDirectory.ResolveRoot); `say`, `wait`, `reply`, `cancel` and the hidden `host` are #124's live channel, `answer` its fallback.
public static class JobsCommands
{
    private const int DefaultLast = 20;

    public static Command Build()
    {
        Option<int> last = new("--last") { Description = "How many recent jobs to show.", DefaultValueFactory = _ => DefaultLast };
        Command list = new("list", "List recent jobs.") { last };
        list.SetAction(parseResult => List(parseResult.GetValue(last)));

        Argument<string> showId = new("id") { Description = "Job id." };
        Command show = new("show", "Show one job's result (or request, if it has not finished).") { showId };
        show.SetAction(parseResult => Show(parseResult.GetRequiredValue(showId)));

        Argument<string> logsId = new("id") { Description = "Job id." };
        Option<bool> stderrOption = new("--stderr") { Description = "Show stderr.log instead of stdout.log." };
        Command logs = new("logs", "Print a job's captured output.") { logsId, stderrOption };
        logs.SetAction(parseResult => Logs(parseResult.GetRequiredValue(logsId), parseResult.GetValue(stderrOption)));

        Option<string?> cleanCwd = new("--cwd") { Description = "Working directory (default: current directory)." };
        Command clean = new(
            "clean",
            "Remove finished isolated jobs' worktrees (docs/PLAN.md §D4); their branches, which carry the work, are kept. "
                + "A worktree with uncommitted changes, on a detached HEAD, mid-rebase/merge/cherry-pick, holding commits only its own reflog has, "
                + "with edits hidden by skip-worktree or assume-unchanged, or that is not this repository's worktree is reported and left in place, and so is one holding job worktrees "
                + "of its own (a coordinate architect's: clean it with --cwd <that worktree> first). The first Ctrl-C or SIGTERM lets the remove in "
                + "flight finish and stops the sweep (exit 130); a second one exits at once.") { cleanCwd };
        clean.SetAction(async parseResult => await CleanAsync(parseResult.GetValue(cleanCwd)));

        Argument<string> budgetTree = new("tree") { Description = "Job tree id — the CLAUSTRUM_PARENT_JOB its members ran with." };
        Option<bool> budgetReset = new("--reset") { Description = "Delete this tree's ledger. Refused while one of its jobs is still running." };
        Command budget = new("budget", "Show (or reset) one job tree's budget ledger (docs/PLAN.md §D4).") { budgetTree, budgetReset };
        budget.SetAction(async parseResult => await BudgetAsync(parseResult.GetRequiredValue(budgetTree), parseResult.GetValue(budgetReset)));

        Command follow = JobsFollowCommand.Build();
        Command say = JobsSayCommand.Build();
        Command wait = JobsWaitCommand.Build();
        Command reply = JobsReplyCommand.Build();
        Command answer = JobsAnswerCommand.Build();
        Command cancel = JobsCancelCommand.Build();
        Command host = JobsHostCommand.Build();

        return new Command("jobs", "Inspect past and running jobs, and talk to a live one.") { list, show, logs, clean, budget, follow, say, wait, reply, answer, cancel, host };
    }

    private static int List(int last)
    {
        string root = JobDirectory.ResolveRoot(AppServices.Platform);
        if (!Directory.Exists(root))
        {
            Console.WriteLine("(no jobs yet)");
            return ExitCodes.Ok;
        }

        IEnumerable<string> ids = Directory.EnumerateDirectories(root)
            .Select(Path.GetFileName)
            .OfType<string>()
            .OrderDescending(StringComparer.Ordinal)
            .Take(last);

        foreach (string id in ids)
            Console.WriteLine($"{id}  {Summarize(root, id)}");

        return ExitCodes.Ok;
    }

    private static string Summarize(string root, string id)
    {
        string resultPath = Path.Combine(root, id, "result.json");
        if (!File.Exists(resultPath))
            return "pending (no result.json)";

        // A job killed mid-write (crash, `kill -9`, power loss) leaves a truncated/partial
        // result.json; one bad job must not take the whole `jobs list` down (review finding #5).
        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(resultPath));
            JsonElement rootElement = document.RootElement;
            string status = rootElement.TryGetProperty("status", out JsonElement s) ? s.GetString() ?? "?" : "?";
            string role = rootElement.TryGetProperty("role", out JsonElement r) ? r.GetString() ?? "?" : "?";
            string backend = rootElement.TryGetProperty("backend", out JsonElement b) ? b.GetString() ?? "?" : "?";
            return $"{status,-16} {role}/{backend}";
        }
        catch (JsonException)
        {
            return "incomplete (unparsable result.json)";
        }
    }

    private static int Show(string id)
    {
        string directory = Path.Combine(JobDirectory.ResolveRoot(AppServices.Platform), JobId.Require(id));
        string resultPath = Path.Combine(directory, "result.json");
        string requestPath = Path.Combine(directory, "request.json");

        if (File.Exists(resultPath))
        {
            PrintIndentedJson(File.ReadAllText(resultPath));
            return ExitCodes.Ok;
        }

        if (File.Exists(requestPath))
        {
            Console.WriteLine("(no result yet)");
            PrintIndentedJson(File.ReadAllText(requestPath));
            return ExitCodes.Ok;
        }

        Console.Error.WriteLine($"job '{id}' not found under {JobDirectory.ResolveRoot(AppServices.Platform)}");
        return ExitCodes.Usage;
    }

    private static int Logs(string id, bool showStderr)
    {
        string directory = Path.Combine(JobDirectory.ResolveRoot(AppServices.Platform), JobId.Require(id));
        string logPath = Path.Combine(directory, showStderr ? "stderr.log" : "stdout.log");

        if (!File.Exists(logPath))
        {
            Console.Error.WriteLine($"no {(showStderr ? "stderr.log" : "stdout.log")} for job '{id}'");
            return ExitCodes.Usage;
        }

        Console.Write(JobLog.ReadAllText(logPath));
        return ExitCodes.Ok;
    }

    // A worktree's directory name IS the job id that created it (JobWorktree.PathFor);
    // JobDirectory.IsFinished decides which ones are done with. The branch is never touched here, and
    // it carries the job's work as a commit since #61, so the architect can still rebase from it. A
    // worktree that still holds uncommitted work is reported and left, never forced (F3) — and so is a
    // coordinate architect's that still holds its children's (#74): `--cwd <it>` sweeps those first. A clean
    // one is renamed aside and its own registration deleted (#106); the sweep first deletes any `.trash-*` an earlier free left.
    private static async Task<int> CleanAsync(string? cwdOption)
    {
        string cwd = Path.GetFullPath(cwdOption ?? Environment.CurrentDirectory);
        string worktreesRoot = Path.Combine(cwd, ".claustrum", "worktrees");

        // #104, review 2026-10-14: a `.claustrum` link that leaves the repo would have the sweep delete, and the frees
        // rename, directories outside it. Core cannot see SyncRoot, so the fence is asked here, before anything is touched.
        if (SyncRoot.Repo(cwd).Escapes(worktreesRoot))
        {
            Console.Error.WriteLine(
                $"{worktreesRoot} resolves outside the working directory ({SyncRoot.Resolve(worktreesRoot)}): `.claustrum` is a link that leaves it, and `jobs clean` touches nothing through one");
            return ExitCodes.Usage;
        }

        if (!Directory.Exists(worktreesRoot))
        {
            Console.WriteLine("(no worktrees)");
            return ExitCodes.Ok;
        }

        string jobsRoot = JobDirectory.ResolveRoot(AppServices.Platform);

        // #106: the first Ctrl-C, SIGTERM or SIGHUP lets the remove in flight finish (it runs to its end under no token) and
        // stops the sweep; a second Ctrl-C or SIGTERM exits. Same handler as `run`; git shares our process group, so it hears the signal too.
        // #113: clean writes no receipt, so its second-signal hint says what is abandoned instead.
        using CancelSignalHandler cancel = new("stop now; a free in flight is left for the next jobs clean");
        int removed = 0;
        OperationCanceledException? cancelled = null;
        List<string> failures = [];
        try
        {
            (int swept, string[] sweepFailures) = await JobWorktree.SweepTrashAsync(cwd, cancel.Token);
            if (swept > 0)
                SafeConsole.Out($"swept {swept} leftover trash {(swept == 1 ? "directory" : "directories")}");
            foreach (string sweepFailure in sweepFailures)
                SafeConsole.Error($"warning: could not sweep {sweepFailure}");
            removed += swept;
        }
        catch (OperationCanceledException ex)
        {
            cancelled = ex;
        }

        // A snapshot, taken after the sweep: freeing a worktree creates (and deletes) a `.trash-*` beside it.
        foreach (string worktreeDirectory in Directory.EnumerateDirectories(worktreesRoot).Where(directory => !JobWorktree.IsTrash(Path.GetFileName(directory))).ToArray())
        {
            if (cancel.Token.IsCancellationRequested || cancelled is not null)
                break;

            string jobId = Path.GetFileName(worktreeDirectory);

            // #74 G5: a `coordinate` hard-killed mid-probe leaves an empty `probe-<hex>` no job owns, which
            // git's remove calls "not a working tree" — exit 1 on every sweep until someone deletes it by hand.
            if (await ArchitectWorktree.TryRemoveStrayProbeAsync(cwd, worktreeDirectory, CancellationToken.None))
            {
                SafeConsole.Out($"removed stray probe directory {jobId}");
                removed++;
                continue;
            }

            if (!JobDirectory.IsFinished(jobsRoot, jobId))
                continue;

            // Per entry, not per sweep: one directory git no longer recognises as a worktree (removed
            // by hand, or left by a hard kill) used to abort the whole command, so every stale
            // worktree after it stayed forever — and every rerun stopped at the same one (review
            // finding). Report it and keep going.
            try
            {
                string branch = await JobWorktree.HeadBranchAsync(cwd, jobId, cancel.Token) ?? JobWorktree.BranchFor(jobId);
                string? warning = await JobWorktree.RemoveAsync(cwd, jobId, cancel.Token);
                SafeConsole.Out($"removed {jobId} (branch {branch} kept)");
                if (warning is not null)
                    SafeConsole.Error($"warning: {warning}");
                removed++;
            }
            catch (OperationCanceledException ex)
            {
                cancelled = ex;
                break;
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or TimeoutException)
            {
                failures.Add($"{jobId}: {ex.Message}");
            }
        }

        // Nothing more is freed: a Windows console close need wait no longer.
        cancel.Settled();

        if (removed == 0 && failures.Count == 0 && !cancel.Token.IsCancellationRequested)
            SafeConsole.Out("(nothing to clean)");

        foreach (string failure in failures)
            SafeConsole.Error($"could not remove {failure}");

        if (cancelled is not null || cancel.Token.IsCancellationRequested)
            return cancelled is null ? ExitCodes.Cancelled : RunCommand.Cancelled(cancelled);

        // Exit 1 when anything was left behind so a script does not read a partial sweep as a full
        // one; the worktrees that did come off are still gone.
        return failures.Count == 0 ? ExitCodes.Ok : ExitCodes.BackendFailure;
    }

    // Exporting a tree id makes docs/PLAN.md §D4's ledger permanent: spend accumulates across
    // processes with nothing to inspect it and no way to start over (review finding F4). This is both.
    // `--reset` refuses while a job still holds its `.live` handle — deleting the ledger under a
    // running child would hand the next one the whole budget again.
    private static async Task<int> BudgetAsync(string tree, bool reset)
    {
        BudgetLedgerState state = await BudgetLedger.ReadAsync(AppServices.Platform, tree);
        Console.WriteLine(state.Directory);

        if (state.Rows.Length == 0)
            Console.WriteLine("(no entries)");

        foreach (BudgetLedgerRow row in state.Rows)
            Console.WriteLine(Describe(row));

        Console.WriteLine($"spent     {BudgetLedger.Dollars(state.Spent)}");
        Console.WriteLine($"reserved  {BudgetLedger.Dollars(state.Reserved)}");

        if (!reset)
            return ExitCodes.Ok;

        if (state.Rows.FirstOrDefault(row => row.State == BudgetEntryState.Running) is { } running)
        {
            Console.Error.WriteLine($"job '{running.Entry.JobId}' of tree '{tree}' is still running; --reset refused");
            return ExitCodes.Usage;
        }

        // Outside the ledger lock deliberately: the directory being deleted *contains* the lock file,
        // and Windows will not remove a directory that still has an open handle in it. A child admitted
        // in that gap is the race any explicit reset has, and the caller asked for the reset.
        if (Directory.Exists(state.Directory))
            Directory.Delete(state.Directory, recursive: true);

        Console.WriteLine($"reset {state.Directory}");
        return ExitCodes.Ok;
    }

    private static string Describe(BudgetLedgerRow row) =>
        $"{row.Entry.JobId}  {row.Entry.Role,-12}  cap {Amount(row.Entry.Cap),8}  cost {Amount(row.Entry.Cost),8}  {StateText(row.State)}";

    private static string Amount(decimal? value) => value is { } amount ? BudgetLedger.Dollars(amount) : "-";

    private static string StateText(BudgetEntryState state) => state switch
    {
        BudgetEntryState.Running => "running",
        BudgetEntryState.Abandoned => "abandoned",
        _ => "done",
    };

    // Plain JsonDocument.WriteTo, not JsonSerializer: this only re-formats bytes already on disk,
    // so it needs no JsonTypeInfo and stays AOT-safe without touching ClaustrumJsonContext.
    private static void PrintIndentedJson(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        using MemoryStream buffer = new();
        using (Utf8JsonWriter writer = new(buffer, new JsonWriterOptions { Indented = true }))
            document.RootElement.WriteTo(writer);

        Console.WriteLine(Encoding.UTF8.GetString(buffer.ToArray()));
    }
}
