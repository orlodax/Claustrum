using Claustrum.Core.Model;

namespace Claustrum.Core.Backends;

// Everything IBackend.Build needs beyond ResolvedRole: the actual brief text, job-scoped paths
// already written to disk (SystemPromptFilePath), and the run-level knobs from RunRequest. Env is
// RunRequest.Env passed through so a backend's ProcessSpec.Env carries caller `--env` entries
// (Process/EnvAllowList.cs "Env allow-list is dead" fix).
public sealed record ResolvedRun(
    ResolvedRole Role,
    string Brief,
    string Cwd,
    decimal? BudgetUsd,
    string? ResumeSession,
    string[] AttachFiles,
    bool Stream,
    string SystemPromptFilePath,
    string JobDirectory,
    Dictionary<string, string> Env,
    // #124: the run is a live session (the backend has a LiveProtocol and Runner is feeding it): the brief
    // goes in as the first message of a conversation. A backend that resumes per turn is built again for each
    // next turn, with the message as Brief and the session as ResumeSession, and Live still true.
    bool Live = false);
