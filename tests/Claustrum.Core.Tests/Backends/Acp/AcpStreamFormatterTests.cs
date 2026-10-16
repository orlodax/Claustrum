using System.Text.RegularExpressions;
using Claustrum.Core.Backends.Acp;
using static Claustrum.Core.Tests.Backends.Acp.AcpLines;

namespace Claustrum.Core.Tests.Backends.Acp;

// `--stream` and `jobs follow` for an ACP run: one readable line per event, as the other backends print, from a stream that arrives in chunks.
// Replayed lines carry no time ("--:--:--"), which makes every line comparable exactly; the live clock has its own test.
public sealed partial class AcpStreamFormatterTests
{
    private const string Dashes = "--:--:-- ";

    private static string[] Printed(AcpStreamFormatter formatter, IEnumerable<string> lines) =>
        [.. lines.Select(line => formatter.FormatLine(line, replayed: true)).OfType<string>().SelectMany(text => text.Split('\n'))];

    private static string[] Printed(string cwd, IEnumerable<string> lines) => Printed(new AcpStreamFormatter(cwd), lines);

    private static AcpStreamFormatter Open(string cwd = "/repo")
    {
        AcpStreamFormatter formatter = new(cwd);
        _ = Printed(formatter, Handshake());
        return formatter;
    }

    private static string[] Fixture(string name, string cwd = "/work") => Printed(cwd, FixtureLines(name));

    // ---- the recorded runs, line for line ------------------------------------------------------------------------

    [Fact]
    public void ARunThatWroteAFileIsToolLinesThenTheReplyCutAtOneHundredFortyCharacters()
    {
        string cwd = "/tmp/claude-1000/-home-orlodax-repos-Claustrum/95abf445-ce92-49a4-b6b9-0cfde217b268/scratchpad/smoke-a";

        string[] lines = Fixture("opencode-success.jsonl", cwd);

        Assert.Equal(4, lines.Length);
        Assert.Equal(Dashes + "SHELL ls -la && cat AGENTS.md 2>/dev/null; cat CLAUDE.md 2>/dev/null; git log --oneline -3 2>/dev/null", lines[0]);
        Assert.Equal(Dashes + "WRITE hello.txt", lines[1]);
        Assert.Equal(Dashes + "SHELL cat hello.txt; git status --short", lines[2]);
        Assert.StartsWith(Dashes + "SAYS  Created `hello.txt` in the working directory containing the single line `hi` followed by a trailing newline. Details worth flagging:", lines[3], StringComparison.Ordinal);
        Assert.EndsWith("…", lines[3], StringComparison.Ordinal);
        Assert.Equal((Dashes + "SAYS  ").Length + 140 + 1, lines[3].Length);
    }

    [Fact]
    public void ADeniedToolCallIsItsLineAndThenACrossWithTheCommandAndTheReasonItFailed()
    {
        string cwd = "/tmp/claude-1000/-home-orlodax-repos/fa4d2fa4-96b3-4cc7-8ec8-f446f0329157/scratchpad/ctl-acp-deny/repo";

        string[] lines = Fixture("opencode-report.jsonl", cwd);

        Assert.Equal(
        [
            Dashes + "SHELL ls -a && echo \"---\" && cat CLAUDE.md AGENTS.md 2>/dev/null | head -100",
            Dashes + "SHELL git status --short --branch && echo \"---\" && git remote -v",
            Dashes + "WRITE hello.txt",
            Dashes + "SHELL cat hello.txt && git status --short",
            Dashes + "SHELL git push origin HEAD 2>&1; echo \"exit=$?\"",
            Dashes + "✗     git push origin HEAD 2>&1; echo \"exit=$?\": Permission denied: shell",
            Dashes + "SHELL od -c hello.txt",
        ], lines[..7]);
        Assert.StartsWith(Dashes + "SAYS  Created `hello.txt`, but the push was blocked by the environment's permission gate.", lines[7], StringComparison.Ordinal);
        Assert.Equal(8, lines.Length);
    }

    // Thoughts are the model's private reasoning; thirty-odd chunks of them print nothing.
    [Fact]
    public void ThoughtsAndAvailableCommandsPrintNothingAndTheSessionOpensSilently()
    {
        string[] thoughtsOnly = [.. FixtureLines("opencode-report.jsonl").Where(line => line.Contains("agent_thought_chunk", StringComparison.Ordinal) || line.Contains("available_commands_update", StringComparison.Ordinal))];
        Assert.NotEmpty(thoughtsOnly);

        Assert.Empty(Printed("/work", [.. FixtureLines("opencode-report.jsonl")[..4], .. thoughtsOnly]));
    }

    [Fact]
    public void ARefusedPermissionEndsTheTurnWithAStopLineAndTheAgentsRequestPrintsNothing()
    {
        string cwd = "/tmp/claude-1000/-home-orlodax-repos/fa4d2fa4-96b3-4cc7-8ec8-f446f0329157/scratchpad/ctl-acp/repo";

        string[] lines = Fixture("opencode-cancelled.jsonl", cwd);

        Assert.Equal(
        [
            Dashes + "SHELL ls -la; echo \"---\"; git status; echo \"---\"; git log --oneline -3; echo \"---\"; git branch -vv; echo \"---\"; git remote -v",
            Dashes + "GLOB  {AGENTS.md,CLAUDE.md,README.md,CONTRIBUTING.md}",
            Dashes + "WRITE hello.txt",
            Dashes + "SHELL od -c hello.txt; echo \"--- push ---\"; git push origin HEAD 2>&1; echo \"exit=$?\"",
            Dashes + "✗     od -c hello.txt; echo \"--- push ---\"; git push origin HEAD 2>&1; echo \"exit=$?\": The user declined this tool call",
            Dashes + "STOP  cancelled",
        ], lines);
    }

    [Fact]
    public void EachTurnsReplyIsPrintedWithTheSessionsRunningTotalNotTheirSum()
    {
        string[] lines = Fixture("opencode-three-turns.jsonl");

        Assert.Equal(
        [
            Dashes + "SAYS  PINEAPPLE ($0.000789)",
            Dashes + "SAYS  MANGO ($0.001022)",
            Dashes + "SAYS  KIWI ($0.001809)",
            Dashes + "ERROR Invalid params: model not found: openrouter/no-such/model-xyz ($0.001809)",
        ], lines);
    }

    [Fact]
    public void APromptAnsweredWithAnErrorIsAnErrorLineWithItsMessage()
    {
        string[] lines = Fixture("opencode-provider-error.jsonl");

        Assert.Equal(4, lines.Length);
        Assert.All(lines[..3], line => Assert.StartsWith(Dashes + "ERROR Internal error: 0 endpoints out of 3 requested are available matching your guardrail restrictions", line, StringComparison.Ordinal));
        Assert.Equal(lines[0], lines[1]);
        Assert.Equal(Dashes + "ERROR Invalid params: model not found: openrouter/no-such/model-xyz", lines[3]);
    }

    [Fact]
    public void ARefusalInTheHandshakeIsOneErrorLine()
    {
        Assert.Equal([Dashes + "ERROR Invalid params: effort not found: bogus"], Fixture("opencode-effort-refused.jsonl"));
    }

    [Fact]
    public void AKilledRunShowsWhatItGotToAndNothingMore()
    {
        Assert.Equal([Dashes + "SHELL sleep 60"], Fixture("opencode-cut-off.jsonl"));
    }

    // The replay of a loaded session (three earlier turns, 1 400 bytes of history) is not this run's output.
    [Fact]
    public void ALoadedSessionsReplayPrintsNothingAndOnlyTheNewTurnIsShown()
    {
        Assert.Equal([Dashes + "SAYS  PLUM ($0.001985)"], Fixture("opencode-load-replay.jsonl"));
    }

    [Theory]
    [InlineData(4)]
    [InlineData(7)]
    [InlineData(10)]
    public void ARunWatchedWhileTheReplayIsStillArrivingShowsNothing(int lines)
    {
        Assert.Empty(Printed("/work", FixtureLines("opencode-load-replay.jsonl").Take(lines)));
    }

    // A replay carries tool calls too (the earlier measurement had none): they are history as well.
    [Fact]
    public void AToolCallInTheReplayPrintsNoLine()
    {
        string[] lines =
        [
            Initialized(),
            Tool("call_old", "in_progress", kind: "tool_call_update", title: "ls", command: "ls"),
            Tool("call_old", "completed", kind: "tool_call_update"),
            Message("history"),
            Loaded(),
            Configured("config-3"),
        ];

        Assert.Empty(Printed("/repo", lines));
    }

    // ---- a message arrives in chunks ----------------------------------------------------------------------------------

    [Fact]
    public void ChunksOfOneMessageArePrintedAsOneSaysLineWhenThePromptIsAnswered()
    {
        AcpStreamFormatter formatter = Open();

        Assert.Null(formatter.FormatLine(Message("Hel", "m1"), replayed: true));
        Assert.Null(formatter.FormatLine(Message("lo, wor", "m1"), replayed: true));
        Assert.Null(formatter.FormatLine(Message("ld", "m1"), replayed: true));
        Assert.Null(formatter.FormatLine(Cost(0m), replayed: true));

        Assert.Equal(Dashes + "SAYS  Hello, world", formatter.FormatLine(Answered("prompt-5"), replayed: true));
    }

    [Fact]
    public void ANewMessageFlushesTheOneBeforeItAtItsFirstChunk()
    {
        AcpStreamFormatter formatter = Open();
        Assert.Null(formatter.FormatLine(Message("before the tool", "m1"), replayed: true));

        Assert.Equal(Dashes + "SAYS  before the tool", formatter.FormatLine(Message("after", "m2"), replayed: true));
        Assert.Equal(Dashes + "SAYS  after", formatter.FormatLine(Answered("prompt-5"), replayed: true));
    }

    [Fact]
    public void AToolLineFlushesThePendingTextAndGoesOutWithItInOneCall()
    {
        AcpStreamFormatter formatter = Open();
        Assert.Null(formatter.FormatLine(Message("running it now", "m1"), replayed: true));

        string? printed = formatter.FormatLine(Tool("c1", "in_progress", kind: "tool_call_update", name: "shell", title: "ls", command: "ls"), replayed: true);

        Assert.Equal(Dashes + "SAYS  running it now\n" + Dashes + "SHELL ls", printed);
    }

    [Fact]
    public void WhitespaceAndNewlinesFoldToOneSpaceAndControlCharactersAreEscaped()
    {
        AcpStreamFormatter formatter = Open();
        _ = formatter.FormatLine(Message("line one\n\nline   two\t\u001b[31mred", "m1"), replayed: true);

        Assert.Equal(Dashes + "SAYS  line one line two \\x1b[31mred", formatter.FormatLine(Answered("prompt-5"), replayed: true));
    }

    [Fact]
    public void ATextOfExactlyOneHundredFortyCharactersIsNotCut()
    {
        AcpStreamFormatter formatter = Open();
        _ = formatter.FormatLine(Message(new string('y', 140), "m1"), replayed: true);

        Assert.Equal(Dashes + "SAYS  " + new string('y', 140), formatter.FormatLine(Answered("prompt-5"), replayed: true));
    }

    // The line goes out the moment the text passes the limit (not at the end of the message), and the rest of that message is not printed.
    [Fact]
    public void ATextPastTheLimitIsCutWithAnEllipsisAtOnceAndTheRestOfThatMessageIsDropped()
    {
        AcpStreamFormatter formatter = Open();
        Assert.Null(formatter.FormatLine(Message(new string('a', 100), "m1"), replayed: true));

        string? cut = formatter.FormatLine(Message(new string('b', 100), "m1"), replayed: true);
        string? more = formatter.FormatLine(Message("never shown", "m1"), replayed: true);
        string? end = formatter.FormatLine(Answered("prompt-5"), replayed: true);

        Assert.Equal(Dashes + "SAYS  " + new string('a', 100) + new string('b', 40) + "…", cut);
        Assert.Null(more);
        Assert.Null(end);
    }

    [Fact]
    public void AFreshMessageAfterACutOneIsPrintedAgain()
    {
        AcpStreamFormatter formatter = Open();
        _ = formatter.FormatLine(Message(new string('a', 200), "m1"), replayed: true);

        _ = formatter.FormatLine(Message("second", "m2"), replayed: true);

        Assert.Equal(Dashes + "SAYS  second", formatter.FormatLine(Answered("prompt-5"), replayed: true));
    }

    // ---- tool lines ----------------------------------------------------------------------------------------------------

    private static string Call(string status, string? name = null, string? kind = null, string? title = null, string? command = null, string? filePath = null, string? pattern = null, string? error = null, string id = "c1", string? updateKind = null) =>
        Tool(id, status, updateKind ?? (name is null ? "tool_call_update" : "tool_call"), name, kind, title, command, filePath, pattern, error);

    [Fact]
    public void AToolIsPrintedOnceWhenItStartsRunningWithTheTitleItsGiven()
    {
        AcpStreamFormatter formatter = Open();

        Assert.Null(formatter.FormatLine(Call("pending", name: "shell", kind: "execute", title: "shell"), replayed: true));
        Assert.Equal(Dashes + "SHELL ls -la", formatter.FormatLine(Call("in_progress", kind: "execute", title: "ls -la", command: "ls -la"), replayed: true));
        Assert.Null(formatter.FormatLine(Call("in_progress", kind: "execute", title: "ls -la", command: "ls -la"), replayed: true));
        Assert.Null(formatter.FormatLine(Call("completed"), replayed: true));
    }

    // opencode names the tool (`shell`, `write`) on the first `tool_call` only; the later updates carry the kind (`execute`). Read naively, every label became EXECU.
    [Fact]
    public void TheToolNameSeenOnTheFirstFrameIsKeptForTheLabelWhenLaterOnesCarryOnlyAKind()
    {
        AcpStreamFormatter formatter = Open();
        _ = formatter.FormatLine(Call("pending", name: "write", kind: "edit", title: "write"), replayed: true);

        Assert.Equal(Dashes + "WRITE a.txt", formatter.FormatLine(Call("in_progress", kind: "edit", title: "write", filePath: "/repo/a.txt"), replayed: true));
    }

    [Theory]
    [InlineData("shell", "execute", "SHELL")]
    [InlineData(null, "execute", "EXECU")]
    [InlineData(null, "edit", "EDIT")]
    [InlineData("webfetch", null, "WEBFE")]
    [InlineData("grep", null, "GREP")]
    [InlineData(null, null, "TOOL")]
    public void TheLabelIsTheToolNameElseItsKindFiveLettersUpperCased(string? name, string? kind, string label)
    {
        AcpStreamFormatter formatter = Open();

        string? printed = formatter.FormatLine(Call("in_progress", name: name, kind: kind, title: "x", command: "go", updateKind: "tool_call"), replayed: true);

        Assert.Equal($"{Dashes}{label,-5} go", printed);
    }

    [Fact]
    public void TheDetailIsTheCommandElseAFilePathRelativeToTheDirectoryElseThePatternElseTheTitle()
    {
        AcpStreamFormatter formatter = Open("/repo");

        Assert.Equal(Dashes + "SHELL echo hi", formatter.FormatLine(Call("in_progress", name: "shell", title: "t1", command: "echo hi", filePath: "/repo/ignored", pattern: "ignored", id: "a"), replayed: true));
        Assert.Equal(Dashes + "EDIT  src/a.cs", formatter.FormatLine(Call("in_progress", name: "edit", title: "t2", filePath: "/repo/src/a.cs", pattern: "ignored", id: "b"), replayed: true));
        Assert.Equal(Dashes + "GREP  TODO", formatter.FormatLine(Call("in_progress", name: "grep", title: "t3", pattern: "TODO", id: "c"), replayed: true));
        Assert.Equal(Dashes + "TASK  spawn a helper", formatter.FormatLine(Call("in_progress", name: "task", title: "spawn a helper", id: "d"), replayed: true));
    }

    [Fact]
    public void APathOutsideTheDirectoryOrThePathOfTheDirectoryItselfStaysAsGiven()
    {
        AcpStreamFormatter formatter = Open("/repo");

        Assert.Equal(Dashes + "READ  /etc/hosts", formatter.FormatLine(Call("in_progress", name: "read", title: "t", filePath: "/etc/hosts", id: "a"), replayed: true));
        Assert.Equal(Dashes + "READ  /repo", formatter.FormatLine(Call("in_progress", name: "read", title: "t", filePath: "/repo", id: "b"), replayed: true));
        Assert.Equal(Dashes + "READ  /repo/", formatter.FormatLine(Call("in_progress", name: "read", title: "t", filePath: "/repo/", id: "c"), replayed: true));
    }

    [Fact]
    public void AToolWithNoTitleIsPrintedOnlyOnceTheTitleIsKnown()
    {
        AcpStreamFormatter formatter = Open();

        Assert.Null(formatter.FormatLine(Call("in_progress", name: "shell"), replayed: true));
        Assert.Equal(Dashes + "SHELL ls", formatter.FormatLine(Call("completed", title: "ls", command: "ls"), replayed: true));
    }

    [Fact]
    public void AToolThatIsOnlyEverPendingPrintsNothing()
    {
        AcpStreamFormatter formatter = Open();

        Assert.Null(formatter.FormatLine(Call("pending", name: "shell", title: "shell"), replayed: true));
        Assert.Null(formatter.FormatLine(Call("pending", title: "ls", command: "ls"), replayed: true));
    }

    [Fact]
    public void ACallThatFailedIsACrossWithItsTitleAndErrorAfterItsLineAndAfterOneOnlyOnce()
    {
        AcpStreamFormatter formatter = Open();
        _ = formatter.FormatLine(Call("in_progress", name: "shell", title: "git push", command: "git push"), replayed: true);

        Assert.Equal(Dashes + "✗     git push: denied", formatter.FormatLine(Call("failed", title: "git push", error: "denied"), replayed: true));
        Assert.Null(formatter.FormatLine(Call("failed", title: "git push", error: "denied"), replayed: true));
    }

    [Fact]
    public void ACallThatFailedWithNoErrorIsJustItsTitleAndOneThatNeverStartedIsStillReported()
    {
        AcpStreamFormatter formatter = Open();

        Assert.Equal(Dashes + "✗     rm -rf /", formatter.FormatLine(Call("failed", name: "shell", title: "rm -rf /"), replayed: true));
        Assert.Equal(Dashes + "✗     WEBFE", formatter.FormatLine(Call("failed", name: "webfetch", id: "n"), replayed: true));
    }

    [Fact]
    public void AFailedCallFlushesThePendingTextFirst()
    {
        AcpStreamFormatter formatter = Open();
        _ = formatter.FormatLine(Message("trying", "m1"), replayed: true);

        Assert.Equal(Dashes + "SAYS  trying\n" + Dashes + "✗     boom: it broke", formatter.FormatLine(Call("failed", title: "boom", error: "it broke"), replayed: true));
    }

    // ---- errors, stops, cost -------------------------------------------------------------------------------------------

    [Fact]
    public void AnErrorAnswerFlushesTheTextAndIsAnErrorLine()
    {
        AcpStreamFormatter formatter = Open();
        _ = formatter.FormatLine(Message("half done", "m1"), replayed: true);

        Assert.Equal(Dashes + "SAYS  half done\n" + Dashes + "ERROR the provider is down", formatter.FormatLine(Failure("prompt-5", -32603, "the provider is down"), replayed: true));
    }

    [Fact]
    public void AnErrorWithNoMessageIsSaidInTheClientsWordsAndAControlCharacterInOneIsEscaped()
    {
        AcpStreamFormatter formatter = Open();

        string bare = Frame(new { jsonrpc = "2.0", id = "prompt-5", error = new { code = -32603 } });
        Assert.Equal(Dashes + "ERROR the agent answered with an error", formatter.FormatLine(bare, replayed: true));
        Assert.Equal(Dashes + "ERROR bad \\x07 bell", formatter.FormatLine(Failure("prompt-6", -32603, "bad \u0007 bell"), replayed: true));
    }

    [Theory]
    [InlineData("refusal")]
    [InlineData("max_tokens")]
    [InlineData("max_turn_requests")]
    [InlineData("cancelled")]
    public void ATurnThatDidNotEndNormallyIsAStopLineAfterItsText(string reason)
    {
        AcpStreamFormatter formatter = Open();
        _ = formatter.FormatLine(Message("partial", "m1"), replayed: true);

        Assert.Equal($"{Dashes}SAYS  partial\n{Dashes}STOP  {reason}", formatter.FormatLine(Answered("prompt-5", reason), replayed: true));
    }

    [Fact]
    public void AnEndTurnPrintsNoStopLineAndAnAnswerWithNoTextPrintsNothing()
    {
        AcpStreamFormatter formatter = Open();

        Assert.Null(formatter.FormatLine(Answered("prompt-5"), replayed: true));
    }

    [Fact]
    public void TheCostSuffixIsTheLastUsageUpdateAssignedNeverAdded()
    {
        AcpStreamFormatter formatter = Open();
        _ = formatter.FormatLine(Cost(0.10m), replayed: true);
        _ = formatter.FormatLine(Cost(0.30m), replayed: true);
        _ = formatter.FormatLine(Message("hi", "m1"), replayed: true);

        Assert.Equal(Dashes + "SAYS  hi ($0.300000)", formatter.FormatLine(Answered("prompt-5"), replayed: true));
    }

    [Fact]
    public void TheCostSuffixGoesOnToolLinesToo()
    {
        AcpStreamFormatter formatter = Open();
        _ = formatter.FormatLine(Cost(0.5m), replayed: true);

        Assert.Equal(Dashes + "SHELL ls ($0.500000)", formatter.FormatLine(Call("in_progress", name: "shell", title: "ls", command: "ls"), replayed: true));
    }

    [Theory]
    [InlineData("EUR", 0.5)]
    [InlineData("USD", 0)]
    public void NoSuffixIsPrintedForAZeroTotalOrOneInAnotherCurrency(string currency, double amount)
    {
        AcpStreamFormatter formatter = Open();
        _ = formatter.FormatLine(Cost((decimal)amount, currency), replayed: true);
        _ = formatter.FormatLine(Message("hi", "m1"), replayed: true);

        Assert.Equal(Dashes + "SAYS  hi", formatter.FormatLine(Answered("prompt-5"), replayed: true));
    }

    // ---- what is not shown ------------------------------------------------------------------------------------------------

    [Fact]
    public void NothingIsPrintedBeforeTheSessionIsOpen()
    {
        AcpStreamFormatter formatter = new("/repo");

        Assert.Null(formatter.FormatLine(Initialized(), replayed: true));
        Assert.Null(formatter.FormatLine(Message("too early", "m1"), replayed: true));
        Assert.Null(formatter.FormatLine(Call("in_progress", name: "shell", title: "ls", command: "ls"), replayed: true));
        Assert.Null(formatter.FormatLine(Opened(), replayed: true));
        Assert.Null(formatter.FormatLine(Answered("prompt-5"), replayed: true));
    }

    [Fact]
    public void ThoughtsUserEchoesPlansCommandsAndAnotherSessionsUpdatesPrintNothing()
    {
        AcpStreamFormatter formatter = Open();

        Assert.Null(formatter.FormatLine(Thought("pondering"), replayed: true));
        Assert.Null(formatter.FormatLine(User("the brief"), replayed: true));
        Assert.Null(formatter.FormatLine(Plan(), replayed: true));
        Assert.Null(formatter.FormatLine(Commands(), replayed: true));
        Assert.Null(formatter.FormatLine(Message("the subagent", "m9", session: "ses_child"), replayed: true));
        Assert.Null(formatter.FormatLine(Call("failed", title: "child call", error: "x", id: "child").Replace(Session, "ses_child", StringComparison.Ordinal), replayed: true));
        Assert.Null(formatter.FormatLine(Cost(9m, session: "ses_child"), replayed: true));

        // The child's $9 was never the run's total.
        _ = formatter.FormatLine(Message("mine", "m1"), replayed: true);
        Assert.Equal(Dashes + "SAYS  mine", formatter.FormatLine(Answered("prompt-5"), replayed: true));
    }

    [Fact]
    public void AnAgentsOwnRequestAndUnknownNotificationsPrintNothing()
    {
        AcpStreamFormatter formatter = Open();
        string permission = Frame(new { jsonrpc = "2.0", id = 0, method = "session/request_permission", @params = new { sessionId = Session, options = Array.Empty<object>() } });
        string other = Frame(new { jsonrpc = "2.0", method = "some/notification", @params = new { } });

        Assert.Null(formatter.FormatLine(permission, replayed: true));
        Assert.Null(formatter.FormatLine(other, replayed: true));
        Assert.Null(formatter.FormatLine("{}", replayed: true));
        Assert.Null(formatter.FormatLine(Update(Session, new { sessionUpdate = "a_future_kind" }), replayed: true));
    }

    // ---- not JSON-RPC at all ----------------------------------------------------------------------------------------------

    [Fact]
    public void ALineThatIsNotAFrameIsPassedThroughSanitized()
    {
        AcpStreamFormatter formatter = new("/repo");

        Assert.Equal("opencode: warning: something", formatter.FormatLine("opencode: warning: something"));
        Assert.Equal("{broken", formatter.FormatLine("{broken"));
        Assert.Equal("[1,2]", formatter.FormatLine("[1,2]"));
        Assert.Equal("\\x1b[2Jboo", formatter.FormatLine("\u001b[2Jboo"));
        Assert.Null(formatter.FormatLine(""));
        Assert.Null(formatter.FormatLine("   \t "));
    }

    // ---- the clock -----------------------------------------------------------------------------------------------------------

    [Fact]
    public void AFrameCarriesNoTimeSoALiveLineShowsTheClockAndAReplayedOneDashes()
    {
        AcpStreamFormatter live = Open();
        AcpStreamFormatter replay = Open();
        _ = live.FormatLine(Message("hi", "m1"));
        _ = replay.FormatLine(Message("hi", "m1"), replayed: true);

        Assert.Matches(Clock(), live.FormatLine(Answered("prompt-5")));
        Assert.Equal("--:--:-- SAYS  hi", replay.FormatLine(Answered("prompt-5"), replayed: true));
    }

    [GeneratedRegex(@"^\d{2}:\d{2}:\d{2} SAYS  hi$")]
    private static partial Regex Clock();

    [Fact]
    public void TheFormatterIsTheBackendsStreamFormatterForThisCwd()
    {
        AcpStreamFormatter formatter = Assert.IsType<AcpStreamFormatter>(Backend().CreateStreamFormatter("/repo"));
        _ = formatter.FormatLine(Opened(), replayed: true);

        Assert.Equal(Dashes + "EDIT  src/a.cs", formatter.FormatLine(Call("completed", name: "edit", title: "t", filePath: "/repo/src/a.cs"), replayed: true));
    }
}
