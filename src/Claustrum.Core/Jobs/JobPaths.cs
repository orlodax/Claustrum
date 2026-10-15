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

    // A detached job (`run --detach`, #124 stage 2) is run by a `claustrum jobs host` process of its own: what it is to run,
    // who it is, where its console output goes, and the marker `jobs cancel` leaves for it.
    public string HostRequestJson => Path.Combine(Directory, "host-request.json");

    public string HostJson => Path.Combine(Directory, "host.json");

    public string HostLog => Path.Combine(Directory, "host.log");

    public string CancelRequest => Path.Combine(Directory, "cancel");
}
