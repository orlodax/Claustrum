using System.Globalization;
using System.Text.RegularExpressions;
using Claustrum.Core.Backends.Claude;

namespace Claustrum.Core.Tests.Backends.Claude;

// #123: one readable line per event of `claude --output-format stream-json`, against a genuine capture
// (fixtures/claude/success.jsonl) and a hand-trimmed one with tool calls (stream-tools.jsonl, same event shapes). The
// clock prefix follows the event's timestamp in local time, so lines are compared with it stripped.
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

    [Fact]
    public void TheGenuineCaptureYieldsOnePongLineAndNothingElse()
    {
        ClaudeStreamFormatter formatter = new("C:\\work\\claustrum-fixture");

        string[] printed = [.. Lines("success.jsonl").Select(formatter.FormatLine).OfType<string>()];

        Assert.Equal(["SAYS  pong"], printed.Select(Bare));
    }

    // The result event carries the run's total; a line printed after it ends with the running cost, six decimals.
    [Fact]
    public void ACostTotalFromTheResultEventSuffixesTheLinesAfterIt()
    {
        ClaudeStreamFormatter formatter = new("/work/repo");
        string[] lines = Lines("stream-tools.jsonl");

        Assert.Null(formatter.FormatLine(lines[^1]));
        string after = Bare(formatter.FormatLine(lines[3]));

        Assert.Equal("BASH  dotnet build ($" + 0.0123m.ToString("F6", CultureInfo.InvariantCulture) + ")", after);
    }

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
    [InlineData(/*lang=json,strict*/ """{"type":"assistant","timestamp":12345,"message":{"content":[{"type":"text","text":"hi"}]}}""")]
    [InlineData(/*lang=json,strict*/ """{"type":"assistant","timestamp":"not a date","message":{"content":[{"type":"text","text":"hi"}]}}""")]
    public void FieldsOfTheWrongTypeNeverThrow(string line)
    {
        ClaudeStreamFormatter formatter = new("/work/repo");

        Exception? thrown = Record.Exception(() => formatter.FormatLine(line));

        Assert.Null(thrown);
    }
}
