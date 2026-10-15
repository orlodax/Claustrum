namespace Claustrum.Core.Jobs;

public sealed record JobPaths(string Id, string Directory, string RequestJson, string SystemMd, string StdoutLog, string StderrLog, string ResultJson)
{
    // Written by Runner when the job starts: records the resolved backend name and model id so
    // `jobs follow` can pick the right stream formatter while the job runs (issue #123).
    public string BackendJson => Path.Combine(Directory, "backend.json");

    // A live job's conversation with its human (#124, Claustrum.Core.Live): the append-only log, the state
    // `jobs wait` and job_status read, and the folder where `jobs say` drops messages.
    public string ConversationJsonl => Path.Combine(Directory, "conversation.jsonl");

    public string LiveJson => Path.Combine(Directory, "live.json");

    public string InboxDirectory => Path.Combine(Directory, "inbox");
}
