using Claustrum.Core.Model;

namespace Claustrum.Core.Backends;

// Everything IBackend.Build needs beyond ResolvedRole: the actual brief text, job-scoped paths
// already written to disk (SystemPromptFilePath), and the run-level knobs from RunRequest.
public sealed record ResolvedRun(
    ResolvedRole Role,
    string Brief,
    string Cwd,
    decimal? BudgetUsd,
    string? ResumeSession,
    string[] AttachFiles,
    bool Stream,
    string SystemPromptFilePath,
    string JobDirectory);
