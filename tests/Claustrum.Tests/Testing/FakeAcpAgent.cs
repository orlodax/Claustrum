using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Claustrum.Tests.Testing;

/// <summary>One tool call of a <see cref="FakeAcpTurn"/>: a `tool_call` (pending), its `in_progress` update carrying the command, and the closing update.</summary>
/// <param name="Name">opencode's tool name (`shell`, `write`): sent once, on the first `tool_call`, as the real agent does.</param>
/// <param name="Command">What the call runs, as `rawInput.command`.</param>
/// <param name="Status">The closing status: `completed`, or `failed` (then <paramref name="Error"/> is its `rawOutput.error`).</param>
/// <param name="Kind">ACP's tool kind (`execute`, `edit`).</param>
/// <param name="Error">The `rawOutput.error` of a failed call.</param>
public sealed record FakeAcpTool(string Name, string Command, string Status = "completed", string Kind = "execute", string? Error = null);

/// <summary>A request the fake agent sends the client mid-turn, whose answer line it reads and records (<see cref="FakeAcpAgent.Answers"/>).</summary>
public sealed record FakeAcpRequest(string Method, string ParamsJson)
{
    /// <summary>A `session/request_permission` for <paramref name="command"/> offering one option per kind (ids are the kinds themselves).</summary>
    public static FakeAcpRequest Permission(string command, params string[] optionKinds)
    {
        string[] kinds = optionKinds.Length > 0 ? optionKinds : ["allow_once", "allow_always", "reject_once"];
        string options = string.Join(',', kinds.Select(kind => "{\"optionId\":\"" + kind + "\",\"kind\":\"" + kind + "\",\"name\":\"" + kind + "\"}"));
        return new FakeAcpRequest(
            "session/request_permission",
            "{\"sessionId\":\"@SESSION@\",\"toolCall\":{\"toolCallId\":\"call_perm\",\"name\":\"shell\",\"title\":\"shell\",\"kind\":\"execute\",\"status\":\"pending\",\"rawInput\":{\"command\":"
            + JsonSerializer.Serialize(command) + "}},\"options\":[" + options + "]}");
    }

    /// <summary>A permission request that offers no option at all.</summary>
    public static FakeAcpRequest PermissionWithNoOptions(string command) => new(
        "session/request_permission",
        "{\"sessionId\":\"@SESSION@\",\"toolCall\":{\"toolCallId\":\"call_perm\",\"title\":\"shell\",\"rawInput\":{\"command\":" + JsonSerializer.Serialize(command) + "}},\"options\":[]}");
}

/// <summary>A JSON-RPC error answer: a prompt's, or (via <see cref="FakeAcpAgent.Refuse"/>) a handshake step's.</summary>
public sealed record FakeAcpError(int Code, string Message, string? ErrorName = null, string? ModelId = null);

/// <summary>
/// One `session/prompt` the fake agent answers: the same turn number as the prompt (the last turn repeats once the list runs out).
/// <see cref="Text"/> is sent as two `agent_message_chunk`s unless <see cref="Chunks"/> says otherwise.
/// </summary>
public sealed record FakeAcpTurn(
    string Text,
    string? Summary = null,          // set: the reply ends with a `claustrum-report` fence carrying it (the role is done); null, with no ReportJson, is a question
    string? ReportJson = null,       // the exact JSON of the fence (a `needs_input` report), instead of Summary's
    int SleepSeconds = 0,            // before the agent says anything: a message that arrives meanwhile is mid-turn
    int StallSeconds = 0,            // after its last update and before it answers: a timeout lands on a pending text with a tool in flight
    decimal CostUsd = 0.01m,         // what the turn adds to the session's running total, which `usage_update` and `session export` report
    string StopReason = "end_turn",
    FakeAcpError? Error = null,      // answer the prompt with this JSON-RPC error instead of a result
    bool Priced = true,              // whether the turn sends its `usage_update` (a turn that failed part-way may not)
    string[]? Chunks = null,         // the `agent_message_chunk` texts as sent; null splits Text (and its fence) in two
    FakeAcpTool[]? Tools = null,     // tool calls made before the agent speaks
    FakeAcpRequest[]? Requests = null, // sent to the client once the tools run; each is answered by the client before the turn goes on
    int ExitCode = 0,                // non-zero: the agent dies on this prompt (stderr "boom") without answering it
    int InputTokens = 10,
    int OutputTokens = 5,
    (string Path, string Content)[]? Writes = null) // files left in the agent's directory before it speaks (no single quote in either)
{
    public static FakeAcpTurn Ask(string question, int sleepSeconds = 0, decimal costUsd = 0.01m) => new(question, SleepSeconds: sleepSeconds, CostUsd: costUsd);

    public static FakeAcpTurn Report(string summary, string text = "all done", int sleepSeconds = 0, decimal costUsd = 0.01m) =>
        new(text, summary, SleepSeconds: sleepSeconds, CostUsd: costUsd);

    public static FakeAcpTurn NeedsInput(string reportJson, string text = "I need the human", decimal costUsd = 0.01m) => new(text, ReportJson: reportJson, CostUsd: costUsd);
}

/// <summary>
/// A stand-in `opencode` for the ACP backend (#130): `acp` speaks newline-delimited JSON-RPC on stdio and answers `initialize`,
/// `session/new`, `session/load` (replaying a little history first), `session/set_config_option` and each `session/prompt` with the
/// scripted turn of the same number; `session export` prints the session's running total; `--version` exits 0. One process holds the
/// whole session, unlike <see cref="FakeOpencode"/>. Every process leaves, in the script's directory: `argv-n.txt`, `pid-n.txt`,
/// `cwd-n.txt`, `config-content-n.json` and `filewatcher-n.txt` (the two environment variables of `OpencodeAcpHarness`), and for the
/// session `received.log` (every line the client sent), `methods.log`, `config-options.log` (`configId=value`), `prompt-n.txt`,
/// `answers.log` (the client's answers to the agent's requests) and `answered`. POSIX sh only.
/// </summary>
public sealed class FakeAcpAgent
{
    public const string Session = "ses_fake";

    private readonly string directory;

    /// <param name="directory">Where the script and its recordings live; it must exist.</param>
    /// <param name="turns">What each `session/prompt` is answered with.</param>
    /// <param name="startingCostUsd">The session's total before the first prompt: a resumed session already has a bill.</param>
    /// <param name="lingerAfterEofSeconds">How long the process stays up after its stdin closes: an agent that does not exit on EOF, as opencode does in 30-40 ms.</param>
    public FakeAcpAgent(string directory, FakeAcpTurn[] turns, decimal startingCostUsd = 0m, int lingerAfterEofSeconds = 0)
    {
        RequirePosixShell();
        this.directory = directory;
        Write(turns, startingCostUsd);
        Put("linger", lingerAfterEofSeconds.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Skips the case on Windows, for a fixture that builds the fake before it can call the constructor's own guard.</summary>
    public static void RequirePosixShell()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("the fake ACP agent is a POSIX sh script.");
    }

    public string ScriptPath => Path.Combine(directory, "opencode");

    /// <summary>`acp` processes started so far.</summary>
    public int Runs => ReadInt("count");

    /// <summary>The arguments process <paramref name="n"/> (1-based) was started with.</summary>
    public string[] Argv(int n) => Lines($"argv-{n}.txt");

    /// <summary>The `OPENCODE_CONFIG_CONTENT` process <paramref name="n"/> was started with.</summary>
    public string ConfigContent(int n) => Text($"config-content-{n}.json");

    /// <summary>The `OPENCODE_DISABLE_FILEWATCHER` process <paramref name="n"/> was started with ("" when unset).</summary>
    public string Filewatcher(int n) => Text($"filewatcher-{n}.txt");

    /// <summary>The working directory process <paramref name="n"/> was started in.</summary>
    public string Cwd(int n) => Text($"cwd-{n}.txt").Trim();

    /// <summary>The pid of process <paramref name="n"/>'s shell.</summary>
    public int Pid(int n) => int.Parse(Text($"pid-{n}.txt"), CultureInfo.InvariantCulture);

    /// <summary>The pid of the `sleep` the agent last waited on (a turn's delay or stall), null when it never waited.</summary>
    public int? NapPid => Text("nap-pid") is { Length: > 0 } text ? int.Parse(text, CultureInfo.InvariantCulture) : null;

    /// <summary>Every line the client wrote to the agent's stdin, in order.</summary>
    public string[] Received => Lines("received.log");

    /// <summary>The method of every request the client sent, in order.</summary>
    public string[] Methods => Lines("methods.log");

    /// <summary>Each `session/set_config_option` as `configId=value`, in the order it arrived.</summary>
    public string[] ConfigOptions => Lines("config-options.log");

    /// <summary>`session/prompt`s received so far.</summary>
    public int Prompts => Directory.GetFiles(directory, "prompt-*.txt").Length;

    /// <summary>Prompts the agent has answered.</summary>
    public int AnsweredPrompts => ReadInt("answered");

    /// <summary>The raw line of prompt <paramref name="n"/>.</summary>
    public string Prompt(int n) => Text($"prompt-{n}.txt").TrimEnd('\n');

    /// <summary>The text of prompt <paramref name="n"/>: the brief, or a human message.</summary>
    public string PromptText(int n)
    {
        using JsonDocument document = JsonDocument.Parse(Prompt(n));
        return document.RootElement.GetProperty("params").GetProperty("prompt")[0].GetProperty("text").GetString() ?? "";
    }

    /// <summary>The client's answers to the agent's own requests, raw, in order.</summary>
    public string[] Answers => Lines("answers.log");

    /// <summary>`session export` calls so far, one line of arguments each.</summary>
    public string[] ExportCalls => Lines("exports");

    public int Exports => ExportCalls.Length;

    /// <summary>Makes `session export` fail (exit 1, <paramref name="message"/> on stderr) from now on, as an opencode whose database is gone.</summary>
    public void FailExports(string message = "boom") => File.WriteAllText(Path.Combine(directory, "export-fails"), message);

    /// <summary>
    /// Answers the next <paramref name="method"/> (`initialize`, `session/new`, `session/load`, or `session/set_config_option` with
    /// <paramref name="configId"/>) with this JSON-RPC error: a refusal in the handshake.
    /// </summary>
    public void Refuse(string method, FakeAcpError error, string? configId = null)
    {
        string name = method switch
        {
            "initialize" => "refuse-initialize",
            "session/new" => "refuse-session-new",
            "session/load" => "refuse-session-load",
            "session/set_config_option" => $"refuse-config-{configId ?? throw new ArgumentException("a config option needs its id", nameof(configId))}",
            _ => throw new ArgumentException($"{method} cannot be refused", nameof(method)),
        };
        File.WriteAllText(Path.Combine(directory, name), ErrorObject(error));
    }

    /// <summary>The agent dies on receiving this handshake request (`initialize`, `session/new`, `session/load`, `session/set_config_option`): <paramref name="exitCode"/>, "crashed" on stderr, no answer.</summary>
    public void CrashOn(string method, int exitCode) => File.WriteAllText(Path.Combine(directory, $"hs-crash-{HandshakeKey(method)}"), exitCode.ToString(CultureInfo.InvariantCulture));

    /// <summary>The agent takes <paramref name="seconds"/> before it answers this handshake request: a startup that hangs.</summary>
    public void StallOn(string method, int seconds) => File.WriteAllText(Path.Combine(directory, $"hs-stall-{HandshakeKey(method)}"), seconds.ToString(CultureInfo.InvariantCulture));

    /// <summary>Lines the agent prints to stdout, not JSON-RPC, just before it answers `initialize`: a banner, a log line that escaped to the wrong stream.</summary>
    public void PrintBeforeInitialize(string text) => File.WriteAllText(Path.Combine(directory, "noise"), text.EndsWith('\n') ? text : text + "\n");

    /// <summary>The `"acp-opencode":{"path":"…"}` entry of a claustrum.json's `backends` object: how the run finds this script instead of a real opencode.</summary>
    public string BackendJson => "\"acp-opencode\":{\"path\":\"" + ScriptPath.Replace("\\", "\\\\", StringComparison.Ordinal) + "\"}";

    /// <summary>Whether the process is still running; a zombie awaiting its parent's wait counts as gone.</summary>
    public static bool IsRunning(int pid)
    {
        string stat = $"/proc/{pid}/stat";
        if (File.Exists(stat))
        {
            try
            {
                string text = File.ReadAllText(stat);
                return text[(text.LastIndexOf(')') + 2)..][0] is not ('Z' or 'X');
            }
            catch (IOException)
            {
                return false;
            }
        }

        try
        {
            using System.Diagnostics.Process process = System.Diagnostics.Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static string HandshakeKey(string method) => method switch
    {
        "initialize" => "initialize",
        "session/new" => "session-new",
        "session/load" => "session-load",
        "session/set_config_option" => "config",
        _ => throw new ArgumentException($"{method} is not a handshake request", nameof(method)),
    };

    private string Text(string name) => File.Exists(Path.Combine(directory, name)) ? File.ReadAllText(Path.Combine(directory, name)) : "";

    private string[] Lines(string name) => File.Exists(Path.Combine(directory, name)) ? File.ReadAllLines(Path.Combine(directory, name)) : [];

    private int ReadInt(string name) => Text(name) is { Length: > 0 } text ? int.Parse(text.Trim(), CultureInfo.InvariantCulture) : 0;

    // Relaxed, so non-ASCII text goes out as UTF-8 bytes the way opencode writes it, not as \uXXXX escapes.
    private static readonly JsonSerializerOptions rawUtf8 = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static string Json(string value) => JsonSerializer.Serialize(value, rawUtf8);

    private static string Money(decimal usd) => usd.ToString(CultureInfo.InvariantCulture);

    private static string ErrorObject(FakeAcpError error)
    {
        List<string> data = [];
        if (error.ErrorName is { } name)
            data.Add("\"errorName\":" + Json(name));
        if (error.ModelId is { } model)
            data.Add("\"modelId\":" + Json(model));

        return "{\"code\":" + error.Code.ToString(CultureInfo.InvariantCulture) + ",\"message\":" + Json(error.Message) + (data.Count > 0 ? ",\"data\":{" + string.Join(',', data) + "}" : "") + "}";
    }

    private void Put(string name, string content) => File.WriteAllText(Path.Combine(directory, name), content);

    private void Write(FakeAcpTurn[] turns, decimal startingCostUsd)
    {
        decimal total = startingCostUsd;
        Put("total", Money(total));
        Put("after-new", Update(/*lang=json,strict*/ """{"sessionUpdate":"available_commands_update","availableCommands":[{"name":"init","description":"guided AGENTS.md setup"}]}""") + "\n");
        Put("replay", Replay());
        for (int i = 0; i < turns.Length; i++)
        {
            FakeAcpTurn turn = turns[i];
            total += turn.CostUsd;
            string n = (i + 1).ToString(CultureInfo.InvariantCulture);
            Put($"writes-{n}", Writes(turn));
            Put($"before-{n}", Before(turn, i + 1));
            Put($"requests-{n}", turn.Requests is { } requests ? string.Join('\n', requests.Select(Request)) + "\n" : "");
            Put($"after-{n}", After(turn, i + 1, total));
            Put($"answer-{n}", Answer(turn));
            Put($"total-{n}", Money(total));
            Put($"sleep-{n}", turn.SleepSeconds.ToString(CultureInfo.InvariantCulture));
            Put($"stall-{n}", turn.StallSeconds.ToString(CultureInfo.InvariantCulture));
            Put($"exit-{n}", turn.ExitCode.ToString(CultureInfo.InvariantCulture));
        }

        Put("turns", turns.Length.ToString(CultureInfo.InvariantCulture));
        File.WriteAllText(ScriptPath, Script().Replace("\r\n", "\n", StringComparison.Ordinal));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(ScriptPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    // Every update is one `session/update` notification; @SESSION@ is the id the session was opened with (the resumed one after a load).
    private static string Update(string update) =>
        "{\"jsonrpc\":\"2.0\",\"method\":\"session/update\",\"params\":{\"sessionId\":\"@SESSION@\",\"update\":" + update + "}}";

    private static string Chunk(string kind, string messageId, string text) =>
        Update("{\"sessionUpdate\":\"" + kind + "\",\"messageId\":\"" + messageId + "\",\"content\":{\"type\":\"text\",\"text\":" + Json(text) + "}}");

    // What `session/load` replays before it answers: a user message, a thought, an agent message and a finished tool call, none of this run's.
    private static string Replay() => string.Join(
        '\n',
        Chunk("user_message_chunk", "msg_old_u", "an earlier question"),
        Chunk("agent_thought_chunk", "msg_old_a:reasoning:0", "an earlier thought"),
        Update(/*lang=json,strict*/ """{"sessionUpdate":"tool_call","toolCallId":"call_old","name":"shell","title":"shell","kind":"execute","status":"pending"}"""),
        Update(/*lang=json,strict*/ """{"sessionUpdate":"tool_call_update","toolCallId":"call_old","status":"completed"}"""),
        Chunk("agent_message_chunk", "msg_old_a", "REPLAYED-HISTORY")) + "\n";

    // A shell snippet run in the agent's own directory: `printf '%s' '<content>' > '<path>'`, so nothing here may need quoting.
    private static string Writes(FakeAcpTurn turn)
    {
        StringBuilder script = new();
        foreach ((string path, string content) in turn.Writes ?? [])
        {
            if (path.Contains('\'', StringComparison.Ordinal) || content.Contains('\'', StringComparison.Ordinal))
                throw new ArgumentException("a written file's path and content may not hold a single quote");

            script.Append("printf '%s' '").Append(content).Append("' > '").Append(path).Append("'\n");
        }

        return script.ToString();
    }

    private static string Request(FakeAcpRequest request, int index) =>
        "{\"jsonrpc\":\"2.0\",\"id\":" + (7000 + index).ToString(CultureInfo.InvariantCulture) + ",\"method\":\"" + request.Method + "\",\"params\":" + request.ParamsJson + "}";

    private static string Before(FakeAcpTurn turn, int n)
    {
        List<string> lines = [Chunk("agent_thought_chunk", $"msg_{n}:reasoning:0", $"thinking about turn {n}")];
        foreach ((FakeAcpTool tool, int index) in (turn.Tools ?? []).Select((tool, index) => (tool, index)))
        {
            string id = $"call_{n}_{index}";
            lines.Add(Update("{\"sessionUpdate\":\"tool_call\",\"toolCallId\":\"" + id + "\",\"name\":" + Json(tool.Name) + ",\"title\":" + Json(tool.Name) + ",\"kind\":\"" + tool.Kind + "\",\"status\":\"pending\"}"));
            lines.Add(Update("{\"sessionUpdate\":\"tool_call_update\",\"toolCallId\":\"" + id + "\",\"status\":\"in_progress\",\"kind\":\"" + tool.Kind + "\",\"title\":" + Json(tool.Command) + ",\"rawInput\":{\"command\":" + Json(tool.Command) + "}}"));
        }

        return string.Join('\n', lines) + "\n";
    }

    private static string After(FakeAcpTurn turn, int n, decimal total)
    {
        List<string> lines = [];
        foreach ((FakeAcpTool tool, int index) in (turn.Tools ?? []).Select((tool, index) => (tool, index)))
        {
            string failure = tool.Error is { } error ? ",\"rawOutput\":{\"error\":" + Json(error) + "}" : "";
            lines.Add(Update("{\"sessionUpdate\":\"tool_call_update\",\"toolCallId\":\"call_" + n + "_" + index + "\",\"status\":\"" + tool.Status + "\",\"rawInput\":{\"command\":" + Json(tool.Command) + "}" + failure + "}"));
        }

        foreach (string chunk in Chunks(turn))
            lines.Add(Chunk("agent_message_chunk", $"msg_{n}", chunk));

        if (turn.Priced)
            lines.Add(Update("{\"sessionUpdate\":\"usage_update\",\"used\":100,\"size\":200000,\"cost\":{\"amount\":" + Money(total) + ",\"currency\":\"USD\"}}"));

        return lines.Count == 0 ? "" : string.Join('\n', lines) + "\n";
    }

    private static string[] Chunks(FakeAcpTurn turn)
    {
        if (turn.Chunks is { } given)
            return given;

        string report = turn.ReportJson is { } json
            ? $"\n```claustrum-report\n{json}\n```"
            : turn.Summary is { } summary ? $"\n```claustrum-report\n{{\"summary\":\"{summary}\"}}\n```" : "";
        string text = turn.Text + report;
        if (text.Length == 0)
            return [];

        // Two chunks, so every test reads a reply the way the real agent streams it: in pieces.
        int cut = text.Length / 2;
        return cut == 0 ? [text] : [text[..cut], text[cut..]];
    }

    private static string Answer(FakeAcpTurn turn)
    {
        if (turn.Error is { } error)
            return "{\"jsonrpc\":\"2.0\",\"id\":\"@ID@\",\"error\":" + ErrorObject(error) + "}\n";

        int total = turn.InputTokens + turn.OutputTokens;
        return "{\"jsonrpc\":\"2.0\",\"id\":\"@ID@\",\"result\":{\"stopReason\":" + Json(turn.StopReason) + ",\"usage\":{\"inputTokens\":" + turn.InputTokens.ToString(CultureInfo.InvariantCulture)
            + ",\"outputTokens\":" + turn.OutputTokens.ToString(CultureInfo.InvariantCulture) + ",\"totalTokens\":" + total.ToString(CultureInfo.InvariantCulture) + ",\"thoughtTokens\":0},\"_meta\":{}}}\n";
    }

    private string Script() => """
        #!/bin/sh
        dir='@DIR@'
        case "$1" in
          --version)
            echo 'fake-acp 1.0.0'
            exit 0 ;;
          session)
            echo "$*" >> "$dir/exports"
            if [ -e "$dir/export-fails" ]; then cat "$dir/export-fails" >&2; exit 1; fi
            printf '{"info":{"cost":%s,"tokens":{"input":10,"output":5,"reasoning":0,"cache":{"read":0,"write":0}}},"messages":[]}\n' "$(cat "$dir/total")"
            exit 0 ;;
          acp) ;;
          *) exit 0 ;;
        esac
        n=$(cat "$dir/count" 2>/dev/null || echo 0)
        n=$((n+1))
        printf '%s' "$n" > "$dir/count"
        printf '%s\n' "$@" > "$dir/argv-$n.txt"
        printf '%s' "$OPENCODE_CONFIG_CONTENT" > "$dir/config-content-$n.json"
        printf '%s' "$OPENCODE_DISABLE_FILEWATCHER" > "$dir/filewatcher-$n.txt"
        pwd > "$dir/cwd-$n.txt"
        printf '%s' "$$" > "$dir/pid-$n.txt"
        session=ses_fake
        pn=0
        nap() {
          [ "$1" -gt 0 ] || return 0
          sleep "$1" &
          printf '%s' "$!" > "$dir/nap-pid"
          wait "$!"
        }
        refuse() {
          printf '{"jsonrpc":"2.0","id":"%s","error":%s}\n' "$1" "$(cat "$2")"
        }
        handshake() {
          if [ -e "$dir/hs-crash-$1" ]; then echo crashed >&2; exit "$(cat "$dir/hs-crash-$1")"; fi
          if [ -e "$dir/hs-stall-$1" ]; then nap "$(cat "$dir/hs-stall-$1")"; fi
        }
        field() {
          printf '%s' "$line" | sed -n "s/.*\"$1\":\"\\([^\"]*\\)\".*/\\1/p"
        }
        while IFS= read -r line; do
          printf '%s\n' "$line" >> "$dir/received.log"
          id=$(printf '%s' "$line" | sed -n 's/^{"jsonrpc":"2.0","id":"\([^"]*\)".*/\1/p')
          case "$line" in
            *'"method":"initialize"'*)
              echo initialize >> "$dir/methods.log"
              handshake initialize
              if [ -e "$dir/noise" ]; then cat "$dir/noise"; fi
              if [ -e "$dir/refuse-initialize" ]; then refuse "$id" "$dir/refuse-initialize"; continue; fi
              printf '{"jsonrpc":"2.0","id":"%s","result":{"protocolVersion":1,"agentCapabilities":{"loadSession":true}}}\n' "$id" ;;
            *'"method":"session/new"'*)
              echo session/new >> "$dir/methods.log"
              handshake session-new
              if [ -e "$dir/refuse-session-new" ]; then refuse "$id" "$dir/refuse-session-new"; continue; fi
              printf '{"jsonrpc":"2.0","id":"%s","result":{"sessionId":"%s","configOptions":[]}}\n' "$id" "$session"
              sed "s/@SESSION@/$session/g" "$dir/after-new" ;;
            *'"method":"session/load"'*)
              echo session/load >> "$dir/methods.log"
              handshake session-load
              session=$(field sessionId)
              if [ -e "$dir/refuse-session-load" ]; then refuse "$id" "$dir/refuse-session-load"; continue; fi
              sed "s/@SESSION@/$session/g" "$dir/replay"
              printf '{"jsonrpc":"2.0","id":"%s","result":{"configOptions":[]}}\n' "$id"
              sed "s/@SESSION@/$session/g" "$dir/after-new" ;;
            *'"method":"session/set_config_option"'*)
              echo session/set_config_option >> "$dir/methods.log"
              handshake config
              cid=$(field configId)
              printf '%s=%s\n' "$cid" "$(field value)" >> "$dir/config-options.log"
              if [ -e "$dir/refuse-config-$cid" ]; then refuse "$id" "$dir/refuse-config-$cid"; continue; fi
              printf '{"jsonrpc":"2.0","id":"%s","result":{"configOptions":[]}}\n' "$id" ;;
            *'"method":"session/prompt"'*)
              echo session/prompt >> "$dir/methods.log"
              pn=$((pn+1))
              printf '%s\n' "$line" > "$dir/prompt-$pn.txt"
              k=$pn
              [ "$k" -le "$(cat "$dir/turns")" ] || k=$(cat "$dir/turns")
              nap "$(cat "$dir/sleep-$k")"
              code=$(cat "$dir/exit-$k")
              if [ "$code" != 0 ]; then echo boom >&2; exit "$code"; fi
              cp "$dir/total-$k" "$dir/total"
              sh "$dir/writes-$k"
              sed "s/@SESSION@/$session/g" "$dir/before-$k"
              if [ -s "$dir/requests-$k" ]; then
                while IFS= read -r request <&3; do
                  printf '%s\n' "$request" | sed "s/@SESSION@/$session/g"
                  IFS= read -r answer || exit 0
                  printf '%s\n' "$answer" >> "$dir/answers.log"
                done 3< "$dir/requests-$k"
              fi
              sed "s/@SESSION@/$session/g" "$dir/after-$k"
              nap "$(cat "$dir/stall-$k")"
              sed "s/@SESSION@/$session/g; s/@ID@/$id/g" "$dir/answer-$k"
              printf '%s' "$pn" > "$dir/answered" ;;
            *) ;;
          esac
        done
        nap "$(cat "$dir/linger")"
        exit 0
        """.Replace("@DIR@", directory, StringComparison.Ordinal) + "\n";
}
