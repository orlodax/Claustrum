using System.Text.Json;
using Claustrum.Core.Backends;
using Claustrum.Core.Backends.Acp;
using Claustrum.Core.Backends.Claude;
using Claustrum.Core.Backends.Opencode;
using Claustrum.Core.Model;
using Claustrum.Core.Report;
using Claustrum.Core.Tracing;

namespace Claustrum.Core.Tests.Testing;

/// <summary>
/// Synthetic <c>stdout.log</c> text in the shapes the traced backends really write (claude stream-json, opencode's
/// <c>tool_use</c> events, ACP <c>session/update</c> frames), run through the backend's own <see cref="IBackend.Trace"/> and
/// <see cref="EnvironmentClassifier"/>: a case is a few commands and what they printed, and the whole path from log line to
/// <c>environment_issues</c> is exercised. The shapes are those of recorded opencode 2.0.30 and claude 2.1.x jobs (2026-10-29);
/// the content is invented. Line numbers: claude puts the tool_result of call <c>i</c> on line <c>3 + 2i</c> (the init is line 1);
/// ACP puts the final update of call <c>i</c> on line <c>5 + 3i</c> (init, session/new, then three updates per call).
/// </summary>
public static class RunLogs
{
    public const string Cwd = "/work/repo";

    /// <summary>
    /// One tool call. <paramref name="Name"/> is claude's tool name, opencode's tool name, or for ACP the call's <c>kind</c>
    /// (<c>shell</c> being an <c>execute</c>). <paramref name="Exit"/> is the status ACP and opencode report for a shell call.
    /// </summary>
    public sealed record ToolCall(string Name, object Input, string Output = "", bool IsError = false, int? Exit = null, string? Title = null);

    /// <summary>A claude <c>Bash</c> call and what it printed; <paramref name="isError"/> is the tool_result's <c>is_error</c>.</summary>
    public static ToolCall Bash(string command, string output = "", bool isError = false) => new("Bash", new { command }, output, isError);

    /// <summary>An ACP or opencode shell call that ran: its output and exit status.</summary>
    public static ToolCall Execute(string command, string output = "", int exit = 0) => new("shell", new { command, cwd = Cwd }, output, Exit: exit, Title: command);

    /// <summary>A failed ACP or opencode call of <paramref name="kind"/> (<c>read</c>, <c>search</c>, <c>fetch</c>, <c>edit</c>, <c>other</c>) whose whole error is <paramref name="error"/>.</summary>
    public static ToolCall Failed(string kind, object input, string error) => new(kind, input, error, IsError: true, Title: kind);

    public static string Claude(string permissionMode, params ToolCall[] steps) => ClaudeWithDenials(permissionMode, [], steps);

    public static string ClaudeWithDenials(string permissionMode, object[] denials, params ToolCall[] steps)
    {
        List<string> lines = [Json(new { type = "system", subtype = "init", permissionMode, session_id = "s1" })];
        for (int index = 0; index < steps.Length; index++)
        {
            string id = $"toolu_{index:0000}";
            lines.Add(Json(new { type = "assistant", message = new { content = new[] { new { type = "tool_use", id, name = steps[index].Name, input = steps[index].Input } } } }));
            lines.Add(Json(new { type = "user", message = new { content = new[] { new { type = "tool_result", tool_use_id = id, content = steps[index].Output, is_error = steps[index].IsError } } } }));
        }

        lines.Add(Json(new { type = "result", subtype = "success", is_error = false, result = "done", session_id = "s1", total_cost_usd = 0.01, permission_denials = denials }));
        return string.Join('\n', lines) + "\n";
    }

    public static string Acp(params ToolCall[] steps)
    {
        List<string> lines =
        [
            Json(new { jsonrpc = "2.0", id = "init-1", result = new { protocolVersion = 1 } }),
            Json(new { jsonrpc = "2.0", id = "new-2", result = new { sessionId = "ses_1" } }),
        ];
        for (int index = 0; index < steps.Length; index++)
        {
            ToolCall step = steps[index];
            bool shell = step.Name == "shell";
            string kind = shell ? "execute" : step.Name;
            string id = $"call_{index}";
            string title = step.Title ?? kind;
            object[] locations = shell ? [new { path = Cwd }] : [];
            lines.Add(Update(new { sessionUpdate = "tool_call", toolCallId = id, name = shell ? "shell" : kind, title, kind, status = "pending", locations = Array.Empty<object>(), rawInput = new { } }));
            lines.Add(Update(new { sessionUpdate = "tool_call_update", toolCallId = id, status = "in_progress", kind, title, locations, rawInput = step.Input }));
            lines.Add(step.IsError
                ? Update(new { sessionUpdate = "tool_call_update", toolCallId = id, status = "failed", kind, title, locations, rawInput = step.Input, content = Text(step.Output), rawOutput = new { metadata = new { }, error = step.Output } })
                : Update(new { sessionUpdate = "tool_call_update", toolCallId = id, status = "completed", locations, content = Text(step.Output), rawOutput = new { metadata = new { status = "completed", truncated = false, exit = step.Exit ?? 0 } } }));
        }

        lines.Add(Json(new { jsonrpc = "2.0", id = "prompt-3", result = new { stopReason = "end_turn" } }));
        return string.Join('\n', lines) + "\n";
    }

    /// <summary>A native <c>opencode run --format json</c> log: one <c>tool_use</c> event per call (<c>bash</c> for a shell one).</summary>
    public static string Opencode(params ToolCall[] steps)
    {
        List<string> lines = [];
        for (int index = 0; index < steps.Length; index++)
        {
            ToolCall step = steps[index];
            object state = step.IsError
                ? new { status = "error", input = step.Input, error = step.Output }
                : new { status = "completed", input = step.Input, output = step.Output, metadata = new { exit = step.Exit ?? 0 } };
            lines.Add(Json(new { type = "tool_use", part = new { id = $"prt_{index}", tool = step.Name == "shell" ? "bash" : step.Name, state } }));
        }

        return string.Join('\n', lines) + "\n";
    }

    /// <summary>The environment issues of <paramref name="stdout"/> read by <paramref name="backend"/>, as Runner reads them.</summary>
    public static EnvironmentIssue[] Classify(IBackend backend, string stdout, PermissionLevel level, string? report = null, string? cwd = null, string[]? readDirs = null, string[]? deny = null)
    {
        ClaustrumReport? parsed = report is null ? null : ReportExtractor.Extract($"```claustrum-report\n{report}\n```").Report;
        return EnvironmentClassifier.Classify(backend.Trace(stdout), new PermissionPolicy(level, deny ?? []), parsed, cwd ?? Cwd, readDirs);
    }

    public static EnvironmentIssue[] ClassifyClaude(string stdout, PermissionLevel level = PermissionLevel.EditShell, string? report = null, string? cwd = null, string[]? readDirs = null, string[]? deny = null) =>
        Classify(new ClaudeBackend(new FakePlatform()), stdout, level, report, cwd, readDirs, deny);

    public static EnvironmentIssue[] ClassifyAcp(string stdout, PermissionLevel level = PermissionLevel.EditShell, string? report = null, string? cwd = null, string[]? readDirs = null, string[]? deny = null) =>
        Classify(new AcpBackend(new FakePlatform(), new OpencodeAcpHarness(new FakePlatform())), stdout, level, report, cwd, readDirs, deny);

    public static EnvironmentIssue[] ClassifyOpencode(string stdout, PermissionLevel level = PermissionLevel.EditShell, string? report = null, string? cwd = null, string[]? readDirs = null) =>
        Classify(new OpencodeBackend(new FakePlatform()), stdout, level, report, cwd, readDirs);

    public static string Json(object value) => JsonSerializer.Serialize(value);

    private static string Update(object update) => Json(new { jsonrpc = "2.0", method = "session/update", @params = new { sessionId = "ses_1", update } });

    private static object[] Text(string text) => [new { type = "content", content = new { type = "text", text } }];
}
