namespace Claustrum.Core.Jobs;

public sealed record JobPaths(string Id, string Directory, string RequestJson, string SystemMd, string StdoutLog, string StderrLog, string ResultJson);
