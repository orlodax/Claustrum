using System.Globalization;
using Claustrum.Casts;
using Claustrum.Core.Git;
using Claustrum.Core.Jobs;
using Claustrum.Delegation;

namespace Claustrum.Coordination;

// The two pieces of text `coordinate` writes: the architect's user prompt (docs/PLAN.md §B3's
// `## Task`/`## Context` convention) and the `## Coordination` section appended to its system body
// (§D3's "with the cast injected into its system body"). Pure and dependency-free on purpose — the
// appendix is the contract a spawned architect actually obeys, so it must be assertable against a
// hand-built Cast without gh, a backend, or a job on disk. The job id is not a dependency either:
// it does not exist when this renders (issue #23), so every line that names it — or, isolated (#74),
// the architect's worktree path — emits DelegateRequest.JobIdToken, filled in once the job is minted.
public static class CoordinationBrief
{
    // CastResolution.ApplyRole's fallback, repeated here because the appendix states what each role
    // WILL run as, and a cast entry with no tier of its own still runs at "high".
    private const string DefaultTier = "high";

    // The order §D3's pipeline actually goes in — not the cast file's key order, which is the role
    // library's and would move the moment a role is added. demo-author is last, and only for a
    // browser-facing feature: it records the commit the tester's gate passed on.
    private static readonly string[] pipelineOrder = ["builder", "code-reviewer", "ui-reviewer", "tester", "demo-author"];

    private const string ContextHeading = "## Context";

    /// <summary>
    /// Where the architect works: <paramref name="cwd"/> itself, or — isolated (#74) — its own worktree
    /// under it, whose path carries <see cref="DelegateRequest.JobIdToken"/> until the job exists.
    /// </summary>
    public static string ArchitectCwd(string cwd, bool isolated) =>
        isolated ? JobWorktree.PathFor(cwd, DelegateRequest.JobIdToken) : cwd;

    public static string WorkingDirectoryLine(string workingDirectory) => $"- Working directory: {workingDirectory}";

    public static string RenderUserPrompt(string task, IReadOnlyList<int> issues, string workingDirectory)
    {
        List<string> lines = ["## Task", task.Trim(), "", ContextHeading];

        // The architect is not blind (docs/PLAN.md §B3: `## Context` is for non-blind roles), so the
        // issue numbers can be named — and `Closes #<n>` is the owner's rule 3, not a suggestion.
        if (issues.Count > 0)
        {
            string numbers = string.Join(", ", issues.Select(issue => $"#{issue}"));
            lines.Add($"- Issues: {numbers}. The commit or PR that resolves one cites `Closes #<n>` (owner's rule).");
        }

        lines.Add(WorkingDirectoryLine(workingDirectory));

        // Last, because the appendix itself is in the *system* prompt: the end of the user prompt is
        // the position a model honours most (Runner.AppendReportTrailer restates its own rule there
        // for the same measured reason).
        lines.Add("- Your system prompt carries a `## Coordination` section — the cast, the delegate command, the budget rules and your work branch. Follow it literally.");

        return string.Join('\n', lines);
    }

    /// <summary>
    /// <paramref name="userPrompt"/> (a <see cref="RenderUserPrompt"/> output) with its `Working directory:` line
    /// moved from <paramref name="placeholder"/> to <paramref name="workingDirectory"/> — in the `## Context`
    /// block this class wrote, never in the task above it, which may quote that very line (#89).
    /// </summary>
    public static string BindWorkingDirectory(string userPrompt, string placeholder, string workingDirectory)
    {
        // The last heading is ours: the task is rendered before it, and nothing after it repeats it.
        int context = userPrompt.LastIndexOf($"\n{ContextHeading}\n", StringComparison.Ordinal);
        if (context < 0)
            throw new InvalidOperationException($"not a coordinate user prompt: no '{ContextHeading}' block to bind");

        string bound = userPrompt[context..].Replace(WorkingDirectoryLine(placeholder), WorkingDirectoryLine(workingDirectory), StringComparison.Ordinal);
        return string.Concat(userPrompt.AsSpan(0, context), bound);
    }

    // `isolated` (#74): the architect runs in its own worktree under `cwd`, so every path it hands a
    // child is that worktree's, and the work branch already exists; not isolated, there is no git at all
    // (NOTES.md "coordinate runs the architect in its own worktree").
    public static string RenderSystemAppendix(Cast cast, string castName, string cwd, string? modelOverride = null, bool isolated = false)
    {
        string workingDirectory = ArchitectCwd(cwd, isolated);
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
            $"Delegate with: claustrum run <role> --cast \"{castName}\" --brief-file <path> --json --cwd \"{workingDirectory}\"",
            "- The cast above picks each role's model, tier and parallelism. Add --tier xhigh or --tier max on a single call when that piece of work warrants it.",
            "- Every claustrum process started from this shell already carries CLAUSTRUM_PARENT_JOB, so the runner accounts every child against this tree and caps it.",
            "- Read `error` whenever a result's `status` is not `success`: it carries the reason (a refused budget, a blind-gate rejection, a missing backend), and `status` alone does not say which.",
            """- A child that comes back with `status: budget_exceeded` has not necessarily spent anything — its `error` says what to do. "… while N running job(s) hold …": wait for one of your running children to finish, then start it again. "$R remaining; --budget X exceeds it": start it again with `--budget` at most R, or wait for a sibling to finish and free more. "rounds to $0.00 — pass --budget (at most $Y)": start it again with that explicit `--budget`. "$0.00 remaining" with nothing of yours running: the tree is spent — stop and report what is done. When you start two children at once (reviewer ‖ ui-reviewer, several builders), give each an explicit `--budget <usd>` that together fit the remaining budget; a child started without one reserves the whole remainder until it finishes, so its sibling is refused.""",
            "- Write each brief to .claustrum/briefs/<n>-<role>.md first, then pass that path to --brief-file.",
            "",
            .. isolated ? WorkBranchLines(cwd, workingDirectory) : [NoGitLine(cwd)],
        ]);

        return string.Join('\n', lines);
    }

    private static bool IsExtraRole(string role) =>
        role != Cast.ArchitectRole && !pipelineOrder.Contains(role, StringComparer.Ordinal);

    // #74 G4: `coordinate` isolates whenever its cwd is in a git repository, so in place means no git at all —
    // no branch to cut, rebase onto or send back, and no worktree to clean: every git line below is moot.
    private static string NoGitLine(string cwd) =>
        $"No git repository at {cwd}: there is no work branch and no worktree isolation — builders run in place, one at a time, and their changes land directly in {cwd}; nothing to rebase or clean.";

    private static string[] WorkBranchLines(string cwd, string workingDirectory)
    {
        return
        [
            $"Work branch: claustrum/{DelegateRequest.JobIdToken} — you are already on it, in your own worktree {workingDirectory}, cut from the operator's HEAD: it holds committed files only.",
            $"- The main checkout at {cwd} is the operator's: never cd into it, and never change its branch or its files.",
            $"- Every delegation takes --cwd \"{workingDirectory}\", as `Delegate with:` above does: an isolated builder's worktree then nests under yours, cut from the work branch's tip.",
            "- Commit your integration yourself and leave your worktree clean: when you finish, the runner commits whatever is still uncommitted in it onto the work branch, stray files included.",
            "- A builder running in parallel returns `worktree` and `branch` (claustrum/<its own job id>), and that branch carries its work as a commit (`commit` on the receipt) — unless its `warnings[]` says what did not land, in one of these shapes. `work left uncommitted on <branch>`: git refused that commit, and the work is still in the builder's `worktree`; commit it inside that worktree yourself before integrating. `work left uncommitted: <path> is on <a detached HEAD | refs/heads/…>, not <branch>`: the builder stopped mid-rebase or switched branch, and a commit there would land off its branch — first finish or abort the rebase (`git -C <path> rebase --continue`/`--abort`) or `git -C <path> switch <branch>`, then commit inside it. `work left uncommitted: <path> has a <merge | rebase | git am> in progress, not a clean <branch>`, or `work left uncommitted: <path> has unresolved conflicts on <branch> — …`: the builder stopped mid-operation, and a commit there would conclude it — abort a merge (`git -C <path> merge --abort`: integration never makes a merge commit); finish or abort a rebase or `git am` (`--continue`/`--abort`); resolve the conflicts (fix the files, then `git -C <path> add` them) or abort what made them (`cherry-pick --abort`, `revert --abort`, `reset --merge`); then commit inside that worktree yourself. `work left uncommitted: <path> is not the job worktree on <branch> (…)`: the worktree was gone or foreign, so nothing reached the branch and what the builder wrote is in `<path>`, outside every branch — move it by hand or rerun the builder. A `… left out of the commit on <branch>` warning means the branch lacks that path while the builder's worktree still holds it — if it belongs in the change, stage and commit it there yourself before integrating (an embedded repository: move it out or add it as a submodule first).",
            IntegrationLine(workingDirectory),
            "- To send an isolated builder's reviewed branch back for remediation (one that ran under `max_parallel` above 1 or with `--branch`; an in-place builder has no branch of its own — its work is already in your working tree), run the builder with `--branch <the branch on that builder's receipt>`: it continues on the same branch, in a fresh worktree, and its fix lands there as another commit.",
            "- `max_parallel` is a cap at every value, 1 included: a builder past it waits for a slot — to actually run builders at once, start the runs in the background and `wait` (the recipe is in your Delegation contract). Isolation starts at 2.",
            "- Never a merge commit, never `git push`.",
        ];
    }

    // Phrase for phrase roles/architect/ROLE.md's (#74 F5), plus the deny clause the host architect has no
    // need of: a rebase in the architect's own tree leaves it on the builder's branch, and `git switch` is denied (#62).
    private static string IntegrationLine(string workingDirectory)
    {
        return "- Integrate each builder branch by rebasing it onto the work branch, then fast-forward the work branch to it. The branch stays checked out in the builder's worktree until `claustrum jobs clean`, "
            + "and git will not rebase a branch checked out elsewhere: rebase it inside the builder's worktree (`git -C <worktree> rebase <work branch>`), then fast-forward the work branch to it from your own working tree (`git merge --ff-only <builder branch>`). "
            + "Never rebase in your own working tree: that leaves it on the builder's branch, and `git checkout`/`git switch` are denied for this run. "
            + $"Once the builders are integrated — not before — run `claustrum jobs clean --cwd \"{workingDirectory}\"` (finished worktrees go, branches stay).";
    }

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
        // needs the number: it decides how many briefs it may fan out at once. R6: a null one is no
        // cap and no gate at all (#58) — printed as 1 it contradicted the "a cap at every value" line.
        string parallel = role != "builder"
            ? ""
            : entry.MaxParallel is { } cap
                ? $", max_parallel {cap.ToString(CultureInfo.InvariantCulture)}"
                : """, max_parallel not set (no cap, no isolation — add "max_parallel": 1 to the cast to serialise builders)""";

        return $"- {role}: {Describe(entry.Model, entry.Tier)}{parallel}";
    }

    private static string Describe(string? model, string? tier) =>
        $"{(model is { Length: > 0 } ? $"model {model}" : "model per claustrum.json")}, tier {tier ?? DefaultTier}";
}
