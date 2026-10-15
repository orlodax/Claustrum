using System.Text.Json;
using Claustrum.Core;
using Claustrum.Core.Jobs;
using Claustrum.Core.Json;
using Claustrum.Core.Model;

namespace Claustrum.Delegation;

// #124 stage 2: the body of `claustrum jobs host <id>`. Runs the job another process minted, from the request it left in the
// job directory, exactly as that process would have in-process: the same Prepare, the same DelegateEngine.RunAsync (slot, worktree,
// budget admission, Runner). What differs is who can cancel it: a signal, or the marker `jobs cancel` leaves.
internal static class DetachedHost
{
    private const int CancelPollMs = 250;

    /// <summary>
    /// Runs the job to its receipt. Never throws for a failed job: whatever stops it before Runner writes result.json is written
    /// as a `failed` receipt here, because a waiter polls for that file and a job left `pending` is waited on for ever.
    /// <c>Refused</c> is a `.claustrum` link that leaves the repo (exit 2 in-process too).
    /// </summary>
    public static async Task<(RunResult Result, bool Refused)> RunAsync(JobPaths job, CancellationToken signalled)
    {
        using CancellationTokenSource cancel = CancellationTokenSource.CreateLinkedTokenSource(signalled);
        using CancellationTokenSource finished = new();
        Task watcher = WatchForCancelAsync(job, cancel, finished.Token);
        DelegateRequest? request = null;
        ResolvedRole? role = null;
        try
        {
            request = JsonSerializer.Deserialize(File.ReadAllText(job.HostRequestJson), DelegationJsonContext.Default.DelegateRequest)
                ?? throw new IOException($"{job.HostRequestJson} is empty");

            PreparedDelegation prepared = DelegateEngine.Prepare(request);
            role = prepared.Role;
            return (await DelegateEngine.RunAsync(prepared, job, cancel.Token), false);
        }
        catch (MachineryRefusedException ex)
        {
            return (ex.Receipt, true);
        }
        catch (Exception ex)
        {
            RunStatus status = cancel.IsCancellationRequested && ex is OperationCanceledException ? RunStatus.Cancelled : RunStatus.Failed;
            return (await ReceiptForAsync(job, role ?? PlaceholderRole(request), status, ex.Message), false);
        }
        finally
        {
            await finished.CancelAsync();
            await watcher;
        }
    }

    // The marker is polled, not signalled: a Windows process with no console cannot be sent Ctrl-C, and one poll serves both systems.
    private static async Task WatchForCancelAsync(JobPaths job, CancellationTokenSource cancel, CancellationToken stop)
    {
        using PeriodicTimer timer = new(TimeSpan.FromMilliseconds(CancelPollMs));
        try
        {
            do
            {
                if (File.Exists(job.CancelRequest))
                {
                    await cancel.CancelAsync();
                    return;
                }
            }
            while (await timer.WaitForNextTickAsync(stop));
        }
        catch (OperationCanceledException)
        {
        }
    }

    // A receipt Runner already wrote stands: this only fills the gap when it never got that far.
    private static async Task<RunResult> ReceiptForAsync(JobPaths job, ResolvedRole role, RunStatus status, string error) =>
        File.Exists(job.ResultJson) && JsonSerializer.Deserialize(await File.ReadAllTextAsync(job.ResultJson), ClaustrumJsonContext.Default.RunResult) is { } written
            ? written
            : await Runner.RefuseAsync(job, role, status, error);

    // Prepare resolves the real role; when it is the thing that failed (a claustrum.json edited since the parent prepared, an
    // unreadable host-request.json) the receipt still needs a name, a backend and a model to be a RunResult.
    private static ResolvedRole PlaceholderRole(DelegateRequest? request) => new(
        Name: request?.Role ?? "unknown",
        SystemPrompt: "",
        Backend: request?.Overrides.Backend ?? "unknown",
        Model: request?.Overrides.Model ?? "unknown",
        Effort: "",
        Permission: new PermissionPolicy(PermissionLevel.ReadOnly, []),
        Blind: false,
        HasReport: false);
}
