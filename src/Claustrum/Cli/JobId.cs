using Claustrum.Core.Jobs;

namespace Claustrum.Cli;

// Every verb that takes a job id from the user checks its shape first (review of #124 stage 2, F6): the id becomes a directory
// under the jobs store, and on Windows a drive-qualified `C:x` would replace the store in Path.Combine. Exit 2, like an unknown job.
internal static class JobId
{
    public static string Require(string id) =>
        JobDirectory.IsJobId(id)
            ? id
            : throw new CliUsageException($"'{id}' is not a job id: expected yyyyMMdd-HHmmss-xxxxxxxx, as `claustrum jobs list` prints them");
}
