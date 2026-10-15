using System.Globalization;
using System.Text.RegularExpressions;
using Claustrum.Core.Backends.Opencode;

namespace Claustrum.Core.Tests.Backends.Opencode;

// #123: one readable line per event of `opencode run --format json`, against the genuine 2.0.12 captures
// (success.jsonl, error.jsonl) and the hand-trimmed three-step stream. The clock prefix follows the event's `timestamp`
// in local time, so lines are compared with it stripped.
public sealed partial class OpencodeStreamFormatterTests
{
    private const string CaptureCwd = "/tmp/claustrum-opencode-capture";

    private static string[] Lines(string name) => File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "fixtures", "opencode", name));

    private static string Bare(string? line)
    {
        Assert.NotNull(line);
        Assert.Matches(Clock(), line);
        return Clock().Replace(line, "");
    }

    [GeneratedRegex(@"^\d{2}:\d{2}:\d{2} ")]
    private static partial Regex Clock();

    [Fact]
    public void TheGenuineCaptureIsAToolLineAndASaysLineWithBookkeepingSilent()
    {
        OpencodeStreamFormatter formatter = new(CaptureCwd);

        string?[] printed = [.. Lines("success.jsonl").Select(formatter.FormatLine)];

        Assert.Null(printed[0]);
        Assert.Equal("WRITE hello.txt", Bare(printed[1]));
        Assert.Null(printed[2]);
        Assert.Null(printed[3]);
        Assert.StartsWith("SAYS  ", Bare(printed[4]), StringComparison.Ordinal);
    }

    // step_finish prints nothing itself but adds its cost to the running total every later line ends with.
    [Fact]
    public void TheCostOfFinishedStepsSuffixesTheLinesAfterThem()
    {
        OpencodeStreamFormatter formatter = new(CaptureCwd);
        string[] lines = Lines("success.jsonl");

        _ = formatter.FormatLine(lines[0]);
        _ = formatter.FormatLine(lines[1]);
        Assert.Null(formatter.FormatLine(lines[2]));
        Assert.Null(formatter.FormatLine(lines[3]));
        string says = Bare(formatter.FormatLine(lines[4]));

        Assert.EndsWith(" ($" + 0.0004988494m.ToString("F6", CultureInfo.InvariantCulture) + ")", says, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCostTotalAddsUpAcrossSteps()
    {
        OpencodeStreamFormatter formatter = new("/work/repo");
        string[] lines = Lines("success-no-final-step-finish.jsonl");

        string?[] printed = [.. lines.Select(formatter.FormatLine)];

        Assert.Equal("READ  src/a.txt", Bare(printed[1]));
        Assert.Equal("BASH  echo hi ($0.000400)", Bare(printed[4]));
        Assert.Equal("SAYS  All done. ($0.001000)", Bare(printed[7]));
    }

    [Fact]
    public void ARealErrorEventIsOneErrorLineWithTheMessage()
    {
        OpencodeStreamFormatter formatter = new(CaptureCwd);

        string?[] printed = [.. Lines("error.jsonl").Select(formatter.FormatLine)];

        Assert.Null(printed[0]);
        Assert.Equal("ERROR User not found.", Bare(printed[1]));
    }

    [Fact]
    public void TheV1ErrorShapeIsReadToo()
    {
        OpencodeStreamFormatter formatter = new(CaptureCwd);

        string?[] printed = [.. Lines("error-v1.jsonl").Select(formatter.FormatLine)];

        Assert.Contains(printed, line => line is not null && line.Contains("ERROR", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(/*lang=json,strict*/ """{"type":"tool_use","part":{"tool":"edit","state":{"status":"completed","input":{"filePath":"/work/repo/a.cs"}}}}""", "EDIT  a.cs")]
    [InlineData(/*lang=json,strict*/ """{"type":"tool_use","part":{"tool":"bash","state":{"status":"completed","input":{"command":"dotnet test"}}}}""", "BASH  dotnet test")]
    [InlineData(/*lang=json,strict*/ """{"type":"tool_use","part":{"tool":"grep","state":{"status":"pending","input":{"pattern":"TODO"}}}}""", "GREP  TODO")]
    [InlineData(/*lang=json,strict*/ """{"type":"tool_use","part":{"tool":"webfetch","state":{"status":"completed","input":{}}}}""", "WEBFE")]
    [InlineData(/*lang=json,strict*/ """{"type":"tool_use","part":{"tool":"read","state":{"status":"completed","input":{"path":"/work/repo/b.md"}}}}""", "READ  b.md")]
    public void ToolLinesTakeTheLabelAndTheMostRelevantInput(string line, string expected)
    {
        OpencodeStreamFormatter formatter = new("/work/repo");

        Assert.Equal(expected, Bare(formatter.FormatLine(line)).TrimEnd());
    }

    [Fact]
    public void AFailedToolCallIsACrossWithItsError()
    {
        OpencodeStreamFormatter formatter = new("/work/repo");
        string line = /*lang=json,strict*/ """{"type":"tool_use","part":{"tool":"bash","state":{"status":"error","input":{"command":"false"},"error":"exit 1"}}}""";

        Assert.Equal("✗     exit 1", Bare(formatter.FormatLine(line)));
    }

    [Fact]
    public void LongTextIsCutAtOneHundredFortyCharactersWithAnEllipsis()
    {
        OpencodeStreamFormatter formatter = new("");
        string line = $$$"""{"type":"text","part":{"id":"p1","text":"{{{new string('y', 200)}}}"}}""";

        Assert.Equal("SAYS  " + new string('y', 140) + "…", Bare(formatter.FormatLine(line)));
    }

    [Theory]
    [InlineData("step_start")]
    [InlineData("heartbeat")]
    public void StepBoundariesAndHeartbeatsPrintNothing(string type)
    {
        Assert.Null(new OpencodeStreamFormatter("").FormatLine($$$"""{"type":"{{{type}}}","part":{"id":"p"}}"""));
    }

    [Fact]
    public void ALineThatIsNotJsonIsPassedThroughAndAnUnknownEventTypePrintsNothing()
    {
        OpencodeStreamFormatter formatter = new("");

        Assert.Equal("plain text", formatter.FormatLine("plain text"));
        Assert.Null(formatter.FormatLine(/*lang=json,strict*/ """{"type":"future_event","x":1}"""));
        Assert.Null(formatter.FormatLine(/*lang=json,strict*/ """{"type":"future_event","x":1}""", replayed: true));
    }

    // The event's own time wins; with none, a replay prints dashes and a live line the clock.
    [Fact]
    public void ALineWithATimestampShowsItAndOneWithoutShowsDashesOnReplayAndTheClockLive()
    {
        OpencodeStreamFormatter formatter = new("");
        string stamped = /*lang=json,strict*/ """{"type":"text","timestamp":1760518800000,"part":{"text":"hi"}}""";
        string bare = /*lang=json,strict*/ """{"type":"text","part":{"text":"hi"}}""";
        string expected = DateTimeOffset.FromUnixTimeMilliseconds(1760518800000).ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);

        Assert.Equal(expected + " SAYS  hi", formatter.FormatLine(stamped, replayed: true));
        Assert.Equal("--:--:-- SAYS  hi", formatter.FormatLine(bare, replayed: true));
        Assert.Matches(@"^\d{2}:\d{2}:\d{2} SAYS  hi$", formatter.FormatLine(bare, replayed: false));
    }

    // ---- nothing a model, a tool or a file says reaches the terminal as a control byte ---------------------------------------

    private static void AssertNoControlCharacters(string text) =>
        Assert.False(text.Any(character => character is < ' ' or '\u007f' or (>= '\u0080' and <= '\u009f')), "a control character reached the output");

    [Fact]
    public void ControlCharactersInModelTextAreVisibleEscapesAndTheLinesAreFolded()
    {
        string line = /*lang=json,strict*/ """{"type":"text","part":{"text":"a\u001b[2Jb\u0007c\u009bd\u007fe\nnext"}}""";

        string said = Bare(new OpencodeStreamFormatter("").FormatLine(line));

        Assert.Equal("SAYS  a\\x1b[2Jb\\x07c\\u009bd\\x7fe next", said);
        AssertNoControlCharacters(said);
    }

    [Theory]
    [InlineData(/*lang=json,strict*/ """{"type":"tool_use","part":{"tool":"bash","state":{"status":"completed","input":{"command":"echo safe\n\u001b[1Arm -rf /\r\necho end"}}}}""", "BASH  echo safe \\x1b[1Arm -rf / echo end")]
    [InlineData(/*lang=json,strict*/ """{"type":"tool_use","part":{"tool":"edit","state":{"status":"completed","input":{"filePath":"/x/\u001b]0;pwned\u0007.cs"}}}}""", "EDIT  /x/\\x1b]0;pwned\\x07.cs")]
    [InlineData(/*lang=json,strict*/ """{"type":"tool_use","part":{"tool":"grep","state":{"status":"pending","input":{"pattern":"a\u009bb"}}}}""", "GREP  a\\u009bb")]
    [InlineData(/*lang=json,strict*/ """{"type":"tool_use","part":{"tool":"bash","state":{"status":"error","error":"boom\u001b[31m\nnext"}}}""", "✗     boom\\x1b[31m next")]
    [InlineData(/*lang=json,strict*/ """{"type":"error","error":{"message":"bad\u001b[2J\nthing"}}""", "ERROR bad\\x1b[2J thing")]
    public void ControlCharactersInAToolTargetCommandOrErrorAreEscapedAndMultiLineFoldsToOne(string line, string expected)
    {
        string printed = new OpencodeStreamFormatter("").FormatLine(line)!;

        Assert.Equal(expected, Bare(printed));
        AssertNoControlCharacters(printed);
    }

    [Fact]
    public void AToolNameWithControlCharactersIsEscapedInItsLabelToo()
    {
        string printed = new OpencodeStreamFormatter("").FormatLine(/*lang=json,strict*/ """{"type":"tool_use","part":{"tool":"\u001b[1mx","state":{"status":"completed","input":{}}}}""")!;

        AssertNoControlCharacters(printed);
    }

    [Theory]
    [InlineData("plain \u001b[2J text", "plain \\x1b[2J text")]
    [InlineData("\u009b31m", "\\u009b31m")]
    public void APassedThroughLineIsSanitizedToo(string raw, string expected)
    {
        Assert.Equal(expected, new OpencodeStreamFormatter("").FormatLine(raw));
    }

    // F15 (2026-10-15): JSON that is no event object, and events with fields of the wrong type, must not throw on the pump thread.
    [Theory]
    [InlineData("[1,2,3]")]
    [InlineData("\"just a string\"")]
    [InlineData("42")]
    [InlineData("null")]
    [InlineData("false")]
    [InlineData("{}")]
    [InlineData(/*lang=json,strict*/ """{"type":5}""")]
    [InlineData(/*lang=json,strict*/ """{"type":{"a":1}}""")]
    public void NonObjectJsonAndATypeThatIsNotAStringAreReturnedRaw(string line)
    {
        Assert.Equal(line, new OpencodeStreamFormatter("").FormatLine(line));
    }

    [Theory]
    [InlineData(/*lang=json,strict*/ """{"type":"error","error":"just text"}""")]
    [InlineData(/*lang=json,strict*/ """{"type":"error","error":{"message":5}}""")]
    [InlineData(/*lang=json,strict*/ """{"type":"error","error":{"data":"x"}}""")]
    [InlineData(/*lang=json,strict*/ """{"type":"error","error":{"data":{"message":7}}}""")]
    [InlineData(/*lang=json,strict*/ """{"type":"text","part":"nope"}""")]
    [InlineData(/*lang=json,strict*/ """{"type":"text","part":{"text":5}}""")]
    [InlineData(/*lang=json,strict*/ """{"type":"text","timestamp":"soon","part":{"text":"hi"}}""")]
    [InlineData(/*lang=json,strict*/ """{"type":"tool_use","part":"nope"}""")]
    [InlineData(/*lang=json,strict*/ """{"type":"tool_use","part":{"tool":7}}""")]
    [InlineData(/*lang=json,strict*/ """{"type":"tool_use","part":{"tool":"edit","state":"done"}}""")]
    [InlineData(/*lang=json,strict*/ """{"type":"tool_use","part":{"tool":"edit","state":{"status":5,"input":[1]}}}""")]
    [InlineData(/*lang=json,strict*/ """{"type":"tool_use","part":{"tool":"edit","state":{"status":"completed","input":{"filePath":3,"path":4,"command":5,"pattern":6}}}}""")]
    [InlineData(/*lang=json,strict*/ """{"type":"tool_use","part":{"tool":"edit","state":{"status":"error","error":{"a":1}}}}""")]
    [InlineData(/*lang=json,strict*/ """{"type":"step_finish","part":{"cost":"free"}}""")]
    [InlineData(/*lang=json,strict*/ """{"type":"step_finish","part":{"cost":1e999}}""")]
    [InlineData(/*lang=json,strict*/ """{"type":"step_finish","part":[1]}""")]
    public void FieldsOfTheWrongTypeNeverThrow(string line)
    {
        OpencodeStreamFormatter formatter = new("/work/repo");

        Exception? thrown = Record.Exception(() => formatter.FormatLine(line));

        Assert.Null(thrown);
    }
}
