using System.Text;

namespace Claustrum.Tests.Testing;

/// <summary>What a <see cref="FakeClaude"/> script does when claustrum runs it as the `claude` backend.</summary>
public sealed record FakeClaudeScript
{
    /// <summary>Files written into the working directory (the job's worktree, for an isolated run).</summary>
    public (string Path, string Content)[] Writes { get; init; } = [];

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

    public bool Fail { get; init; }
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
            Assert.Skip("this case records the fake backend's argv or stdin, which only the POSIX script does.");
    }

    public static void Write(string scriptPath, FakeClaudeScript script)
    {
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllText(scriptPath, WindowsText(script));
            return;
        }

        File.WriteAllText(scriptPath, PosixText(script));
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

    private static string ReplyJson(FakeClaudeScript script)
    {
        string result = script.Summary is { } summary
            ? $"done\\n```claustrum-report\\n{{\\\"summary\\\":\\\"{summary}\\\"}}\\n```"
            : "done";
        string isError = script.Fail ? "true" : "false";
        return "{\"result\":\"" + result + "\",\"total_cost_usd\":0.01,\"is_error\":" + isError + "}";
    }

    private static string PosixText(FakeClaudeScript script)
    {
        StringBuilder text = new("#!/bin/sh\n");
        text.Append(script.ArgvLog is { } argv ? $"printf '%s\\n' \"$@\" > '{argv}'\n" : "");
        text.Append(script.StartStampDirectory is { } stamps ? $"touch '{stamps}/start-'$$\n" : "");
        text.Append(script.StdinLog is { } stdin ? $"cat > '{stdin}'\n" : "cat > /dev/null\n");
        text.Append(script.MarkerLog is { } start ? $"echo start >> '{start}'\n" : "");
        text.Append(script.SleepSeconds > 0 ? $"sleep {script.SleepSeconds}\n" : "");
        foreach ((string path, string content) in script.Writes)
            text.Append($"printf '%s\\n' '{content}' > '{path}'\n");

        text.Append(script.WriteUniqueFile ? "printf 'unique\\n' > \"out-$$.txt\"\n" : "");
        text.Append(script.MarkerLog is { } end ? $"echo end >> '{end}'\n" : "");
        text.Append($"echo '{ReplyJson(script)}'\n");
        text.Append(script.Fail ? "exit 1\n" : "");
        return text.ToString();
    }

    // cmd.exe: `echo` prints an unescaped `"` as-is, so the reply goes out verbatim (CoordinateEndToEndTests'
    // 2026-09-22 finding). Not run in this repo's Linux gate; the argv/stdin capture is POSIX-only.
    private static string WindowsText(FakeClaudeScript script)
    {
        StringBuilder text = new("@echo off\r\nfindstr \"^\" > nul\r\n");
        text.Append(script.MarkerLog is { } start ? $"echo start>> \"{start}\"\r\n" : "");
        text.Append(script.SleepSeconds > 0 ? $"ping -n {script.SleepSeconds + 1} 127.0.0.1 > nul\r\n" : "");
        foreach ((string path, string content) in script.Writes)
            text.Append($"echo {content}> \"{path}\"\r\n");

        text.Append(script.WriteUniqueFile ? "echo unique> \"out-%RANDOM%%RANDOM%.txt\"\r\n" : "");
        text.Append(script.MarkerLog is { } end ? $"echo end>> \"{end}\"\r\n" : "");
        text.Append($"echo {ReplyJson(script)}\r\n");
        text.Append(script.Fail ? "exit /b 1\r\n" : "");
        return text.ToString();
    }
}
