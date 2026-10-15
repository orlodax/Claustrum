namespace Claustrum.Core.Jobs;

public sealed record JobPaths(string Id, string Directory, string RequestJson, string SystemMd, string StdoutLog, string StderrLog, string ResultJson)
{
    // Written by Runner when the job starts: records the resolved backend name and model id so
    // `jobs follow` can pick the right stream formatter while the job runs (issue #123).
    public string BackendJson => Path.Combine(Directory, "backend.json");
}
