using Claustrum.Live;

namespace Claustrum.Cli;

// #126: a job its cast's fallback took over is no longer the one to talk to. The verbs that address a running job by id follow its receipt's
// retried_as to the retry (one hop: a retry is never retried) and say so on stderr, where stdout stays the answer.
internal static class JobRetry
{
    public static LiveJob Follow(LiveJob job)
    {
        LiveJob current = job.Current;
        if (current != job)
            SafeConsole.Error(Note(job.Id, current.Id));

        return current;
    }

    public static string Note(string from, string to) => $"job {from} was retried on its cast's fallback as {to}; going on with {to}";
}
