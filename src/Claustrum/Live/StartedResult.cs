namespace Claustrum.Live;

// `run --detach --json`'s one document: the job is minted and its host started.
public sealed record StartedResult(string Status, string JobId)
{
    public static StartedResult For(string jobId) => new("started", jobId);
}
