using System.CommandLine;
using Claustrum.Core.Jobs;
using Claustrum.Core.Model;
using Claustrum.Delegation;

namespace Claustrum.Cli;

// #124 stage 2, hidden: `claustrum jobs host <id>` runs a job another process minted (`run --detach`, or a tree's `run`), so a live
// session's stdin pipe belongs to a process that outlives its starter (NOTES.md "Detached hosts and tree routing"). Not a verb to
// type: the exit code is the job's, as `run` would have ended with, and everything it prints is in the job's host.log.
public static class JobsHostCommand
{
    public static Command Build()
    {
        Argument<string> id = new("id") { Description = "Job id." };
        Command host = new("host", "Run a minted job in this process, detached from its starter (started by `run --detach`; not for direct use).") { Hidden = true };
        host.Arguments.Add(id);
        host.SetAction(async parseResult => await ExecuteAsync(parseResult.GetRequiredValue(id)));
        return host;
    }

    private static async Task<int> ExecuteAsync(string id)
    {
        JobPaths job = JobDirectory.Open(AppServices.Platform, id)
            ?? throw new CliUsageException($"job '{id}' not found under {JobDirectory.ResolveRoot(AppServices.Platform)}");

        HostDetach.Apply(job.HostLog);

        // The first SIGTERM or SIGHUP cancels the job with its receipt, as for `run`; `jobs cancel` leaves a marker instead.
        using CancelSignalHandler signals = new();
        (RunResult result, bool refused) = await DetachedHost.RunAsync(job, signals.Token);
        signals.Settled();
        return refused ? ExitCodes.Usage : RunCommand.ExitCodeFor(result.Status);
    }
}
