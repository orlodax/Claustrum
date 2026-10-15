using System.Text;

namespace Claustrum.Tests.Testing;

/// <summary>What a <see cref="FakeClaude"/> script does when claustrum runs it as the `claude` backend.</summary>
public sealed record FakeClaudeScript
{
    /// <summary>Files written into the working directory (the job's worktree, for an isolated run).</summary>
    public (string Path, string Content)[] Writes { get; init; } = [];

    /// <summary>
    /// Shell lines run in the working directory after <see cref="Writes"/>, in order — how a role leaves a repository
    /// in an awkward state (a commit-less `git init`, a conflicted merge, a chmod 000 file) for the runner's commit to
    /// meet (POSIX shells only).
    /// </summary>
    public string[] Commands { get; init; } = [];

    /// <summary>One more file, `out-&lt;pid&gt;.txt`, so two concurrent runs never write the same path.</summary>
    public bool WriteUniqueFile { get; init; }

    public int SleepSeconds { get; init; }

    /// <summary>A file the script appends `start` to when it begins and `end` to when it is done.</summary>
    public string? MarkerLog { get; init; }

    /// <summary>
    /// A directory the script `touch`es `start-&lt;pid&gt;` in the moment it begins, so a test can compare when
    /// each run started with when another run's result.json was written (POSIX shells only).
    /// </summary>
    public string? StartStampDirectory { get; init; }

    /// <summary>A file the script writes its argv to, one argument per line (POSIX shells only).</summary>
    public string? ArgvLog { get; init; }

    /// <summary>A file the script copies its standard input to — the brief, since #68 (POSIX shells only).</summary>
    public string? StdinLog { get; init; }

    /// <summary>When set, the reply carries a `claustrum-report` fence with this `summary`.</summary>
    public string? Summary { get; init; }

    /// <summary>
    /// When set, the reply's `claustrum-report` fence holds exactly this JSON (a `needs_input` report with its `questions`, #124),
    /// instead of <see cref="Summary"/>'s. May not hold a single quote or a line break.
    /// </summary>
    public string? ReportJson { get; init; }

    /// <summary>When set, the one-shot reply names this `session_id`, so the job has a session `--resume` can continue.</summary>
    public string? SessionId { get; init; }

    public bool Fail { get; init; }

    /// <summary>
    /// stream-json events printed one per line before the reply, which then carries `"type":"result"` as claude's last line does
    /// (POSIX shells only; the lines may not hold a single quote).
    /// </summary>
    public string[] StreamLines { get; init; } = [];

    /// <summary>
    /// A conversing claude (#124): the script reads one stream-json user message per stdin line, answers each with an
    /// `assistant` and a `result` event from the matching turn (the last turn repeats once the list runs out), stays
    /// alive between turns and exits 0 when stdin closes. Replaces the one-shot reply; POSIX shells only.
    /// </summary>
    public FakeClaudeTurn[]? Turns { get; init; }

    /// <summary>With <see cref="Turns"/>: a file every stdin line is appended to, raw, so a test sees each message the engine sent.</summary>
    public string? LiveInputLog { get; init; }
}

/// <summary>One turn of a conversing <see cref="FakeClaude"/>. <see cref="Text"/> may not hold a quote, a backslash or a line break.</summary>
/// <param name="Text">The turn's reply text.</param>
/// <param name="Summary">When set, the reply ends with a `claustrum-report` fence carrying it: the role is done. Null is a question.</param>
/// <param name="EchoReply">Appends the text of the user message this turn answers, JSON-escaped as it came on the wire.</param>
/// <param name="SleepSeconds">How long the turn takes before its `result`: other messages queue on stdin meanwhile, as with the real thing.</param>
/// <param name="ReadMidTurn">How many more stdin lines the turn blocks to read (and log) before its `result`: a message that arrives mid-turn, as the real claude takes it.</param>
/// <param name="LingerSeconds">How long the process stays alive after the `result`, as a claude that is slow to exit once it has reported.</param>
/// <param name="ReportJson">The exact JSON of the `claustrum-report` fence this turn ends with (a `needs_input` report), instead of <paramref name="Summary"/>'s; no single quote.</param>
/// <param name="TotalCostUsd">The session's running cost at this turn's `result`; by default it rises per turn from claude's measured $0.2037 / $0.2278.</param>
public sealed record FakeClaudeTurn(string Text, string? Summary = null, bool EchoReply = false, int SleepSeconds = 0, decimal? TotalCostUsd = null, int ReadMidTurn = 0, int LingerSeconds = 0, string? ReportJson = null)
{
    public static FakeClaudeTurn Ask(string question, int sleepSeconds = 0) => new(question, SleepSeconds: sleepSeconds);

    public static FakeClaudeTurn Report(string summary, string text = "all done", bool echoReply = false, int sleepSeconds = 0) =>
        new(text, summary, echoReply, sleepSeconds);
}

/// <summary>
/// A stand-in for the `claude` binary: a script that `claustrum.json` `backends.claude.path` points at, so a
/// run reaches the real Runner, worktree and commit code without a real backend or a cent spent — the same
/// override CoordinateEndToEndTests uses, here also able to leave files behind, record what it was given and
/// take a while. It prints a claude-shaped `json` result.
/// </summary>
public static class FakeClaude
{
    public static string ScriptName => OperatingSystem.IsWindows() ? "fake-claude.cmd" : "fake-claude.sh";

    /// <summary>Argv and stdin capture rely on `printf '%s\n' "$@"` and `cat`; the Windows spelling is not verified.</summary>
    public static void RequirePosixShell()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("this case records the fake backend's argv or stdin, or runs shell commands, which only the POSIX script does.");
    }

    public static void Write(string scriptPath, FakeClaudeScript script)
    {
        if (OperatingSystem.IsWindows())
        {
            if (script.Turns is not null)
                throw new NotSupportedException("a conversing fake claude is a POSIX sh script; the test must call RequirePosixShell first.");

            File.WriteAllText(scriptPath, WindowsText(script));
            return;
        }

        File.WriteAllText(scriptPath, script.Turns is null ? PosixText(script) : LiveText(script));
        File.SetUnixFileMode(
            scriptPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }

    /// <summary>`{"backends":{"claude":{"path":"…"}}}`, the config that points the claude backend at the script.</summary>
    public static string ConfigJson(string scriptPath)
    {
        string escaped = scriptPath.Replace("\\", "\\\\", StringComparison.Ordinal);
        return "{\"backends\":{\"claude\":{\"path\":\"" + escaped + "\"}}}";
    }

    /// <summary>The last line the script prints, the one the receipt is parsed from.</summary>
    public static string ReplyLine(FakeClaudeScript script) => ReplyJson(script);

    private static string ReplyJson(FakeClaudeScript script)
    {
        string result = script.ReportJson is { } reportJson
            ? $"done\\n```claustrum-report\\n{Escaped(reportJson)}\\n```"
            : script.Summary is { } summary
                ? $"done\\n```claustrum-report\\n{{\\\"summary\\\":\\\"{summary}\\\"}}\\n```"
                : "done";
        string isError = script.Fail ? "true" : "false";
        string type = script.StreamLines.Length > 0 ? "\"type\":\"result\"," : "";
        string session = script.SessionId is { } id ? "\"session_id\":\"" + id + "\"," : "";
        return "{" + type + session + "\"result\":\"" + result + "\",\"total_cost_usd\":0.01,\"is_error\":" + isError + "}";
    }

    // A JSON document as the content of a JSON string: backslashes and quotes escaped.
    private static string Escaped(string json) => json.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

    private static string PosixText(FakeClaudeScript script)
    {
        StringBuilder text = new("#!/bin/sh\n");
        text.Append(script.ArgvLog is { } argv ? $"printf '%s\\n' \"$@\" > '{argv}'\n" : "");
        text.Append(script.StartStampDirectory is { } stamps ? $"touch '{stamps}/start-'$$\n" : "");
        // A live run (--input-format on argv, #124) keeps stdin open until the report, so a one-shot script reads only the
        // brief's line: `cat` would wait for an end that comes after this script has exited.
        string stdinTarget = script.StdinLog is { } stdin ? $"'{stdin}'" : "/dev/null";
        text.Append($"case \" $*\" in *\" --input-format \"*) IFS= read -r brief; printf '%s\\n' \"$brief\" > {stdinTarget};; *) cat > {stdinTarget};; esac\n");
        text.Append(script.MarkerLog is { } start ? $"echo start >> '{start}'\n" : "");
        text.Append(script.SleepSeconds > 0 ? $"sleep {script.SleepSeconds}\n" : "");
        foreach ((string path, string content) in script.Writes)
            text.Append($"printf '%s\\n' '{content}' > '{path}'\n");

        text.Append(script.WriteUniqueFile ? "printf 'unique\\n' > \"out-$$.txt\"\n" : "");
        foreach (string command in script.Commands)
            text.Append(command).Append('\n');

        text.Append(script.MarkerLog is { } end ? $"echo end >> '{end}'\n" : "");
        foreach (string streamLine in script.StreamLines)
            text.Append($"printf '%s\\n' '{streamLine}'\n");

        // Not `echo`: dash (Ubuntu's /bin/sh) expands the reply's `\n` escapes, the JSON breaks and the report is lost (CI, 2026-10-08).
        text.Append($"printf '%s\\n' '{ReplyJson(script)}'\n");
        text.Append(script.Fail ? "exit 1\n" : "");
        return text.ToString();
    }

    private static string LiveText(FakeClaudeScript script)
    {
        FakeClaudeTurn[] turns = script.Turns ?? [];
        StringBuilder text = new("#!/bin/sh\n");
        text.Append(script.ArgvLog is { } argv ? $"printf '%s\\n' \"$@\" > '{argv}'\n" : "");
        text.Append(script.MarkerLog is { } start ? $"echo start >> '{start}'\n" : "");
        foreach ((string path, string content) in script.Writes)
            text.Append($"printf '%s\\n' '{content}' > '{path}'\n");

        foreach (string command in script.Commands)
            text.Append(command).Append('\n');

        string record = script.LiveInputLog is { } log ? $"printf '%s\\n' \"$line\" >> '{log}'; " : "";
        text.Append("n=0\nsaid=''\ntake() { " + record + "said=$(printf '%s' \"$line\" | sed -n 's/.*\"text\":\"\\(.*\\)\"}\\]}}.*/\\1/p'); }\n");
        text.Append("while IFS= read -r line; do\n  n=$((n+1))\n  take\n  case $n in\n");
        for (int i = 0; i < turns.Length; i++)
            text.Append(i == turns.Length - 1 ? "  *)" : $"  {i + 1})").Append(' ').Append(TurnCommands(turns[i], i)).Append(";;\n");

        text.Append("  esac\ndone\n");
        text.Append(script.MarkerLog is { } end ? $"echo end >> '{end}'\n" : "");
        text.Append("exit 0\n");
        return text.ToString();
    }

    // The two events a turn prints: an `assistant` message, then the `result` that closes it with the running totals.
    private static string TurnCommands(FakeClaudeTurn turn, int index)
    {
        string report = turn.ReportJson is { } reportJson
            ? $"\\n```claustrum-report\\n{Escaped(reportJson)}\\n```"
            : turn.Summary is { } summary ? $"\\n```claustrum-report\\n{{\\\"summary\\\":\\\"{summary}\\\"}}\\n```" : "";
        string cost = (turn.TotalCostUsd ?? 0.2037m + 0.0241m * index).ToString(System.Globalization.CultureInfo.InvariantCulture);
        string head = "{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"session_id\":\"fake-session\",\"num_turns\":1,\"result\":\"" + turn.Text;
        string echo = turn.EchoReply ? " you said: '\"$said\"'" : "";
        string tail = report + "\",\"total_cost_usd\":" + cost + ",\"usage\":{\"input_tokens\":" + 10 * (index + 1) + ",\"output_tokens\":" + 5 * (index + 1) + "}}";
        string assistant = "{\"type\":\"assistant\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"" + turn.Text + "\"}]},\"session_id\":\"fake-session\"}";
        string sleep = turn.SleepSeconds > 0 ? $"sleep {turn.SleepSeconds}; " : "";
        string mid = turn.ReadMidTurn > 0 ? $"k=0; while [ $k -lt {turn.ReadMidTurn} ] && IFS= read -r line; do k=$((k+1)); take; done; " : "";
        string linger = turn.LingerSeconds > 0 ? $"; sleep {turn.LingerSeconds}" : "";
        return $"{mid}{sleep}printf '%s\\n' '{assistant}'; printf '%s\\n' '{head}{echo}{tail}'{linger}";
    }

    // cmd's `>>` opens the file unshared: two runs appending at once lose one line to "file in use"
    // (RunCapEndToEndTests' overlap case on PR #75's Windows CI), so a refused append is retried.
    private static string WindowsAppend(string label, string line, string path) =>
        $":{label}\r\n(echo {line}>> \"{path}\") 2>nul || (ping -n 1 127.0.0.1 > nul & goto {label})\r\n";

    // cmd.exe: `echo` prints an unescaped `"` as-is, so the reply goes out verbatim (CoordinateEndToEndTests'
    // 2026-09-22 finding). Not run in this repo's Linux gate; the argv/stdin capture is POSIX-only.
    private static string WindowsText(FakeClaudeScript script)
    {
        // Under --input-format (a live run) stdin stays open past this script, so only the brief's line is read.
        StringBuilder text = new("@echo off\r\necho %* | findstr /c:\"--input-format\" > nul && (set /p _brief=) || (findstr \"^\" > nul)\r\n");
        text.Append(script.MarkerLog is { } start ? WindowsAppend("markstart", "start", start) : "");
        text.Append(script.SleepSeconds > 0 ? $"ping -n {script.SleepSeconds + 1} 127.0.0.1 > nul\r\n" : "");
        foreach ((string path, string content) in script.Writes)
            text.Append($"echo {content}> \"{path}\"\r\n");

        text.Append(script.WriteUniqueFile ? "echo unique> \"out-%RANDOM%%RANDOM%.txt\"\r\n" : "");
        text.Append(script.MarkerLog is { } end ? WindowsAppend("markend", "end", end) : "");
        text.Append($"echo {ReplyJson(script)}\r\n");
        text.Append(script.Fail ? "exit /b 1\r\n" : "");
        return text.ToString();
    }
}
