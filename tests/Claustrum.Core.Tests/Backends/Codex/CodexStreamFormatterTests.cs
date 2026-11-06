using Claustrum.Core.Backends.Codex;

namespace Claustrum.Core.Tests.Backends.Codex;

public sealed class CodexStreamFormatterTests
{
    private readonly CodexStreamFormatter formatter = new();

    private static string[] Lines(string fixture) =>
        File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "fixtures", "codex", fixture));

    [Fact]
    public void AToolRunPrintsWhatItSaidAndRanOnceEach()
    {
        string[] printed = [.. Lines("tools-read-only.jsonl").Select(line => formatter.FormatLine(line, replayed: true)).OfType<string>()];

        Assert.Equal(3, printed.Length);
        Assert.StartsWith("--:--:-- SAYS  I’ll read `a.txt`", printed[0], StringComparison.Ordinal);
        Assert.Equal("--:--:-- SHELL /bin/bash -lc 'cat a.txt'", printed[1]);
        Assert.StartsWith("--:--:-- SAYS  `a.txt` contains:", printed[2], StringComparison.Ordinal);
    }

    [Fact]
    public void AFailedCommandAFileChangeAndARefusalAreMarked()
    {
        string? failed = Lines("command-failed.jsonl").Select(line => formatter.FormatLine(line, replayed: true)).OfType<string>().ElementAt(1);
        string? edit = Lines("file-change.jsonl").Select(line => formatter.FormatLine(line, replayed: true)).OfType<string>().ElementAt(1);
        string[] auth = [.. Lines("auth-401.jsonl").Select(line => formatter.FormatLine(line, replayed: true)).OfType<string>()];

        Assert.StartsWith("--:--:-- ✗     /bin/bash -lc", failed, StringComparison.Ordinal);
        Assert.Equal("--:--:-- EDIT  /work/repo/c.txt", edit);
        Assert.StartsWith("--:--:-- RETRY Reconnecting... 2/5", auth[0], StringComparison.Ordinal);
        Assert.StartsWith("--:--:-- ERROR unexpected status 401", auth[^1], StringComparison.Ordinal);
        Assert.Single(auth, line => line.Contains("ERROR", StringComparison.Ordinal));
    }

    [Fact]
    public void ATerminalErrorEventIsAnErrorNotARetry()
    {
        string[] printed = [.. Lines("model-not-found.jsonl").Select(line => formatter.FormatLine(line, replayed: true)).OfType<string>()];

        Assert.Equal(2, printed.Length);
        Assert.StartsWith("--:--:-- WARN  Model metadata for", printed[0], StringComparison.Ordinal);
        Assert.StartsWith("--:--:-- ERROR The model `gpt-nonexistent-9` does not exist", printed[1], StringComparison.Ordinal);
    }

    [Fact]
    public void BookkeepingPrintsNothingAndAnEscapeInTextIsShownNotSent()
    {
        Assert.Null(formatter.FormatLine(/*lang=json,strict*/ """{"type":"turn.started"}"""));
        Assert.Null(formatter.FormatLine(/*lang=json,strict*/ """{"type":"item.started","item":{"type":"command_execution","command":"ls"}}"""));
        Assert.Equal(
            "--:--:-- SAYS  a\\x1b[2Jb",
            formatter.FormatLine(/*lang=json,strict*/ """{"type":"item.completed","item":{"type":"agent_message","text":"a\u001b[2Jb"}}""", replayed: true));
    }
}
