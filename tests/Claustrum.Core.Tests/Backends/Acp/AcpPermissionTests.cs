using System.Text.Json;
using Claustrum.Core.Model;
using static Claustrum.Core.Tests.Backends.Acp.AcpLines;

namespace Claustrum.Core.Tests.Backends.Acp;

// An agent's `session/request_permission` is answered by the client at once, through the driver: allowed the way the native backend's `--auto` allows,
// unless a deny pattern names the command. A backup, never the enforcement point: the deny list is the harness's own config (NOTES.md "ACP as a
// transport"). Answered here against the shapes opencode really sends (the cancelled fixture's own request and the answer the probe recorded for it).
public sealed class AcpPermissionTests
{
    private static readonly (string Id, string Kind)[] standard = [("once", "allow_once"), ("always", "allow_always"), ("reject", "reject_once")];

    private static string Selected(object id, string optionId) => $"{{\"jsonrpc\":\"2.0\",\"id\":{Raw(id)},\"result\":{{\"outcome\":{{\"outcome\":\"selected\",\"optionId\":\"{optionId}\"}}}}}}";

    private static string Cancelled(object id) => $"{{\"jsonrpc\":\"2.0\",\"id\":{Raw(id)},\"result\":{{\"outcome\":{{\"outcome\":\"cancelled\"}}}}}}";

    private static string Raw(object id) => JsonSerializer.Serialize(id);

    private static string Request(object id, string? command, (string Id, string Kind)[] options, string? title = null)
    {
        Dictionary<string, object> toolCall = new() { ["toolCallId"] = "call_1", ["kind"] = "execute", ["status"] = "pending" };
        if (title is not null)
            toolCall["title"] = title;
        if (command is not null)
            toolCall["rawInput"] = new { command };

        return Frame(new
        {
            jsonrpc = "2.0",
            id,
            method = "session/request_permission",
            @params = new { sessionId = Session, toolCall, options = options.Select(option => new { optionId = option.Id, kind = option.Kind, name = option.Id }) },
        });
    }

    private static async Task<string> AnswerAsync(string request, string[]? deny = null)
    {
        await using DriverRig rig = new(Run(permission: new PermissionPolicy(PermissionLevel.EditShell, deny ?? [])));
        _ = await rig.OpenAsync();

        rig.Agent(request);

        return (await rig.NextAsync()).Raw;
    }

    // ---- what is allowed --------------------------------------------------------------------------------------------

    [Fact]
    public async Task APermissionRequestIsAnsweredAllowOnceByDefaultWithTheSameNumericIdAsync()
    {
        string answer = await AnswerAsync(Request(0, "ls -la", standard));

        Assert.Equal(Selected(0, "once"), answer);
    }

    [Fact]
    public async Task AStringIdIsHandedBackAsAStringAndALargeNumberAsTheSameNumberAsync()
    {
        Assert.Equal(Selected("perm-1", "once"), await AnswerAsync(Request("perm-1", "ls", standard)));
        Assert.Equal(Selected(123456789012L, "once"), await AnswerAsync(Request(123456789012L, "ls", standard)));
    }

    [Fact]
    public async Task AllowOnceIsPreferredToAllowAlwaysWhateverTheOrderOfTheOptionsAsync()
    {
        string answer = await AnswerAsync(Request(1, "ls", [("always", "allow_always"), ("reject", "reject_once"), ("once", "allow_once")]));

        Assert.Equal(Selected(1, "once"), answer);
    }

    [Fact]
    public async Task AllowAlwaysIsTheAnswerWhenThereIsNoAllowOnceAsync()
    {
        Assert.Equal(Selected(1, "always"), await AnswerAsync(Request(1, "ls", [("reject", "reject_once"), ("always", "allow_always")])));
    }

    [Fact]
    public async Task TheFirstOptionIsTheAnswerWhenNeitherAllowKindIsOfferedAndNothingIsDeniedAsync()
    {
        Assert.Equal(Selected(1, "x"), await AnswerAsync(Request(1, "ls", [("x", "some_future_kind"), ("y", "another")])));
    }

    [Fact]
    public async Task OfTwoOptionsOfTheSameKindTheFirstWinsAsync()
    {
        Assert.Equal(Selected(1, "first"), await AnswerAsync(Request(1, "ls", [("first", "allow_once"), ("second", "allow_once")])));
    }

    [Fact]
    public async Task ARequestWithNoOptionsAtAllIsAnsweredCancelledNotWithAnInventedChoiceAsync()
    {
        Assert.Equal(Cancelled(1), await AnswerAsync(Request(1, "ls", [])));
    }

    [Fact]
    public async Task ARequestThatNamesNoCommandAtAllIsNotDeniedWhateverTheListHoldsAsync()
    {
        Assert.Equal(Selected(1, "once"), await AnswerAsync(Request(1, command: null, standard), ["git push", "*"]));
    }

    // ---- what is denied ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task ADeniedCommandIsRejectedOnceWhenTheAgentOffersItAsync()
    {
        string answer = await AnswerAsync(Request(0, "git push origin HEAD", standard), ["git push"]);

        Assert.Equal(Selected(0, "reject"), answer);
    }

    [Fact]
    public async Task RejectOnceIsPreferredToRejectAlwaysAndRejectAlwaysIsUsedWhenItIsAllThereIsAsync()
    {
        Assert.Equal(Selected(1, "no"), await AnswerAsync(Request(1, "git push", [("always", "allow_always"), ("never", "reject_always"), ("no", "reject_once")]), ["git push"]));
        Assert.Equal(Selected(1, "never"), await AnswerAsync(Request(1, "git push", [("once", "allow_once"), ("never", "reject_always")]), ["git push"]));
    }

    // A refused call never falls back to "the first option", which could be an allow.
    [Fact]
    public async Task ADeniedCommandWithNoRejectOptionIsCancelledNeverAllowedAsync()
    {
        Assert.Equal(Cancelled(2), await AnswerAsync(Request(2, "git push", [("once", "allow_once"), ("always", "allow_always")]), ["git push"]));
        Assert.Equal(Cancelled(2), await AnswerAsync(Request(2, "git push", []), ["git push"]));
        Assert.Equal(Cancelled(2), await AnswerAsync(Request(2, "git push", [("x", "some_future_kind")]), ["git push"]));
    }

    [Fact]
    public async Task TheTitleStandsInForTheCommandWhenTheCallCarriesNoRawInputAsync()
    {
        Assert.Equal(Selected(3, "reject"), await AnswerAsync(Request(3, command: null, standard, title: "git push origin HEAD"), ["git push"]));
        Assert.Equal(Selected(3, "once"), await AnswerAsync(Request(3, command: null, standard, title: "git status"), ["git push"]));
    }

    [Fact]
    public async Task TheCommandInTheRawInputWinsOverTheTitleAsync()
    {
        Assert.Equal(Selected(3, "once"), await AnswerAsync(Request(3, "ls", standard, title: "git push"), ["git push"]));
        Assert.Equal(Selected(3, "reject"), await AnswerAsync(Request(3, "git push", standard, title: "ls"), ["git push"]));
    }

    // opencode's own request for the run the probe recorded, and the answer the probe sent for it ("reject"): the same bytes.
    [Fact]
    public async Task TheRealRequestOpencodeSentIsRejectedWithTheRecordedAnswerWhenItsCommandRunsAGitPushAsync()
    {
        string request = FixtureLines("opencode-cancelled.jsonl")[46];
        string recorded = FixtureLines("opencode-cancelled.client.jsonl")[^1];
        Assert.Contains("\"method\":\"session/request_permission\"", request, StringComparison.Ordinal);

        string answer = await AnswerAsync(request, ["git push"]);

        Assert.Equal(recorded, answer);
    }

    [Fact]
    public async Task TheSameRealRequestIsAllowedOnceWhenNothingIsDeniedAsync()
    {
        string answer = await AnswerAsync(FixtureLines("opencode-cancelled.jsonl")[46]);

        Assert.Equal(Selected(0, "once"), answer);
    }

    // ---- how a pattern names a command ------------------------------------------------------------------------------------

    [Theory]
    // the pattern is the command, or starts it and a space
    [InlineData("git push", "git push", true)]
    [InlineData("git push", "git push origin HEAD", true)]
    [InlineData("git push", "git pushx", false)]
    [InlineData("git push", "git  push", false)]
    [InlineData("git push", "GIT PUSH", false)]
    [InlineData("git push", "echo git push", false)]
    [InlineData("git push", "git status", false)]
    [InlineData("git push", "  git push  ", true)]
    // every segment of a compound command is judged
    [InlineData("git push", "ls && git push", true)]
    [InlineData("git push", "ls || git push origin", true)]
    [InlineData("git push", "ls; git push", true)]
    [InlineData("git push", "ls | git push", true)]
    [InlineData("git push", "ls & git push", true)]
    [InlineData("git push", "ls\ngit push", true)]
    [InlineData("git push", "ls && git status; echo done", false)]
    [InlineData("git push", "echo 'git push' && ls", false)]
    // a trailing " *" is that same match; a trailing "*" a plain prefix
    [InlineData("git push *", "git push", true)]
    [InlineData("git push *", "git push origin", true)]
    [InlineData("git push *", "git pushx", false)]
    [InlineData("rm -rf*", "rm -rf /", true)]
    [InlineData("rm -rf*", "rm -rfx", true)]
    [InlineData("rm -rf*", "rm -r /", false)]
    [InlineData("rm*", "rmdir x", true)]
    [InlineData("rm *", "rmdir x", false)]
    [InlineData("rm *", "rm x", true)]
    public async Task APatternNamesACommandAsTheDenySemanticsSayAsync(string pattern, string command, bool denied)
    {
        string answer = await AnswerAsync(Request(0, command, standard), [pattern]);

        Assert.Equal(denied ? Selected(0, "reject") : Selected(0, "once"), answer);
    }

    [Fact]
    public async Task AnyOnePatternOfTheListIsEnoughAsync()
    {
        string[] deny = ["rm -rf", "git push", "curl"];

        Assert.Equal(Selected(0, "reject"), await AnswerAsync(Request(0, "curl http://x", standard), deny));
        Assert.Equal(Selected(0, "once"), await AnswerAsync(Request(0, "wget http://x", standard), deny));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task APatternThatSaysNothingNamesNothingAsync(string pattern)
    {
        Assert.Equal(Selected(0, "once"), await AnswerAsync(Request(0, "git push", standard), [pattern]));
    }

    // "A trailing * is a plain prefix": the empty prefix starts every command, so a bare wildcard denies all of them (and so does " *", trimmed to it).
    [Theory]
    [InlineData("*")]
    [InlineData(" *")]
    public async Task ABareWildcardNamesEveryCommandAsync(string pattern)
    {
        Assert.Equal(Selected(0, "reject"), await AnswerAsync(Request(0, "ls", standard), [pattern]));
    }

    // ---- a request that is not the shape opencode sends --------------------------------------------------------------------------

    // The pump thread must never throw on what an agent sent, and the agent waits for an answer: a malformed request is answered cancelled, not left hanging.
    [Theory]
    [InlineData(/*lang=json,strict*/ """{"jsonrpc":"2.0","id":4,"method":"session/request_permission"}""")]
    [InlineData(/*lang=json,strict*/ """{"jsonrpc":"2.0","id":4,"method":"session/request_permission","params":null}""")]
    [InlineData(/*lang=json,strict*/ """{"jsonrpc":"2.0","id":4,"method":"session/request_permission","params":{}}""")]
    [InlineData(/*lang=json,strict*/ """{"jsonrpc":"2.0","id":4,"method":"session/request_permission","params":"nope"}""")]
    [InlineData(/*lang=json,strict*/ """{"jsonrpc":"2.0","id":4,"method":"session/request_permission","params":{"toolCall":5,"options":"x"}}""")]
    [InlineData(/*lang=json,strict*/ """{"jsonrpc":"2.0","id":4,"method":"session/request_permission","params":{"toolCall":{"rawInput":"git push"},"options":[{"kind":"allow_once"},5,{"optionId":7}]}}""")]
    public async Task AMalformedPermissionRequestIsStillAnsweredCancelledAsync(string request)
    {
        string answer = await AnswerAsync(request, ["git push"]);

        Assert.Equal(Cancelled(4), answer);
    }

    [Fact]
    public async Task AnOptionWithoutAnIdIsSkippedAndTheNextOneIsUsedAsync()
    {
        string request = Frame(new
        {
            jsonrpc = "2.0",
            id = 4,
            method = "session/request_permission",
            @params = new { toolCall = new { rawInput = new { command = "ls" } }, options = new object[] { new { kind = "allow_once" }, new { optionId = "ok", kind = "allow_once" } } },
        });

        Assert.Equal(Selected(4, "ok"), await AnswerAsync(request));
    }
}
