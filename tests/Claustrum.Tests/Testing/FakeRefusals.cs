using System.Globalization;

namespace Claustrum.Tests.Testing;

/// <summary>
/// The stand-ins for a provider that refuses a request (#126), hermetic: shell scripts that replay recorded output and cost nothing.
/// <see cref="Opencode"/> replays opencode's stdout (the real 402 of job 20261015-164125-2f5f1b1d, or a variation) and exits non-zero;
/// <see cref="WriteRefusingClaude"/> writes a claude that prints a refusing result. Both count their runs and record the brief they were given. POSIX sh only.
/// </summary>
public static class FakeRefusals
{
    public static void RequirePosixShell()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("the fake provider refusals are POSIX sh scripts.");
    }

    /// <summary>The recorded 402: tests/fixtures/opencode/quota-402.jsonl.</summary>
    public static string RealQuotaEvents() => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "opencode", "quota-402.jsonl"));

    /// <summary>
    /// opencode's stdout for a run that did one balanced step, optionally completed a tool call first, and then met this error event.
    /// What it cost is not in the stream: `session export` reports it (<see cref="Opencode"/>'s <c>costUsd</c>).
    /// </summary>
    public static string OpencodeEvents(string errorJson, bool completedToolCall = false)
    {
        string tool = completedToolCall
            ? /*lang=json,strict*/ """{"type":"tool_use","sessionID":"ses_fake","part":{"id":"call_1","type":"tool","tool":"read","state":{"status":"completed"}}}""" + "\n"
            : "";
        return /*lang=json,strict*/ """{"type":"step_start","sessionID":"ses_fake","part":{"id":"prt_1","type":"step-start"}}""" + "\n"
            + tool
            + /*lang=json,strict*/ """{"type":"step_finish","sessionID":"ses_fake","part":{"id":"prt_2","type":"step-finish","reason":"tool-calls","cost":0,"tokens":{"input":10,"output":5,"reasoning":0,"cache":{"read":0,"write":0}}}}""" + "\n"
            + """{"type":"error","sessionID":"ses_fake","error":""" + errorJson + "}\n";
    }

    public const string Quota402 = /*lang=json,strict*/ """{"type":"provider.quota","message":"This request would exceed your available credits.","status":402}""";

    public const string Auth401 = /*lang=json,strict*/ """{"type":"provider.auth","message":"User not found.","status":401}""";

    /// <summary>A stand-in `opencode run` that replays the events it is given and exits non-zero (1 unless told otherwise).</summary>
    public sealed class Opencode
    {
        private readonly string directory;

        public Opencode(string directory, string events, int exitCode = 1, decimal costUsd = 0m)
        {
            RequirePosixShell();
            this.directory = directory;
            File.WriteAllText(Path.Combine(directory, "events"), events);
            File.WriteAllText(ScriptPath, Script(exitCode, costUsd));
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(ScriptPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        public string ScriptPath => Path.Combine(directory, "opencode");

        public string BackendJson => "\"opencode\":{\"path\":\"" + ScriptPath.Replace("\\", "\\\\", StringComparison.Ordinal) + "\"}";

        /// <summary>The `run` processes started so far.</summary>
        public int Runs => File.Exists(Path.Combine(directory, "count")) ? int.Parse(File.ReadAllText(Path.Combine(directory, "count")), CultureInfo.InvariantCulture) : 0;

        public string[] Argv(int n) => File.ReadAllLines(Path.Combine(directory, $"argv-{n}.txt"));

        public string Message(int n) => File.ReadAllText(Path.Combine(directory, $"message-{n}.txt"));

        private string Script(int exitCode, decimal costUsd) => """
            #!/bin/sh
            dir='@DIR@'
            case "$1" in
              session)
                printf '{"info":{"cost":@COST@,"tokens":{"input":0,"output":0,"reasoning":0,"cache":{"read":0,"write":0}}},"messages":[]}\n'
                exit 0 ;;
              run) ;;
              *) exit 0 ;;
            esac
            n=$(cat "$dir/count" 2>/dev/null || echo 0)
            n=$((n+1))
            printf '%s' "$n" > "$dir/count"
            printf '%s\n' "$@" > "$dir/argv-$n.txt"
            message=''
            seen_dash=0
            prev=''
            for arg in "$@"; do
              if [ "$seen_dash" = 1 ]; then message="$arg"; fi
              if [ "$arg" = '--' ]; then seen_dash=1; fi
              prev="$arg"
            done
            [ "$seen_dash" = 1 ] || message="$prev"
            printf '%s' "$message" > "$dir/message-$n.txt"
            cat "$dir/events"
            exit @EXIT@
            """.Replace("@DIR@", directory, StringComparison.Ordinal).Replace("@COST@", costUsd.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal).Replace("@EXIT@", exitCode.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal) + "\n";
    }

    /// <summary>
    /// Writes a stand-in `claude` that refuses with this HTTP status (a claude `result` with `api_error_status`), counting its runs in
    /// `claude-count` beside it and keeping each brief it was given in `claude-brief-n.txt`. Returns the count file's path.
    /// </summary>
    public static string WriteRefusingClaude(string scriptPath, int status)
    {
        RequirePosixShell();
        string directory = Path.GetDirectoryName(scriptPath) ?? throw new InvalidOperationException(scriptPath);
        string count = Path.Combine(directory, "claude-count");
        string result = "{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":true,\"api_error_status\":" + status.ToString(CultureInfo.InvariantCulture)
            + ",\"session_id\":\"refused\",\"result\":\"Invalid API key\",\"total_cost_usd\":0}";
        File.WriteAllText(scriptPath, $$"""
            #!/bin/sh
            n=$(cat '{{count}}' 2>/dev/null || echo 0)
            n=$((n+1))
            printf '%s' "$n" > '{{count}}'
            case " $*" in *" --input-format "*) IFS= read -r brief; printf '%s\n' "$brief" > '{{directory}}/claude-brief-'$n'.txt';; *) cat > '{{directory}}/claude-brief-'$n'.txt';; esac
            printf '%s\n' '{"type":"system","subtype":"init","session_id":"refused"}'
            printf '%s\n' '{{result}}'
            exit 1

            """);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(scriptPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        return count;
    }

    public static int RunsOf(string countFile) => File.Exists(countFile) ? int.Parse(File.ReadAllText(countFile), CultureInfo.InvariantCulture) : 0;
}
