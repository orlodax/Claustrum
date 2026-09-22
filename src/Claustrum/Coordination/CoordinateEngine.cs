using Claustrum.Casts;
using Claustrum.Cli;

namespace Claustrum.Coordination;

// docs/PLAN.md §D3's spawned architect: one `claustrum run architect` with the cast injected into
// its system body and the job tree handed to its children. Everything here happens *before*
// DelegateEngine.RunAsync — which is why it is a separate class rather than a branch inside the
// engine: the engine stays the one pipeline both `run` and `delegate` already share (NOTES.md
// "coordinate: a spawned architect is a job run with the cast injected and the tree handed down").
public static class CoordinateEngine
{
    /// <summary>
    /// Everything that can be decided — and refused — with no job on disk: the task source, the cast,
    /// the `gh` import and the architect's user prompt. Both front doors call it before any job
    /// directory is minted, so a usage error, a missing cast and a failed `gh` all leave the job store
    /// untouched; <see cref="CoordinatePlan.ToDelegateRequest"/> finishes the request afterwards.
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

        return new CoordinatePlan(request, cast, castName, CoordinationBrief.RenderUserPrompt(task, request.Issues, request.Cwd));
    }

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
