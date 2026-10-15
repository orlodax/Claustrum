using Claustrum.Cli;
using Claustrum.Core.Jobs;
using Claustrum.Live;

namespace Claustrum.Coordination;

// #124 stage 2 review F1 (NOTES.md "Detached hosts and tree routing"): a tree's detached children are not the architect's process
// children, so when `coordinate` ends (a receipt, a cancel, a timeout) it asks every one of them still running to stop. They are
// found in the job store by the CLAUSTRUM_PARENT_JOB their host.json recorded, never by what this process remembers: a child
// started by an architect that has been resumed, or by a second shell in the same tree, is in the store all the same.
internal static class TreeSettler
{
    /// <summary>
    /// Writes the cancel marker of every detached job of tree <paramref name="treeId"/> that has no receipt and whose host is alive, and
    /// returns their ids. The hosts end those jobs `cancelled` on their own time (a poll of 250 ms, then the run's usual cancel); this does
    /// not wait for them. A job that finishes or whose host dies meanwhile is simply not listed. Never throws.
    /// </summary>
    public static string[] CancelRemaining(string treeId)
    {
        List<string> asked = [];
        try
        {
            string root = JobDirectory.ResolveRoot(AppServices.Platform);
            if (!Directory.Exists(root))
                return [];

            foreach (string directory in Directory.EnumerateDirectories(root).OrderBy(path => path, StringComparer.Ordinal))
            {
                if (LiveJob.Find(Path.GetFileName(directory)) is not { HasResult: false } job || job.ReadHost() is not { } host || host.Tree != treeId)
                    continue;

                try
                {
                    job.RequestCancel();
                    asked.Add(job.Id);
                }
                catch (Exception ex) when (ex is CliUsageException or IOException or UnauthorizedAccessException)
                {
                    // Done, or its host is gone meanwhile: nothing to stop.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A store that cannot be listed costs the sweep; the hosts' own watch of their root is the other end.
        }

        return [.. asked];
    }

    public static string WarningFor(string jobId) =>
        $"detached job {jobId} of this tree was still running when the coordinate job ended: cancelled";
}
