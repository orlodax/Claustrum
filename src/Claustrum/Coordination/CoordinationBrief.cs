using System.Globalization;
using Claustrum.Casts;
using Claustrum.Core.Jobs;
using Claustrum.Delegation;

namespace Claustrum.Coordination;

// The two pieces of text `coordinate` writes: the architect's user prompt (docs/PLAN.md §B3's
// `## Task`/`## Context` convention) and the `## Coordination` section appended to its system body
// (§D3's "with the cast injected into its system body"). Pure and dependency-free on purpose — the
// appendix is the contract a spawned architect actually obeys, so it must be assertable against a
// hand-built Cast without gh, a backend, or a job on disk. The job id is not a dependency either:
// it does not exist when this renders (issue #23), so the three lines that name it emit
// DelegateRequest.JobIdToken and the engine substitutes the real id once the directory is minted.
public static class CoordinationBrief
{
    // CastResolution.ApplyRole's fallback, repeated here because the appendix states what each role
    // WILL run as, and a cast entry with no tier of its own still runs at "high".
    private const string DefaultTier = "high";

    // The order §D3's pipeline actually goes in — not the cast file's key order, which is the role
    // library's and would move the moment a role is added. demo-author is last and optional: it
    // records the shipped state, so it only ever runs once the tester's gate is green.
    private static readonly string[] pipelineOrder = ["builder", "code-reviewer", "ui-reviewer", "tester", "demo-author"];

    public static string RenderUserPrompt(string task, IReadOnlyList<int> issues, string cwd)
    {
        List<string> lines = ["## Task", task.Trim(), "", "## Context"];

        // The architect is not blind (docs/PLAN.md §B3: `## Context` is for non-blind roles), so the
        // issue numbers can be named — and `Closes #<n>` is the owner's rule 3, not a suggestion.
        if (issues.Count > 0)
        {
            string numbers = string.Join(", ", issues.Select(issue => $"#{issue}"));
            lines.Add($"- Issues: {numbers}. The commit or PR that resolves one cites `Closes #<n>` (owner's rule).");
        }

        lines.Add($"- Working directory: {cwd}");

        // Last, because the appendix itself is in the *system* prompt: the end of the user prompt is
        // the position a model honours most (Runner.AppendReportTrailer restates its own rule there
        // for the same measured reason).
        lines.Add("- Your system prompt carries a `## Coordination` section — the cast, the delegate command, the budget rules and your work branch. Follow it literally.");

        return string.Join('\n', lines);
    }

    public static string RenderSystemAppendix(Cast cast, string castName, string cwd, string? modelOverride = null)
    {
        List<string> lines =
        [
            "## Coordination",
            "",
            $"Cast: {castName}",
            ArchitectLine(cast.Architect, modelOverride),
        ];

        foreach (string role in pipelineOrder)
            lines.Add(RoleLine(role, cast.Roles.GetValueOrDefault(role)));

        // A role this cast knows about that the pipeline order does not (a library role added after
        // this text was written): named rather than silently dropped, or the architect would be told
        // a cast it can read itself is smaller than it is.
        foreach (string role in cast.Roles.Keys.Where(IsExtraRole).OrderBy(role => role, StringComparer.Ordinal))
            lines.Add(RoleLine(role, cast.Roles.GetValueOrDefault(role)));

        lines.AddRange([
            "",
            cast.BudgetUsd is { } budget
                ? $"Budget: {BudgetLedger.Dollars(budget)} across the whole job tree"
                : "Budget: unlimited — children are not accounted; `claustrum jobs budget` will be empty",
            $"Job tree: {DelegateRequest.JobIdToken}",
            $"Inspect: claustrum jobs budget {DelegateRequest.JobIdToken}",
            "",
            // Both values quoted: a cwd or a cast name with a space in it otherwise splits into two
            // arguments, and double quotes read the same in bash and in PowerShell.
            $"Delegate with: claustrum run <role> --cast \"{castName}\" --brief-file <path> --json --cwd \"{cwd}\"",
            "- The cast above picks each role's model, tier and parallelism. Add --tier xhigh or --tier max on a single call when that piece of work warrants it.",
            "- Every claustrum process started from this shell already carries CLAUSTRUM_PARENT_JOB, so the runner accounts every child against this tree and caps it.",
            "- Read `error` whenever a result's `status` is not `success`: it carries the reason (a refused budget, a blind-gate rejection, a missing backend), and `status` alone does not say which.",
            """- A child that comes back with `status: budget_exceeded` has not necessarily spent anything — its `error` says what to do. "… while N running job(s) hold …": wait for one of your running children to finish, then start it again. "$R remaining; --budget X exceeds it": start it again with `--budget` at most R, or wait for a sibling to finish and free more. "rounds to $0.00 — pass --budget (at most $Y)": start it again with that explicit `--budget`. "$0.00 remaining" with nothing of yours running: the tree is spent — stop and report what is done. When you start two children at once (reviewer ‖ ui-reviewer, several builders), give each an explicit `--budget <usd>` that together fit the remaining budget; a child started without one reserves the whole remainder until it finishes, so its sibling is refused.""",
            "- Write each brief to .claustrum/briefs/<n>-<role>.md first, then pass that path to --brief-file.",
            "",
            $"Work branch: claustrum/{DelegateRequest.JobIdToken} — create it from the current HEAD before delegating anything.",
            "- A builder running in parallel returns `worktree` and `branch` (claustrum/<its own job id>). Integrate each one with `git rebase` onto the work branch, then fast-forward the work branch to it.",
            "- Never a merge commit, never `git push`.",
        ]);

        return string.Join('\n', lines);
    }

    private static bool IsExtraRole(string role) =>
        role != Cast.ArchitectRole && !pipelineOrder.Contains(role, StringComparer.Ordinal);

    // The appendix describes the cast, but --model overrides only *this* run, so the override is
    // named next to the cast's model instead of letting the two disagree silently.
    private static string ArchitectLine(CastArchitect architect, string? modelOverride)
    {
        string thisRun = modelOverride is { Length: > 0 } ? $" (this run: {modelOverride})" : "";
        return $"- {Cast.ArchitectRole}: mode {architect.Mode}, {Describe(architect.Model, architect.Tier)}{thisRun} — that is you, in this run";
    }

    private static string RoleLine(string role, CastRoleEntry? entry)
    {
        if (entry is null)
            return $"- {role}: not needed for this cast — do not delegate to it";

        // max_parallel is builder-only by construction (CastBuilder.FromAnswers), and the architect
        // needs the number: it decides how many briefs it may fan out at once.
        string parallel = role == "builder"
            ? $", max_parallel {(entry.MaxParallel ?? 1).ToString(CultureInfo.InvariantCulture)}"
            : "";

        return $"- {role}: {Describe(entry.Model, entry.Tier)}{parallel}";
    }

    private static string Describe(string? model, string? tier) =>
        $"{(model is { Length: > 0 } ? $"model {model}" : "model per claustrum.json")}, tier {tier ?? DefaultTier}";
}
