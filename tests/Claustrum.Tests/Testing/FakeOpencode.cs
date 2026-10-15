using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Claustrum.Tests.Testing;

/// <summary>
/// One process of a <see cref="FakeOpencode"/>: what `opencode run` prints for one turn of a live session (#124 stage 3a), or for a
/// plain run. <see cref="Text"/> may not hold a single quote.
/// </summary>
/// <param name="Text">The turn's reply text, one `text` event.</param>
/// <param name="Summary">When set, the reply ends with a `claustrum-report` fence carrying it: the role is done. Null (and no <paramref name="ReportJson"/>) is a question.</param>
/// <param name="ReportJson">The exact JSON of the report fence (a `needs_input` report), instead of <paramref name="Summary"/>'s.</param>
/// <param name="SleepSeconds">How long the process takes before it prints: a message that arrives meanwhile is mid-turn.</param>
/// <param name="ExitCode">The process's exit code; non-zero prints nothing on stdout and "boom" on stderr, as a turn that failed.</param>
/// <param name="CostUsd">What this process adds to the session's running total, which `session export` reports.</param>
/// <param name="BecomesUnrunnable">The script takes its own permissions away after this process, so starting the next turn fails (not a missing file: a missing configured path falls back to PATH, where a real opencode may be).</param>
public sealed record FakeOpencodeTurn(
    string Text,
    string? Summary = null,
    string? ReportJson = null,
    int SleepSeconds = 0,
    int ExitCode = 0,
    decimal CostUsd = 0.01m,
    bool BecomesUnrunnable = false)
{
    public static FakeOpencodeTurn Ask(string question, int sleepSeconds = 0, decimal costUsd = 0.01m) => new(question, SleepSeconds: sleepSeconds, CostUsd: costUsd);

    public static FakeOpencodeTurn Report(string summary, string text = "all done", int sleepSeconds = 0, decimal costUsd = 0.01m) =>
        new(text, summary, SleepSeconds: sleepSeconds, CostUsd: costUsd);

    public static FakeOpencodeTurn NeedsInput(string reportJson, string text = "I need the human", decimal costUsd = 0.01m) => new(text, ReportJson: reportJson, CostUsd: costUsd);
}

/// <summary>
/// A stand-in `opencode` for the live (one process per turn) tests: the process number n answers with turn n (the last turn repeats once
/// the list runs out). `run … [--session id] [--] message` prints opencode's event lines for the turn and exits; `session export` prints
/// the session's running cost; anything else (`--version`) exits 0. Every run is recorded in the script's directory: `argv-n.txt` (one argument
/// per line), `message-n.txt` (the argument after `--`, else the last) and `count`. POSIX sh only.
/// </summary>
public sealed class FakeOpencode
{
    public const string Session = "ses_fake";

    private readonly string directory;

    /// <param name="directory">Where the script and its recordings live; it must exist.</param>
    /// <param name="turns">What each process answers with.</param>
    /// <param name="startingCostUsd">The session's total before the first process: a resumed session already has a bill.</param>
    public FakeOpencode(string directory, FakeOpencodeTurn[] turns, decimal startingCostUsd = 0m)
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("the fake opencode is a POSIX sh script.");

        this.directory = directory;
        Write(turns, startingCostUsd);
    }

    /// <summary>Skips the case on Windows, for a harness that builds the fake before it can call the constructor's own guard.</summary>
    internal static void RequirePosixShellForHarness()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("the fake opencode is a POSIX sh script.");
    }

    public string ScriptPath => Path.Combine(directory, "opencode");

    /// <summary>The processes `run` started so far.</summary>
    public int Runs => File.Exists(Path.Combine(directory, "count")) ? int.Parse(File.ReadAllText(Path.Combine(directory, "count")), CultureInfo.InvariantCulture) : 0;

    /// <summary>The arguments process <paramref name="n"/> (1-based) was started with.</summary>
    public string[] Argv(int n) => File.Exists(Path.Combine(directory, $"argv-{n}.txt")) ? File.ReadAllLines(Path.Combine(directory, $"argv-{n}.txt")) : [];

    /// <summary>The message process <paramref name="n"/> was given: the argument after `--`, else the last argument.</summary>
    public string Message(int n) => File.Exists(Path.Combine(directory, $"message-{n}.txt")) ? File.ReadAllText(Path.Combine(directory, $"message-{n}.txt")) : "";

    /// <summary>How many `session export` calls ran.</summary>
    public int Exports => File.Exists(Path.Combine(directory, "exports")) ? File.ReadAllLines(Path.Combine(directory, "exports")).Length : 0;

    /// <summary>`"backends":{"opencode":{"path":"…"}}` for a claustrum.json.</summary>
    public string BackendJson => "\"opencode\":{\"path\":\"" + ScriptPath.Replace("\\", "\\\\", StringComparison.Ordinal) + "\"}";

    private void Write(FakeOpencodeTurn[] turns, decimal startingCostUsd)
    {
        decimal total = startingCostUsd;
        File.WriteAllText(Path.Combine(directory, "total"), Money(total));
        for (int i = 0; i < turns.Length; i++)
        {
            FakeOpencodeTurn turn = turns[i];
            total += turn.CostUsd;
            string n = (i + 1).ToString(CultureInfo.InvariantCulture);
            File.WriteAllText(Path.Combine(directory, $"events-{n}"), Events(turn, i + 1));
            File.WriteAllText(Path.Combine(directory, $"total-{n}"), Money(total));
            File.WriteAllText(Path.Combine(directory, $"exit-{n}"), turn.ExitCode.ToString(CultureInfo.InvariantCulture));
            File.WriteAllText(Path.Combine(directory, $"sleep-{n}"), turn.SleepSeconds.ToString(CultureInfo.InvariantCulture));
            File.WriteAllText(Path.Combine(directory, $"suicide-{n}"), turn.BecomesUnrunnable ? "1" : "0");
        }

        File.WriteAllText(Path.Combine(directory, "turns"), turns.Length.ToString(CultureInfo.InvariantCulture));
        File.WriteAllText(ScriptPath, Script());
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(ScriptPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static string Money(decimal usd) => usd.ToString(CultureInfo.InvariantCulture);

    // The events opencode prints for a turn: a step_start and one text part (no step_finish: opencode never emits one for its final message).
    private static string Events(FakeOpencodeTurn turn, int n)
    {
        if (turn.ExitCode != 0)
            return "";

        string report = turn.ReportJson is { } json
            ? $"\n```claustrum-report\n{json}\n```"
            : turn.Summary is { } summary ? $"\n```claustrum-report\n{{\"summary\":\"{summary}\"}}\n```" : "";
        string text = JsonSerializer.Serialize(turn.Text + report);
        StringBuilder events = new();
        events.Append("{\"type\":\"step_start\",\"sessionID\":\"@SESSION@\",\"part\":{\"id\":\"prt_").Append(n).Append("_1\",\"type\":\"step-start\"}}\n");
        events.Append("{\"type\":\"text\",\"sessionID\":\"@SESSION@\",\"part\":{\"id\":\"prt_").Append(n).Append("_2\",\"type\":\"text\",\"text\":").Append(text).Append("}}\n");
        return events.ToString();
    }

    private string Script() => """
        #!/bin/sh
        dir='@DIR@'
        case "$1" in
          session)
            echo "$*" >> "$dir/exports"
            printf '{"info":{"cost":%s,"tokens":{"input":10,"output":5,"reasoning":0,"cache":{"read":0,"write":0}}},"messages":[]}\n' "$(cat "$dir/total")"
            exit 0 ;;
          run) ;;
          *) exit 0 ;;
        esac
        n=$(cat "$dir/count" 2>/dev/null || echo 0)
        n=$((n+1))
        printf '%s' "$n" > "$dir/count"
        printf '%s\n' "$@" > "$dir/argv-$n.txt"
        session=ses_fake
        message=''
        seen_dash=0
        prev=''
        for arg in "$@"; do
          if [ "$prev" = '--session' ]; then session="$arg"; fi
          if [ "$seen_dash" = 1 ]; then message="$arg"; fi
          if [ "$arg" = '--' ]; then seen_dash=1; fi
          prev="$arg"
        done
        [ "$seen_dash" = 1 ] || message="$prev"
        printf '%s' "$message" > "$dir/message-$n.txt"
        k=$n
        [ "$k" -le "$(cat "$dir/turns")" ] || k=$(cat "$dir/turns")
        sleep "$(cat "$dir/sleep-$k")"
        cp "$dir/total-$k" "$dir/total"
        [ "$(cat "$dir/suicide-$k")" = 1 ] && chmod 000 "$0"
        code=$(cat "$dir/exit-$k")
        if [ "$code" != 0 ]; then echo boom >&2; exit "$code"; fi
        sed "s/@SESSION@/$session/" "$dir/events-$k"
        exit 0
        """.Replace("@DIR@", directory, StringComparison.Ordinal).Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";
}
