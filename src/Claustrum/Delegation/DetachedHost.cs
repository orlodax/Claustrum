using System.Text.Json;
using Claustrum.Cli;
using Claustrum.Core;
using Claustrum.Core.Jobs;
using Claustrum.Core.Json;
using Claustrum.Core.Model;
using Claustrum.Live;

namespace Claustrum.Delegation;

// #124 stage 2: the body of `claustrum jobs host <id>`. Runs the job another process minted, from the request it left in the
// job directory, exactly as that process would have in-process: the same Prepare, the same DelegateEngine.RunAsync (slot, worktree,
// budget admission, Runner). What differs is who can cancel it: a signal, the marker `jobs cancel` leaves, or the end of the
// coordinate job it belongs to (TreeRootWatch). A cast's fallback (#126) makes the host run a second job after the first: the marker
// that counts is the one of whichever job is running (NOTES.md "A cast can name a fallback model").
internal static class DetachedHost
{
    public const string RootEndedNotice = "The coordinate job that started this one has ended; this job was cancelled.";

    private const int CancelPollMs = 250;

    // The root is checked every twelve marker polls (3 s), the first time at once: a host started under a root already gone ends at the start.
    private const int RootCheckEveryPolls = 12;

    // How long the Notice is given to reach conversation.jsonl before the cancel (the session reads its inbox every 500 ms).
    private static readonly TimeSpan noticeWait = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Runs the job to its receipt. Never throws for a failed job: whatever stops it before Runner writes result.json is written
    /// as a `failed` receipt here, because a waiter polls for that file and a job left `pending` is waited on for ever.
    /// <c>Refused</c> is a `.claustrum` link that leaves the repo (exit 2 in-process too).
    /// </summary>
    public static async Task<(RunResult Result, bool Refused)> RunAsync(JobPaths job, CancellationToken signalled)
    {
        using CancellationTokenSource cancel = CancellationTokenSource.CreateLinkedTokenSource(signalled);
        using CancellationTokenSource finished = new();
        RunningJob running = new(job);
        Task watcher = WatchForCancelAsync(job, running, cancel, finished.Token);
        DelegateRequest? request = null;
        ResolvedRole? role = null;
        try
        {
            request = JsonSerializer.Deserialize(File.ReadAllText(job.HostRequestJson), DelegationJsonContext.Default.DelegateRequest)
                ?? throw new IOException($"{job.HostRequestJson} is empty");

            PreparedDelegation prepared = DelegateEngine.Prepare(request);
            role = prepared.Role;
            return (await DelegateEngine.RunAsync(prepared, job, (retry, retryJob) =>
            {
                role = retry.Role;
                running.Job = retryJob;
            }, cancel.Token), false);
        }
        catch (MachineryRefusedException ex)
        {
            return (ex.Receipt, true);
        }
        catch (Exception ex)
        {
            // The job that was running when it threw, so a retry that fails outside Runner gets its receipt too, not the first's.
            RunStatus status = cancel.IsCancellationRequested && ex is OperationCanceledException ? RunStatus.Cancelled : RunStatus.Failed;
            return (await ReceiptForAsync(running.Job, role ?? PlaceholderRole(request), status, ex.Message), false);
        }
        finally
        {
            await finished.CancelAsync();
            await watcher;
        }
    }

    // The job the host is running now: the one it was started for, then its retry once the cast's fallback took over (#126). Set by the
    // run, read by the watcher on its own thread.
    private sealed class RunningJob(JobPaths first)
    {
        public volatile JobPaths Job = first;
    }

    // The marker is polled, not signalled: a Windows process with no console cannot be sent Ctrl-C, and one poll serves both systems.
    // The first job's marker and the running job's are both honoured (they are one while the first runs); the root of the job's tree
    // is polled on the same timer, a twelfth as often.
    private static async Task WatchForCancelAsync(JobPaths job, RunningJob running, CancellationTokenSource cancel, CancellationToken stop)
    {
        TreeRootWatch? tree = TreeRootWatch.ForEnvironment();
        using PeriodicTimer timer = new(TimeSpan.FromMilliseconds(CancelPollMs));
        try
        {
            for (int poll = 0; ; poll++)
            {
                if (File.Exists(job.CancelRequest) || File.Exists(running.Job.CancelRequest))
                {
                    await cancel.CancelAsync();
                    return;
                }

                if (tree is not null && poll % RootCheckEveryPolls == 0 && tree.HasEnded())
                {
                    await CancelForEndedRootAsync(running.Job, cancel, stop);
                    return;
                }

                if (!await timer.WaitForNextTickAsync(stop))
                    return;
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    // The Notice first, so the conversation says why; then the cancel, which ends the job with its receipt like any other.
    private static async Task CancelForEndedRootAsync(JobPaths job, CancellationTokenSource cancel, CancellationToken stop)
    {
        SafeConsole.Error(RootEndedNotice);
        try
        {
            if (LiveJob.At(job).Notify(RootEndedNotice) is { } inboxFile)
            {
                // The session deletes the file once the notice is recorded; a session that cannot (or a job that ends first) costs the wait, not the cancel.
                DateTime due = DateTime.UtcNow + noticeWait;
                while (File.Exists(inboxFile) && DateTime.UtcNow < due)
                    await Task.Delay(100, stop);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CliUsageException)
        {
            SafeConsole.Error($"warning: the notice could not be recorded: {ex.Message}");
        }

        await cancel.CancelAsync();
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
