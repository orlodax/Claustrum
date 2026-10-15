using System.Text.RegularExpressions;
using Claustrum.Core.Backends.Claude;

namespace Claustrum.Core.Tests.Backends.Claude;

// #123: one readable line per event of `claude --output-format stream-json`, against a genuine capture
// (fixtures/claude/success.jsonl) and a hand-trimmed one with tool calls (stream-tools.jsonl, same event shapes).
// Claude's stream carries no timestamps: a live line is stamped with the clock on arrival, a replayed one with `--:--:--`, and
// lines are compared with that prefix stripped. Every string printed is sanitized (StreamText): controls show as escapes.
public sealed partial class ClaudeStreamFormatterTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "fixtures", "claude", name);

    private static string[] Lines(string name) => File.ReadAllLines(Fixture(name));

    private static string Bare(string? line)
    {
        Assert.NotNull(line);
        Assert.Matches(Clock(), line);
        return Clock().Replace(line, "");
    }

    [GeneratedRegex(@"^\d{2}:\d{2}:\d{2} ")]
    private static partial Regex Clock();

    [Fact]
    public void ToolCallsAreOneLabelledLineWithPathsShortenedToTheCwd()
    {
        ClaudeStreamFormatter formatter = new("/work/repo");
        string[] lines = Lines("stream-tools.jsonl");

        Assert.Equal("EDIT  src/a.cs", Bare(formatter.FormatLine(lines[1])));
        Assert.Equal("BASH  dotnet build", Bare(formatter.FormatLine(lines[3])));
    }

    [Fact]
    public void TextIsFoldedOntoOneSaysLine()
    {
        ClaudeStreamFormatter formatter = new("/work/repo");

        Assert.Equal("SAYS  The build failed on a missing semicolon.", Bare(formatter.FormatLine(Lines("stream-tools.jsonl")[5])));
    }

    [Fact]
    public void LongTextIsCutAtOneHundredFortyCharactersWithAnEllipsis()
    {
        ClaudeStreamFormatter formatter = new("");
        string line = $$$"""{"type":"assistant","message":{"content":[{"type":"text","text":"{{{new string('x', 300)}}}"}]}}""";

        string said = Bare(formatter.FormatLine(line));

        Assert.Equal("SAYS  " + new string('x', 140) + "…", said);
    }

    [Fact]
    public void AToolResultThatSucceededPrintsNothing()
    {
        ClaudeStreamFormatter formatter = new("/work/repo");

        Assert.Null(formatter.FormatLine(Lines("stream-tools.jsonl")[2]));
    }

    [Fact]
    public void AToolResultWithIsErrorPrintsACrossWithTheFoldedMessage()
    {
        ClaudeStreamFormatter formatter = new("/work/repo");

        Assert.Equal("✗     Exit code 1 error CS1002: ; expected", Bare(formatter.FormatLine(Lines("stream-tools.jsonl")[4])));
    }

    [Fact]
    public void AToolResultWhoseContentIsAnArrayOfTextBlocksIsJoined()
    {
        ClaudeStreamFormatter formatter = new("");
        string line = /*lang=json,strict*/ """{"type":"user","message":{"content":[{"type":"tool_result","is_error":true,"content":[{"type":"text","text":"first"},{"type":"text","text":"second"}]}]}}""";

        Assert.Equal("✗     first second", Bare(formatter.FormatLine(line)));
    }

    [Fact]
    public void SeveralFailedResultsInOneEventAreOneLineEach()
    {
        ClaudeStreamFormatter formatter = new("");
        string line = /*lang=json,strict*/ """{"type":"user","message":{"content":[{"type":"tool_result","is_error":true,"content":"one"},{"type":"tool_result","is_error":true,"content":"two"}]}}""";

        string[] printed = formatter.FormatLine(line)!.Split('\n');

        Assert.Equal(["✗     one", "✗     two"], printed.Select(Bare));
    }

    [Theory]
    [InlineData("system")]
    [InlineData("rate_limit_event")]
    [InlineData("stream_event")]
    [InlineData("some_future_type")]
    public void BookkeepingAndUnknownEventTypesPrintNothing(string type)
    {
        ClaudeStreamFormatter formatter = new("");

        Assert.Null(formatter.FormatLine($$$"""{"type":"{{{type}}}","subtype":"init"}"""));
    }

    // The result event is the one place the total is known, so it is the one line that shows it.
    [Fact]
    public void TheGenuineCaptureYieldsThePongLineAndTheTotalLineAndNothingElse()
    {
        ClaudeStreamFormatter formatter = new("C:\\work\\claustrum-fixture");

        string[] printed = [.. Lines("success.jsonl").Select(formatter.FormatLine).OfType<string>()];

        Assert.Equal(2, printed.Length);
        Assert.Equal("SAYS  pong", Bare(printed[0]));
        Assert.Matches(@"^DONE  total \$\d+\.\d{4,}$", Bare(printed[1]));
    }

    [Fact]
    public void TheResultEventPrintsTheTotalAndTheLinesBeforeItCarryNoCost()
    {
        ClaudeStreamFormatter formatter = new("/work/repo");
        string[] lines = Lines("stream-tools.jsonl");

        Assert.Equal("BASH  dotnet build", Bare(formatter.FormatLine(lines[3])));
        Assert.Equal("DONE  total $0.0123", Bare(formatter.FormatLine(lines[^1])));
    }

    [Theory]
    [InlineData(/*lang=json,strict*/ """{"type":"result","total_cost_usd":0.5}""", "DONE  total $0.5000")]
    [InlineData(/*lang=json,strict*/ """{"type":"result","total_cost_usd":1.234567}""", "DONE  total $1.234567")]
    [InlineData(/*lang=json,strict*/ """{"type":"result","total_cost_usd":0}""", "DONE  total $0.0000")]
    public void TheTotalIsFourDecimalsAtLeastAndNeverRoundedAwayBelowThat(string line, string expected)
    {
        Assert.Equal(expected, Bare(new ClaudeStreamFormatter("").FormatLine(line)));
    }

    [Fact]
    public void AResultWithoutANumericTotalPrintsNothing()
    {
        Assert.Null(new ClaudeStreamFormatter("").FormatLine(/*lang=json,strict*/ """{"type":"result","subtype":"success"}"""));
    }

    // Claude's stream has no stamps: a replay prints a placeholder rather than a time it made up, a live line prints the clock.
    [Fact]
    public void AReplayedLineShowsDashesAndALiveOneShowsTheClock()
    {
        ClaudeStreamFormatter formatter = new("/work/repo");
        string line = Lines("stream-tools.jsonl")[5];

        Assert.StartsWith("--:--:-- SAYS  The build failed", formatter.FormatLine(line, replayed: true), StringComparison.Ordinal);
        Assert.Matches(@"^\d{2}:\d{2}:\d{2} SAYS  The build failed", formatter.FormatLine(line, replayed: false));
        Assert.Matches(@"^\d{2}:\d{2}:\d{2} SAYS  The build failed", formatter.FormatLine(line));
    }

    [Fact]
    public void AReplayedTotalAndAReplayedFailureShowDashesToo()
    {
        ClaudeStreamFormatter formatter = new("");

        Assert.StartsWith("--:--:-- DONE  total $", formatter.FormatLine(Lines("stream-tools.jsonl")[^1], replayed: true), StringComparison.Ordinal);
        Assert.StartsWith("--:--:-- ✗     Exit code 1", formatter.FormatLine(Lines("stream-tools.jsonl")[4], replayed: true), StringComparison.Ordinal);
    }

    // ---- nothing a model, a tool or a file says reaches the terminal as a control byte ---------------------------------------

    private static string Assistant(string block) => $$$"""{"type":"assistant","message":{"content":[{{{block}}}]}}""";

    [Fact]
    public void ControlCharactersInModelTextAreVisibleEscapesNeverRawBytes()
    {
        string line = Assistant(/*lang=json,strict*/ """{"type":"text","text":"a\u001b[2Jb\u0007c\u009bd\u007fe\u0000f"}""");

        string said = Bare(new ClaudeStreamFormatter("").FormatLine(line));

        Assert.Equal("SAYS  a\\x1b[2Jb\\x07c\\u009bd\\x7fe\\x00f", said);
        AssertNoControlCharacters(said);
    }

    [Theory]
    [InlineData("command", "echo hi\\u001b]0;pwned\\u0007", "BASH  echo hi\\x1b]0;pwned\\x07")]
    [InlineData("file_path", "/x/\\u001b[2K.cs", "EDIT  /x/\\x1b[2K.cs")]
    [InlineData("pattern", "a\\u009bb", "GREP  a\\u009bb")]
    public void ControlCharactersInAToolTargetAreVisibleEscapes(string field, string value, string expected)
    {
        string tool = field switch { "command" => "Bash", "file_path" => "Edit", _ => "Grep" };
        string line = Assistant($$$"""{"type":"tool_use","name":"{{{tool}}}","input":{"{{{field}}}":"{{{value}}}"}}""");

        string printed = Bare(new ClaudeStreamFormatter("").FormatLine(line));

        Assert.Equal(expected, printed);
        AssertNoControlCharacters(printed);
    }

    [Fact]
    public void AMultiLineCommandFoldsToOneLineSoNothingIsHiddenBehindANewline()
    {
        string line = Assistant(/*lang=json,strict*/ """{"type":"tool_use","name":"Bash","input":{"command":"echo safe\n\r\n\t  rm -rf /\u000b\u000cend"}}""");

        string printed = new ClaudeStreamFormatter("").FormatLine(line)!;

        Assert.DoesNotContain('\n', printed);
        Assert.DoesNotContain('\r', printed);
        Assert.Equal("BASH  echo safe rm -rf / end", Bare(printed));
    }

    [Fact]
    public void AToolNameWithControlCharactersIsEscapedInItsLabelToo()
    {
        string line = Assistant(/*lang=json,strict*/ """{"type":"tool_use","name":"\u001b[1mBold","input":{}}""");

        string printed = new ClaudeStreamFormatter("").FormatLine(line)!;

        AssertNoControlCharacters(printed);
        Assert.Contains("\\X1B", printed, StringComparison.Ordinal);
    }

    [Fact]
    public void ControlCharactersInAFailedToolResultAreEscapedAndItsLinesFolded()
    {
        string line = /*lang=json,strict*/ """{"type":"user","message":{"content":[{"type":"tool_result","is_error":true,"content":"boom\u001b[31m\nnext\u0007"}]}}""";

        string printed = new ClaudeStreamFormatter("").FormatLine(line)!;

        Assert.Equal("✗     boom\\x1b[31m next\\x07", Bare(printed));
        AssertNoControlCharacters(printed);
    }

    // What the formatter does not understand is still untrusted: it goes out sanitized, never as the bytes the backend wrote.
    [Theory]
    [InlineData("plain \u001b[2J text", "plain \\x1b[2J text")]
    [InlineData("[1,\u0007 2]", "[1,\\x07 2]")]
    [InlineData("\u009b31m", "\\u009b31m")]
    public void APassedThroughLineIsSanitizedToo(string raw, string expected)
    {
        Assert.Equal(expected, new ClaudeStreamFormatter("").FormatLine(raw));
    }

    private static void AssertNoControlCharacters(string text) =>
        Assert.False(text.Any(character => character is < ' ' or '\u007f' or (>= '\u0080' and <= '\u009f')), "a control character reached the output");

    [Fact]
    public void ALineThatIsNotJsonIsPassedThroughAsIs()
    {
        Assert.Equal("plain stderr-ish text", new ClaudeStreamFormatter("").FormatLine("plain stderr-ish text"));
    }

    // F15 (2026-10-15): a line that is JSON but not an event object, or an event with fields of the wrong type, must not throw -
    // on the pump thread it would end the process. Raw (not nothing) is the honest output for what is not a known event.
    [Theory]
    [InlineData("[1,2,3]")]
    [InlineData("\"just a string\"")]
    [InlineData("42")]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("{}")]
    [InlineData(/*lang=json,strict*/ """{"type":5}""")]
    [InlineData(/*lang=json,strict*/ """{"type":null}""")]
    [InlineData(/*lang=json,strict*/ """{"type":["assistant"]}""")]
    public void NonObjectJsonAndATypeThatIsNotAStringAreReturnedRaw(string line)
    {
        Assert.Equal(line, new ClaudeStreamFormatter("").FormatLine(line));
    }

    [Theory]
    [InlineData(/*lang=json,strict*/ """{"type":"assistant","message":[]}""")]
    [InlineData(/*lang=json,strict*/ """{"type":"assistant","message":"text"}""")]
    [InlineData(/*lang=json,strict*/ """{"type":"assistant","message":{"content":"text"}}""")]
    [InlineData(/*lang=json,strict*/ """{"type":"assistant","message":{"content":[7,"x",null,[1],{"type":5},{"type":"text","text":9},{"type":"tool_use","name":3},{"type":"tool_use","name":"Edit","input":[1]},{"type":"tool_use","name":"Edit","input":{"file_path":8}}]}}""")]
    [InlineData(/*lang=json,strict*/ """{"type":"user","message":{"content":"text"}}""")]
    [InlineData(/*lang=json,strict*/ """{"type":"user","message":{"content":[1,"x",null,{"type":"tool_result","is_error":"yes"},{"type":"tool_result","is_error":true,"content":7},{"type":"tool_result","is_error":true,"content":[1,{"type":"text","text":5}]}]}}""")]
    [InlineData(/*lang=json,strict*/ """{"type":"result","total_cost_usd":"cheap"}""")]
    public void FieldsOfTheWrongTypeNeverThrow(string line)
    {
        ClaudeStreamFormatter formatter = new("/work/repo");

        Exception? thrown = Record.Exception(() => formatter.FormatLine(line));

        Assert.Null(thrown);
    }
}
