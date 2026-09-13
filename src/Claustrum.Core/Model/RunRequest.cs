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
    bool Stream);
