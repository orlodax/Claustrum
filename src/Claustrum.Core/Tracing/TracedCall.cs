namespace Claustrum.Core.Tracing;

/// <summary>
/// One tool call as the backend's own stream recorded it (#145). <see cref="Path"/> is what a read, search or edit targeted, and a shell
/// command's working directory. <see cref="Error"/> is set only when the call failed; <see cref="Output"/> only for a shell command, since
/// any other tool's output is file or page content. <see cref="Line"/> is the 1-based stdout.log line that carried the call's result.
/// </summary>
public sealed record TracedCall(
    string Name,
    ToolFamily Family,
    string? Command,
    string? Path,
    string? Url,
    bool Failed,
    string? Error,
    string? Output,
    int Line);
