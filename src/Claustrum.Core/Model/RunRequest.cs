namespace Claustrum.Core.Model;

public sealed record RunRequest(
    string Role,
    string? Brief,
    string? BriefFile,
    string Cwd,
    string? Backend,
    string? Model,
    string? Effort,
    PermissionPolicy? Permission,
    decimal? BudgetUsd,
    TimeSpan? Timeout,
    string? ResumeSession,
    string[] AttachFiles,
    Dictionary<string, string> Env,
    bool Stream,
    // MCP-originated attachments are read as regular files only: a FIFO or a device among them would block a
    // server call (2026-10-14, found by review). The CLI's --file stays a plain read, so `<(cmd)` works there.
    bool AttachmentsMustBeRegular = false);
