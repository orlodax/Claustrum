using System.Text.Json;
using Claustrum.Core.Backends;
using Claustrum.Core.Backends.Acp;
using Claustrum.Core.Jobs;
using Claustrum.Core.Model;
using Claustrum.Core.Tests.Testing;

namespace Claustrum.Core.Tests.Backends.Acp;

/// <summary>
/// The ACP tests' shared kit: the recorded transcripts under <c>fixtures/acp</c> (opencode 2.0.30, 2026-10-16: tests/fixtures/acp/README.md
/// says how each was recorded), the backend under test, and builders for the agent's frames, so a synthetic transcript reads as the
/// frames it holds. Frames are built from anonymous objects, which the test project may serialize by reflection.
/// </summary>
internal static class AcpLines
{
    public const string Session = "ses_test";

    public static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "acp", name));

    public static string[] FixtureLines(string name) => File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "fixtures", "acp", name));

    /// <summary>The first <paramref name="count"/> lines of a fixture: a recording cut off where the process was killed.</summary>
    public static string Head(string name, int count) => string.Join('\n', FixtureLines(name).Take(count));

    public static AcpBackend Backend(FakePlatform? platform = null)
    {
        FakePlatform used = platform ?? new FakePlatform();
        return new AcpBackend(used, new OpencodeAcpHarness(used));
    }

    public static ResolvedRun Run(
        string model = "opencode/fake",
        string role = "builder",
        PermissionPolicy? permission = null,
        string? resume = null,
        string brief = "do the thing",
        Dictionary<string, string>? env = null,
        string cwd = "/repo") => new(
            Role: new ResolvedRole(role, "system body", "acp-opencode", model, "high", permission ?? new PermissionPolicy(PermissionLevel.EditShell, []), Blind: false, HasReport: true),
            Brief: brief,
            Cwd: cwd,
            BudgetUsd: null,
            ResumeSession: resume,
            AttachFiles: [],
            Stream: false,
            SystemPromptFilePath: "/job/system.md",
            JobDirectory: "/job",
            Env: env ?? []);

    public static string Join(params string[] lines) => string.Join('\n', lines);

    public static JobPaths JobIn(string directory) => new(
        "20261016-000000-00000000",
        directory,
        Path.Combine(directory, "request.json"),
        Path.Combine(directory, "system.md"),
        Path.Combine(directory, "stdout.log"),
        Path.Combine(directory, "stderr.log"),
        Path.Combine(directory, "result.json"));

    // ---- the agent's frames ---------------------------------------------------------------------------------

    public static string Result(string id, object result) => Frame(new { jsonrpc = "2.0", id, result });

    public static string Failure(string id, int code, string message, object? data = null) =>
        Frame(data is null ? new { jsonrpc = "2.0", id, error = new { code, message } } : new { jsonrpc = "2.0", id, error = new { code, message, data } });

    public static string Initialized(string id = "init-1") => Result(id, new { protocolVersion = 1, agentCapabilities = new { loadSession = true } });

    public static string Opened(string id = "new-2", string session = Session) => Result(id, new { sessionId = session, configOptions = Array.Empty<object>() });

    public static string Configured(string id) => Result(id, new { configOptions = Array.Empty<object>() });

    public static string Loaded(string id = "load-2") => Result(id, new { configOptions = Array.Empty<object>() });

    /// <summary>init, new, model, mode: the handshake of a run that was not told to resume.</summary>
    public static string[] Handshake(string session = Session) => [Initialized(), Opened(session: session), Configured("config-3"), Configured("config-4")];

    public static string Answered(string id, string stopReason = "end_turn", object? usage = null) =>
        Result(id, new { stopReason, usage, _meta = new { } });

    public static object Tokens(int? input = null, int? output = null, int? cachedRead = null, int? cachedWrite = null)
    {
        Dictionary<string, object> usage = [];
        if (input is { } i)
            usage["inputTokens"] = i;
        if (output is { } o)
            usage["outputTokens"] = o;
        if (cachedRead is { } r)
            usage["cachedReadTokens"] = r;
        if (cachedWrite is { } w)
            usage["cachedWriteTokens"] = w;

        return usage;
    }

    public static string Message(string text, string messageId = "msg_1", string session = Session) =>
        Update(session, new { sessionUpdate = "agent_message_chunk", messageId, content = new { type = "text", text } });

    public static string Thought(string text, string messageId = "msg_1:reasoning:0", string session = Session) =>
        Update(session, new { sessionUpdate = "agent_thought_chunk", messageId, content = new { type = "text", text } });

    public static string User(string text, string session = Session) =>
        Update(session, new { sessionUpdate = "user_message_chunk", messageId = "msg_u", content = new { type = "text", text } });

    public static string Cost(decimal amount, string currency = "USD", string session = Session) =>
        Update(session, new { sessionUpdate = "usage_update", used = 100, size = 200000, cost = new { amount, currency } });

    public static string Plan(string session = Session) => Update(session, new { sessionUpdate = "plan", entries = Array.Empty<object>() });

    public static string Commands(string session = Session) =>
        Update(session, new { sessionUpdate = "available_commands_update", availableCommands = Array.Empty<object>() });

    /// <summary>A `tool_call` or `tool_call_update`; absent properties are left out, as the agent leaves them out.</summary>
    public static string Tool(
        string callId,
        string? status = null,
        string kind = "tool_call",
        string? name = null,
        string? toolKind = null,
        string? title = null,
        string? command = null,
        string? filePath = null,
        string? pattern = null,
        string? error = null,
        string session = Session)
    {
        Dictionary<string, object> update = new() { ["sessionUpdate"] = kind, ["toolCallId"] = callId };
        if (name is not null)
            update["name"] = name;
        if (toolKind is not null)
            update["kind"] = toolKind;
        if (title is not null)
            update["title"] = title;
        if (status is not null)
            update["status"] = status;

        Dictionary<string, object> input = [];
        if (command is not null)
            input["command"] = command;
        if (filePath is not null)
            input["filePath"] = filePath;
        if (pattern is not null)
            input["pattern"] = pattern;
        if (input.Count > 0)
            update["rawInput"] = input;

        if (error is not null)
            update["rawOutput"] = new { error };

        return Update(session, update);
    }

    public static string Update(string session, object update) => Frame(new { jsonrpc = "2.0", method = "session/update", @params = new { sessionId = session, update } });

    public static string Frame(object value) => JsonSerializer.Serialize(value);

    // ---- reading a transcript the way an independent observer would -------------------------------------------

    /// <summary>The text of every <c>agent_message_chunk</c> in the first <paramref name="take"/> lines of a fixture, glued: what a one-turn transcript's reply is.</summary>
    public static string MessageText(string name, int take = int.MaxValue) => string.Concat(FixtureLines(name)
        .Take(take)
        .Select(line => JsonDocument.Parse(line).RootElement)
        .Where(frame => frame.TryGetProperty("params", out JsonElement parameters) && parameters.TryGetProperty("update", out JsonElement update)
            && update.GetProperty("sessionUpdate").GetString() == "agent_message_chunk")
        .Select(frame => frame.GetProperty("params").GetProperty("update").GetProperty("content").GetProperty("text").GetString()));
}
