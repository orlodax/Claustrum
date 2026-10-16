using System.Text.Json;
using Claustrum.Casts;
using Claustrum.Core;
using Claustrum.Core.Backends;
using Claustrum.Core.Config;
using Claustrum.Core.IO;
using Claustrum.Core.Jobs;
using Claustrum.Core.Json;
using Claustrum.Core.Model;
using Claustrum.Live;
using Claustrum.Roles;

namespace Claustrum.Delegation;

// #126: a cast's fallback. A run whose provider refused it before it did any work is retried once, as a new job, on the model the cast
// named for the role — same brief (the blind gate checked again), role, tier, permission, cast and tree. NOTES.md "A cast can name a
// fallback model" has the 402 that started this, the before-any-work predicate and what the retry deliberately leaves alone.
internal static class FallbackRetry
{
    private const int DetailChars = 300;

    /// <summary>
    /// Whether the cast's fallback may answer <paramref name="first"/>: the request has one (a flag did not choose the model), it is not
    /// already a retry, and it does not continue a session that only its own backend can read; the run failed with a provider-side
    /// <see cref="ErrorKind"/> before any work (<see cref="ProviderErrors.BeforeAnyWork"/>).
    /// </summary>
    public static bool Applies(DelegateRequest request, RunResult first) =>
        request.Fallback is { Length: > 0 }
        && request.FallbackOf is null
        && string.IsNullOrEmpty(request.ResumeSession)
        && request.ParentJobId is null
        && request.Pin is null
        && Qualifies(first);

    /// <summary>
    /// The receipt's half of <see cref="Applies"/>, for a reader that has no request: a failure of the provider's making, before any work
    /// (<see cref="ProviderErrors.BeforeAnyWork"/>), that nothing has retried yet.
    /// </summary>
    public static bool Qualifies(RunResult first) =>
        first is { Status: RunStatus.Failed, ErrorKind: { } kind, BeforeAnyWork: true, RetriedAs: null } && ProviderErrors.IsProviderSide(kind);

    /// <summary>
    /// <paramref name="first"/> when the fallback does not apply or cannot be used (then said on its receipt), else the retry's receipt after
    /// <paramref name="runRetry"/> ran it in a job minted here. <paramref name="prepare"/> resolves the retry's request the way the front door
    /// resolved the first's; the first job's receipt names the retry (<c>retried_as</c>) before the retry starts, so a follower can switch to it.
    /// </summary>
    public static async Task<RunResult> RunAsync(
        PreparedDelegation prepared,
        RunResult first,
        Func<DelegateRequest, PreparedDelegation> prepare,
        Func<PreparedDelegation, JobPaths, Task<RunResult>> runRetry)
    {
        if (!Applies(prepared.Request, first))
            return first;

        (PreparedDelegation? retry, string? declined) = Build(prepared, first, prepare);
        if (retry is null)
        {
            RunResult noted = first with { Warnings = [.. first.Warnings, $"{first.Role}: fallback {prepared.Request.Fallback} not used: {declined}"] };
            Rewrite(noted);
            return noted;
        }

        // The retry's host first (when the first job had one), then the first receipt's pointer: whoever follows retried_as finds a job `jobs cancel` can reach.
        JobPaths retryJob = JobDirectory.Create(AppServices.Platform);
        List<string> notes = [];
        if (Adopt(first, retryJob) is { } unhosted)
            notes.Add($"job {retryJob.Id} could not be recorded as the detached host's own, so `jobs cancel` cannot reach it: {unhosted}");
        if (Rewrite(first with { RetriedAs = retryJob.Id }) is { } unwritten)
            notes.Add($"job {first.JobId}'s receipt could not be rewritten with retried_as: {unwritten}");
        if (notes.Count > 0)
            retry = retry with { Options = retry.Options with { PreRunWarnings = [.. retry.Options.PreRunWarnings ?? [], .. notes] } };

        return await runRetry(retry, retryJob);
    }

    private static (PreparedDelegation? Retry, string? Declined) Build(PreparedDelegation prepared, RunResult first, Func<DelegateRequest, PreparedDelegation> prepare)
    {
        DelegateRequest request = prepared.Request;
        string fallback = request.Fallback!;

        // The first job's cost is spent whatever happens next: the retry gets what is left of the run's own cap. A tree's ledger does the
        // same for the tree's cap when the retry is admitted, since the first job's charge is already in it.
        decimal? remaining = prepared.BudgetUsd is { } cap ? cap - (first.CostUsd ?? 0m) : null;
        if (remaining <= 0m)
            return (null, $"the run's budget of ${prepared.BudgetUsd} went on the first job");

        DelegateRequest retryRequest = request with
        {
            Overrides = request.Overrides with { Model = fallback, Backend = null },
            Fallback = null,
            FallbackResolved = null,
            FallbackOf = first.JobId,
            PreRunWarnings = null,
        };

        try
        {
            PreparedDelegation retry = prepare(retryRequest);

            // The blind gate again, exactly as for the first: nothing is added to the brief, and nothing minted if it refuses.
            DelegateEngine.Validate(retry);

            string[] warnings = [Warning(first, retry), .. Drift(request, fallback, first.Role), .. retry.Options.PreRunWarnings ?? []];
            return (retry with { BudgetUsd = remaining ?? retry.BudgetUsd, Options = retry.Options with { PreRunWarnings = warnings } }, null);
        }
        catch (Exception ex) when (ex is ConfigException or RoleRenderException or RunRequestException or BlindGateException)
        {
            return (null, ex.Message);
        }
    }

    // code-reviewer: opencode:openrouter/deepseek/deepseek-v4-pro failed before starting (provider_quota: This request would exceed …); ran on claude:opus instead (job 2026…)
    private static string Warning(RunResult first, PreparedDelegation retry)
    {
        string detail = string.Join(' ', (first.ErrorDetail ?? first.Error ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (detail.Length > DetailChars)
            detail = $"{detail[..DetailChars]}…";

        return $"{first.Role}: {first.Backend}:{first.Model} failed before starting ({ProviderErrors.Name(first.ErrorKind!.Value)}: {detail}); "
            + $"ran on {retry.Role.Backend}:{retry.Role.Model} instead (job {first.JobId})";
    }

    // #121 for the fallback: said at the moment it is used, not on every run that never needs it.
    private static string[] Drift(DelegateRequest request, string fallback, string role)
    {
        if (request.FallbackResolved is not { } stored || request.CastName is not { } castName)
            return [];

        ModelResolution now = Config.Load(AppServices.Platform, request.Cwd).Describe(fallback);
        string target = CastModelLines.Target(now);
        return stored == target ? [] : [CastModelLines.ResolvedChangedWarning(castName, $"{role} fallback", fallback, stored, target, now.Source ?? "")];
    }

    // A retry run by a detached host is that host's job too (the process is the same, so is the tree): its host.json is the first's, which is what
    // `jobs cancel`, the tree's settle and a waiter's abandoned check read. Null when written or when the first job had no host; otherwise why not.
    private static string? Adopt(RunResult first, JobPaths retryJob)
    {
        if (LiveJob.Find(first.JobId)?.ReadHost() is not { } host)
            return null;

        try
        {
            AtomicFile.WriteAllText(retryJob.HostJson, JsonSerializer.Serialize(host, LiveJsonContext.Default.HostRecord));
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }
    }

    // Null when written; otherwise why not. A receipt that cannot be rewritten must not stop the retry that was already decided.
    private static string? Rewrite(RunResult receipt)
    {
        if (JobDirectory.Open(AppServices.Platform, receipt.JobId) is not { } paths)
            return "the job directory is gone";

        try
        {
            AtomicFile.WriteAllText(paths.ResultJson, JsonSerializer.Serialize(receipt, ClaustrumJsonContext.Default.RunResult));
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }
    }
}
