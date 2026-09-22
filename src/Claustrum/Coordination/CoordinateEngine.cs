using Claustrum.Casts;
using Claustrum.Cli;
using Claustrum.Core.Config;
using Claustrum.Core.Jobs;
using Claustrum.Core.Model;
using Claustrum.Delegation;

namespace Claustrum.Coordination;

// docs/PLAN.md §D3's spawned architect: one `claustrum run architect` with the cast injected into
// its system body and the job tree handed to its children. Everything specific to `coordinate` lives
// here rather than as a branch inside DelegateEngine — the engine stays the one pipeline `run` and
// `delegate` already share (NOTES.md "coordinate: a spawned architect is a job run with the cast
// injected and the tree handed down"). Two steps: PlanAsync before the job exists, RunAsync around
// the delegation, which is where the coordinator's own cost reaches its tree's ledger (issue #21).
public static class CoordinateEngine
{
    /// <summary>
    /// Everything that can be decided — and refused — with no job on disk: the task source, the cast
    /// (loaded once, then resolved once for the architect), the `gh` import and the architect's user
    /// prompt. Both front doors call it before any job directory is minted, so a usage error, a
    /// missing cast and a failed `gh` all leave the job store untouched;
    /// <see cref="CoordinatePlan.Prepare"/> is the second half, and still mints nothing.
    /// </summary>
    public static async Task<CoordinatePlan> PlanAsync(CoordinateRequest request, IIssueSource issues, CancellationToken cancellationToken)
    {
        // Flags first, cast second, gh last: a mistyped invocation should name the flags, not the
        // repo state, and the only step that costs a network round trip goes after both.
        string? brief = RequireOneTaskSource(request);

        // Runner validates the same thing, but only from inside DelegateEngine.RunAsync — by which
        // point both doors have minted the job directory a refused invocation must not leave behind.
        if (request.Overrides.TimeoutSeconds is <= 0)
            throw new CliUsageException("--timeout must be greater than zero");

        // No gate on Architect.Mode: `host` is what the /claustrum skill reads to decide which path
        // to take, and a human who typed `coordinate` anyway has asked for this one explicitly.
        string castName = request.CastName is { Length: > 0 } named ? named : CastStore.DefaultName;
        Cast cast = request.CastName is { Length: > 0 }
            ? CastStore.Load(request.Cwd, castName)
            : CastStore.TryLoadDefault(request.Cwd)
                ?? throw new CastException("coordinate needs a cast: pass --cast <name> or create .claustrum/casts/default.json");

        string task = brief ?? IssueImporter.RenderTask(await LoadIssuesAsync(request, issues, cancellationToken));

        // The architect's tier/model/budget precedence, resolved here and kept on the plan: doing it
        // in ToDelegateRequest re-read the cast file *after* the job directory had been minted, so a
        // cast edited or deleted in between threw over a job nothing would ever close (issue #23).
        (string tier, ConfigOverrides overrides, CastBudget? castBudget, _, _) =
            CastApplication.Resolve(request.Cwd, Cast.ArchitectRole, request.CastName, request.TierFlag, request.Overrides);

        return new CoordinatePlan(
            request, cast, castName, CoordinationBrief.RenderUserPrompt(task, request.Issues, request.Cwd), tier, overrides, castBudget);
    }

    /// <summary>
    /// The architect's run, plus the one thing that happens after it: its own cost written to the
    /// ledger of the tree it handed down (issue #21). Both front doors go through here — the MCP one
    /// as the JobManager callback — so the coordinator is accounted whichever door started it.
    /// </summary>
    public static async Task<RunResult> RunAsync(CoordinatePlan plan, PreparedDelegation prepared, JobPaths job, CancellationToken cancellationToken)
    {
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        RunResult result = await DelegateEngine.RunAsync(prepared, job, cancellationToken);

        // Only a capped cast has a ledger at all (DelegateEngine builds no JobTreeBudget without a
        // cap, so its children write no entry either). The architect is deliberately not a *member*
        // of its own tree — nothing reserved its cap, and nothing would ever close an entry for it —
        // so the entry is written here, finished, once the run is over: `jobs budget <tree>` then
        // totals the whole tree instead of only the children (NOTES.md "M4 follow-ups…").
        if (plan.Cast.BudgetUsd is null)
            return result;

        // A run that never spawned a process is not a $0 row, it is no row: RecordFinishedAsync
        // creates the tree's ledger directory, and an empty-but-present one would deny
        // CoordinateCommand its "no ledger directory ⇒ no child ever ran" reading (review finding).
        if (NeverRan(result))
            return result;

        try
        {
            // ChargeAsync's rule, mirrored: the reported cost, or the granted cap when the backend
            // ran and reported none (cursor and copilot never do).
            decimal? cost = result.CostUsd ?? prepared.BudgetUsd;
            await BudgetLedger.RecordFinishedAsync(
                AppServices.Platform, job.Id, job.Id, prepared.Request.Role, prepared.BudgetUsd, cost, startedAt);

            return result;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            // The run has finished and its result.json is already written; a ledger that cannot be
            // reached must not turn it into a failure (the same three exceptions every other ledger
            // caller swallows). ⚠ The warning rides the returned result only — result.json on disk was
            // written by Runner before this ran, and is not rewritten.
            return result with
            {
                Warnings = [.. result.Warnings, $"budget ledger for tree '{job.Id}' not updated with the architect's own cost: {ex.Message}"],
            };
        }
    }

    // Runner's "nothing ran" shapes, read back off the result: the two statuses that exist only
    // without a process, plus the pre-spawn failure — which is a plain Failed, and tells itself apart
    // by hardcoding exit -1 *and* a zero duration, where a killed process reports a real elapsed time
    // next to its own -1 (Windows). Same notion Runner.ChargeAsync gets handed as `ran`.
    private static bool NeverRan(RunResult result) =>
        result.Status is RunStatus.BackendMissing or RunStatus.BudgetExceeded
        || (result.ExitCode == -1 && result.DurationSeconds == 0);

    // The brief text, or null when --issues carries the task — and a CliUsageException when both or
    // neither do.
    private static string? RequireOneTaskSource(CoordinateRequest request)
    {
        string? brief = string.IsNullOrWhiteSpace(request.Brief) ? null : request.Brief;
        if (request.Issues.Length > 0 && brief is not null)
            throw new CliUsageException("use either --issues or --brief/--brief-file, not both");
        if (request.Issues.Length == 0 && brief is null)
            throw new CliUsageException("coordinate needs a task: pass --issues <n,m> or --brief/--brief-file");

        return brief;
    }

    // Serial, in the order given: `gh` talks to github.com, and the brief's `## Task` reads in the
    // order the human named the issues.
    private static async Task<List<GhIssue>> LoadIssuesAsync(CoordinateRequest request, IIssueSource issues, CancellationToken cancellationToken)
    {
        List<GhIssue> loaded = [];
        foreach (int number in request.Issues)
            loaded.Add(await issues.ViewAsync(request.Cwd, number, cancellationToken));

        return loaded;
    }
}
