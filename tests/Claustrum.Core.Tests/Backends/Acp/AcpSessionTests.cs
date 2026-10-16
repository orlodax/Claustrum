using System.Text.Json;
using Claustrum.Core.Process;
using static Claustrum.Core.Tests.Backends.Acp.AcpLines;

namespace Claustrum.Core.Tests.Backends.Acp;

// The driver of every ACP run, played against an in-memory stdin: the frames the client sends, their ids and their order, what ends the run, and the record it keeps.
// AcpSession is internal; it is reached as ProcessRunner reaches it, through IDuplexBackend.CreateDriver and IProcessLive (the sequence is NOTES.md "The ACP backend as built").
public sealed class AcpSessionTests
{
    // ---- the handshake and the first prompt -------------------------------------------------------------------------

    [Fact]
    public async Task ABatchRunSendsInitializeNewSessionTheModelTheModeThenThePromptWithTheClientsOwnIdsAsync()
    {
        await using DriverRig rig = new(Run(brief: "do the thing"));

        SentFrame[] sent = await rig.OpenAsync();

        Assert.Equal(["initialize", "session/new", "session/set_config_option", "session/set_config_option", "session/prompt"], sent.Select(frame => frame.Method));
        Assert.Equal(["init-1", "new-2", "config-3", "config-4", "prompt-5"], sent.Select(frame => frame.Id));
        Assert.All(sent, frame => Assert.Equal("2.0", JsonDocument.Parse(frame.Raw).RootElement.GetProperty("jsonrpc").GetString()));
    }

    [Fact]
    public async Task InitializeAdvertisesNeitherFilesNorTerminalAndNamesTheClientAsync()
    {
        await using DriverRig rig = new();

        SentFrame init = await rig.NextAsync();

        JsonElement parameters = init.Params;
        Assert.Equal(1, parameters.GetProperty("protocolVersion").GetInt32());
        JsonElement capabilities = parameters.GetProperty("clientCapabilities");
        Assert.False(capabilities.GetProperty("fs").GetProperty("readTextFile").GetBoolean());
        Assert.False(capabilities.GetProperty("fs").GetProperty("writeTextFile").GetBoolean());
        Assert.False(capabilities.GetProperty("terminal").GetBoolean());
        Assert.Equal("claustrum", parameters.GetProperty("clientInfo").GetProperty("name").GetString());
        Assert.False(string.IsNullOrWhiteSpace(parameters.GetProperty("clientInfo").GetProperty("version").GetString()));
    }

    [Fact]
    public async Task ANewSessionIsOpenedInTheRunsDirectoryWithNoMcpServersAndEveryLaterFrameNamesTheSessionTheAgentGaveAsync()
    {
        await using DriverRig rig = new(Run(model: "opencode/fake", brief: "do the thing"));

        SentFrame[] sent = await rig.OpenAsync();

        Assert.Equal("/repo", sent[1].ParamString("cwd"));
        Assert.Equal(JsonValueKind.Array, sent[1].Params.GetProperty("mcpServers").ValueKind);
        Assert.Equal(0, sent[1].Params.GetProperty("mcpServers").GetArrayLength());
        Assert.All(sent[2..], frame => Assert.Equal(Session, frame.ParamString("sessionId")));
    }

    [Fact]
    public async Task TheModelThenTheRolesAgentAreTheConfigOptionsSetBeforeThePromptAsync()
    {
        await using DriverRig rig = new(Run(model: "opencode/fake", role: "tester"));

        SentFrame[] sent = await rig.OpenAsync();

        Assert.Equal(("model", "opencode/fake"), (sent[2].ParamString("configId"), sent[2].ParamString("value")));
        Assert.Equal(("mode", "claustrum-tester"), (sent[3].ParamString("configId"), sent[3].ParamString("value")));
    }

    [Fact]
    public async Task AVariantInTheModelIdIsSetAsTheEffortOptionBetweenTheModelAndTheModeAsync()
    {
        await using DriverRig rig = new(Run(model: "opencode/fake#high"));

        SentFrame[] sent = await rig.OpenAsync(configOptions: 3);

        Assert.Equal(["config-3", "config-4", "config-5", "prompt-6"], sent[2..].Select(frame => frame.Id));
        Assert.Equal([("model", "opencode/fake"), ("effort", "high"), ("mode", "claustrum-builder")], sent[2..5].Select(frame => (frame.ParamString("configId"), frame.ParamString("value"))));
    }

    [Fact]
    public async Task ThePromptIsTheBriefAsOneTextBlockExactlyWhateverCharactersItHoldsAsync()
    {
        const string Brief = "line one\nline \"two\" \\ backslash\té 😀 <tag> `fence` {\"a\":1}\r\nend";
        await using DriverRig rig = new(Run(brief: Brief));

        SentFrame[] sent = await rig.OpenAsync();

        JsonElement block = Assert.Single(sent[^1].Params.GetProperty("prompt").EnumerateArray());
        Assert.Equal("text", block.GetProperty("type").GetString());
        Assert.Equal(Brief, block.GetProperty("text").GetString());
        Assert.DoesNotContain('\n', sent[^1].Raw);
        Assert.DoesNotContain('\r', sent[^1].Raw);
    }

    [Theory]
    [InlineData("C:\\Users\\me\\my project")]
    [InlineData("/home/me/\"quoted\" dir/\u00e9\ud83d\ude00")]
    public async Task TheRunsDirectoryReachesTheAgentExactlyWhateverCharactersItHoldsAsync(string cwd)
    {
        await using DriverRig rig = new(Run(cwd: cwd, resume: null));

        SentFrame[] sent = await rig.OpenAsync();

        Assert.Equal(cwd, sent[1].ParamString("cwd"));
    }

    [Fact]
    public async Task TheDirectoryOfALoadedSessionIsTheRunsTooAsync()
    {
        await using DriverRig rig = new(Run(cwd: "/work/tree", resume: "ses_old"));

        SentFrame[] sent = await rig.OpenAsync(load: true);

        Assert.Equal("/work/tree", sent[1].ParamString("cwd"));
    }

    // ---- the wire format, against what the real client wrote -------------------------------------------------------------------

    private static string WithoutVersion(string frame) => System.Text.RegularExpressions.Regex.Replace(frame, "\"version\":\"[^\"]*\"", "\"version\":\"x\"");

    // The recording is job 20261016-164432-4bf5f173: the client's own acp-client.jsonl and the agent's own stdout.log. Answered with the recorded agent lines (so the
    // session id is the real one), the driver has to write the recorded client lines byte for byte: key order, escaping (`\u0060` for a backtick) and ids included.
    [Fact]
    public async Task ARealRunsClientFramesAreWhatTheDriverWritesWhenAnsweredWithTheRealAgentsLinesAsync()
    {
        string[] client = FixtureLines("opencode-success.client.jsonl");
        string[] agent = FixtureLines("opencode-success.jsonl");
        using JsonDocument recordedOpen = JsonDocument.Parse(client[1]);
        using JsonDocument recordedPrompt = JsonDocument.Parse(client[4]);
        string cwd = recordedOpen.RootElement.GetProperty("params").GetProperty("cwd").GetString() ?? "";
        string brief = recordedPrompt.RootElement.GetProperty("params").GetProperty("prompt")[0].GetProperty("text").GetString() ?? "";
        await using DriverRig rig = new(Run(model: "opencode/deepseek-v4.1-flash-free", cwd: cwd, brief: brief));
        List<string> written = [];

        // The agent's lines 1, 2, 4 and 5 answer init-1, new-2, config-3 and config-4 (line 3 is an update the driver has no need of).
        foreach (int line in new[] { 0, 1, 3, 4 })
        {
            written.Add((await rig.NextAsync()).Raw);
            rig.Agent(agent[line]);
        }

        written.Add((await rig.NextAsync()).Raw);

        Assert.Equal(client.Select(WithoutVersion), written.Select(WithoutVersion));
    }

    // A refused effort: the same four frames the real client wrote before the agent's -32602 stopped it, and nothing after.
    [Fact]
    public async Task ARealRefusedEffortIsTheFramesTheDriverWritesAndThenItStopsAsync()
    {
        string[] client = FixtureLines("opencode-effort-refused.client.jsonl");
        string[] agent = FixtureLines("opencode-effort-refused.jsonl");
        using JsonDocument recordedOpen = JsonDocument.Parse(client[1]);
        string cwd = recordedOpen.RootElement.GetProperty("params").GetProperty("cwd").GetString() ?? "";
        await using DriverRig rig = new(Run(model: "opencode/deepseek-v4.1-flash-free#bogus", cwd: cwd));
        List<string> written = [];

        foreach (int line in new[] { 0, 1, 3, 4 })
        {
            written.Add((await rig.NextAsync()).Raw);
            rig.Agent(agent[line]);
        }

        await rig.StdinClosesAsync();
        await rig.CompletesAsync();
        Assert.Equal(client.Select(WithoutVersion), written.Select(WithoutVersion));
        Assert.Equal(4, rig.Stdin.Everything.Count);
    }

    // ---- resuming ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AResumedRunLoadsTheSessionInsteadOfOpeningANewOneAndPromptsItAsync()
    {
        await using DriverRig rig = new(Run(resume: "ses_old"));

        SentFrame[] sent = await rig.OpenAsync(load: true);

        Assert.Equal(["initialize", "session/load", "session/set_config_option", "session/set_config_option", "session/prompt"], sent.Select(frame => frame.Method));
        Assert.Equal(["init-1", "load-2", "config-3", "config-4", "prompt-5"], sent.Select(frame => frame.Id));
        Assert.Equal("ses_old", sent[1].ParamString("sessionId"));
        Assert.Equal("/repo", sent[1].ParamString("cwd"));
        Assert.Equal(0, sent[1].Params.GetProperty("mcpServers").GetArrayLength());

        // `session/load` answers with no session id: the resumed one is the session every later frame names, and the model is set again after the load.
        Assert.All(sent[2..], frame => Assert.Equal("ses_old", frame.ParamString("sessionId")));
        Assert.Equal("model", sent[2].ParamString("configId"));
    }

    [Fact]
    public async Task AHarnessThatCannotLoadASessionOpensANewOneEvenForAResumeRequestAsync()
    {
        await using DriverRig rig = new(Run(resume: "ses_old"), new FakeAcpHarness(totals: []) { LoadsSessions = false });

        SentFrame[] sent = await rig.OpenAsync();

        Assert.Equal("session/new", sent[1].Method);
        Assert.DoesNotContain(sent, frame => frame.Method == "session/load");
    }

    [Fact]
    public async Task AnEmptyResumeIdIsNoResumeAtAllAsync()
    {
        await using DriverRig rig = new(Run(resume: ""));

        SentFrame[] sent = await rig.OpenAsync();

        Assert.Equal("session/new", sent[1].Method);
    }

    // The harness's own setup decides the options and their order: a second row would set different ones.
    [Fact]
    public async Task AnotherHarnessRowsConfigOptionsAreTheOnesSetAsync()
    {
        await using DriverRig rig = new(Run(model: "m-1", role: "builder"), new FakeAcpHarness());

        SentFrame[] sent = await rig.OpenAsync();

        Assert.Equal([("model", "m-1"), ("mode", "builder")], sent[2..4].Select(frame => (frame.ParamString("configId"), frame.ParamString("value"))));
    }

    // ---- what ends a run -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task OnceThePromptIsAnsweredStdinClosesAndTheRunIsDeliveredAsync()
    {
        await using DriverRig rig = new();
        SentFrame[] sent = await rig.OpenAsync();
        Assert.False(rig.Driver.ReportDelivered);

        rig.Agent(Answered(sent[^1].Id));

        await rig.StdinClosesAsync();
        await rig.CompletesAsync();
        Assert.True(rig.Driver.ReportDelivered);
        Assert.Equal(5, rig.Stdin.Everything.Count);
    }

    // The agent saying more after its answer (a late update, a second answer) writes nothing and changes nothing.
    [Fact]
    public async Task AnythingTheAgentSaysAfterTheAnswerIsReadAndNeverAnsweredAsync()
    {
        await using DriverRig rig = new();
        SentFrame[] sent = await rig.OpenAsync();
        rig.Agent(Answered(sent[^1].Id));
        await rig.CompletesAsync();

        rig.Agent(Message("late"));
        rig.Agent(Answered(sent[^1].Id));

        Assert.Equal(5, rig.Stdin.Everything.Count);
    }

    [Fact]
    public async Task APromptAnsweredWithAnErrorEndsTheBatchRunAllTheSameAsync()
    {
        await using DriverRig rig = new();
        SentFrame[] sent = await rig.OpenAsync();

        rig.Agent(Failure(sent[^1].Id, -32603, "the provider is down"));

        await rig.StdinClosesAsync();
        await rig.CompletesAsync();
        Assert.True(rig.Driver.ReportDelivered);
    }

    public static TheoryData<string, int> HandshakeSteps() => new()
    {
        { "initialize", 1 },
        { "session/new", 2 },
        { "model", 3 },
        { "mode", 4 },
    };

    // Any JSON-RPC error answering a step of the handshake ends the run right there: stdin closes, nothing more is sent, and Parse reads the refusal.
    [Theory]
    [MemberData(nameof(HandshakeSteps))]
    public async Task AnyErrorInTheHandshakeClosesStdinAtOnceAndSendsNoPromptAsync(string step, int framesSent)
    {
        await using DriverRig rig = new();
        List<SentFrame> sent = [];
        for (int i = 0; i < framesSent; i++)
        {
            SentFrame frame = await rig.NextAsync();
            sent.Add(frame);
            rig.Agent(i == framesSent - 1 ? Failure(frame.Id, -32602, $"{step} refused") : OkFor(frame));
        }

        await rig.StdinClosesAsync();
        await rig.CompletesAsync();
        Assert.Equal(framesSent, rig.Stdin.Everything.Count);
        Assert.DoesNotContain(rig.Stdin.Everything, line => line.Contains("session/prompt", StringComparison.Ordinal));
        Assert.False(rig.Driver.ReportDelivered);
    }

    [Fact]
    public async Task ARefusedLoadEndsTheRunBeforeAnyConfigOrPromptAsync()
    {
        await using DriverRig rig = new(Run(resume: "ses_gone"));
        rig.Agent(Initialized((await rig.NextAsync()).Id));
        SentFrame load = await rig.NextAsync();

        rig.Agent(Failure(load.Id, -32602, "session not found"));

        await rig.StdinClosesAsync();
        await rig.CompletesAsync();
        Assert.Equal(["initialize", "session/load"], rig.Stdin.Everything.Select(line => new SentFrame(line).Method));
    }

    [Fact]
    public async Task ARefusedEffortEndsTheRunAfterTheModelWasSetAndBeforeTheModeAsync()
    {
        await using DriverRig rig = new(Run(model: "opencode/fake#bogus"));
        rig.Agent(Initialized((await rig.NextAsync()).Id));
        rig.Agent(Opened((await rig.NextAsync()).Id));
        rig.Agent(Configured((await rig.NextAsync()).Id));
        SentFrame effort = await rig.NextAsync();

        rig.Agent(Failure(effort.Id, -32602, "Invalid params: effort not found: bogus", new { effort = "bogus" }));

        await rig.StdinClosesAsync();
        await rig.CompletesAsync();
        Assert.Equal(("effort", "bogus"), (effort.ParamString("configId"), effort.ParamString("value")));
        Assert.Equal(4, rig.Stdin.Everything.Count);
    }

    [Fact]
    public async Task ASessionAnsweredWithNoSessionIdEndsTheRunAsync()
    {
        await using DriverRig rig = new();
        rig.Agent(Initialized((await rig.NextAsync()).Id));
        SentFrame open = await rig.NextAsync();

        rig.Agent(Result(open.Id, new { configOptions = Array.Empty<object>() }));

        await rig.StdinClosesAsync();
        await rig.CompletesAsync();
        Assert.Equal(2, rig.Stdin.Everything.Count);
    }

    // ProcessRunner cancels the driver's token once the process is gone (a kill, a timeout, an agent that exited without answering): that is the normal end, not a fault.
    [Fact]
    public async Task AProcessThatEndsMidHandshakeEndsTheRunQuietlyWithStdinClosedAsync()
    {
        await using DriverRig rig = new();
        _ = await rig.NextAsync();

        rig.EndProcess();

        await rig.CompletesAsync();
        await rig.StdinClosesAsync();
        Assert.False(rig.Driver.ReportDelivered);
    }

    [Fact]
    public async Task AProcessThatEndsWhileThePromptIsInFlightIsNotADeliveredRunAsync()
    {
        await using DriverRig rig = new();
        _ = await rig.OpenAsync();

        rig.EndProcess();

        await rig.CompletesAsync();
        Assert.False(rig.Driver.ReportDelivered);
    }

    [Fact]
    public async Task ARunGivenNoStdinReturnsAtOnceAsync()
    {
        string scratch = Directory.CreateTempSubdirectory("claustrum-acp-nostdin-").FullName;
        try
        {
            IProcessLive driver = Backend().CreateDriver(Run(), JobIn(scratch), live: null);

            await driver.RunAsync(stdin: null, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            Assert.False(driver.ReportDelivered);
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    // ---- the agent's own requests ------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("fs/read_text_file")]
    [InlineData("fs/write_text_file")]
    [InlineData("terminal/create")]
    [InlineData("terminal/output")]
    [InlineData("elicitation/create")]
    [InlineData("some/future/method")]
    public async Task ARequestForAnythingButPermissionIsMethodNotFoundWithTheSameIdAsync(string method)
    {
        await using DriverRig rig = new();
        _ = await rig.OpenAsync();

        rig.Agent(Frame(new { jsonrpc = "2.0", id = "agent-7", method, @params = new { sessionId = Session } }));
        SentFrame reply = await rig.NextAsync();

        Assert.Equal("{\"jsonrpc\":\"2.0\",\"id\":\"agent-7\",\"error\":{\"code\":-32601,\"message\":\"client does not implement " + method + "\"}}", reply.Raw);
    }

    [Fact]
    public async Task NotificationsResponsesToNobodyAndJunkAreNeverAnsweredAsync()
    {
        await using DriverRig rig = new();
        _ = await rig.OpenAsync();

        rig.Agent(Message("a streamed chunk"));
        rig.Agent(Cost(0.1m));
        rig.Agent(Frame(new { jsonrpc = "2.0", method = "some/notification", @params = new { } }));
        rig.Agent(Result("prompt-99", new { stopReason = "end_turn" }));
        rig.Agent("not json at all");
        rig.Agent("");
        rig.Agent("{\"jsonrpc\":\"2.0\",\"id\":\"half");
        rig.Agent(Frame(new { jsonrpc = "2.0", id = 3, method = "fs/read_text_file", @params = new { } }));
        SentFrame reply = await rig.NextAsync();

        // The only thing the client wrote after the prompt is the answer to the one request among them.
        Assert.Equal("fs/read_text_file", JsonDocument.Parse(reply.Raw).RootElement.GetProperty("error").GetProperty("message").GetString()?.Replace("client does not implement ", ""));
        Assert.Equal(6, rig.Stdin.Everything.Count);
    }

    // ---- the record -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task EveryFrameTheClientSentIsAppendedToTheClientLogInOrderAsync()
    {
        await using DriverRig rig = new();
        SentFrame[] sent = await rig.OpenAsync();
        rig.Agent(Frame(new { jsonrpc = "2.0", id = 0, method = "fs/read_text_file", @params = new { } }));
        SentFrame refused = await rig.NextAsync();
        rig.Agent(Answered(sent[^1].Id));
        await rig.CompletesAsync();

        string[] log = await File.ReadAllLinesAsync(rig.Job.AcpClientLog, TestContext.Current.CancellationToken);

        Assert.Equal([.. sent.Select(frame => frame.Raw), refused.Raw], log);
        Assert.Equal(log, rig.Stdin.Everything);
    }

    [Fact]
    public async Task AClientLogThatCannotBeWrittenCostsTheRecordNeverTheRunAsync()
    {
        await using DriverRig rig = new(blockClientLog: true);
        SentFrame[] sent = await rig.OpenAsync();

        rig.Agent(Answered(sent[^1].Id));

        await rig.CompletesAsync();
        Assert.True(rig.Driver.ReportDelivered);
        Assert.True(Directory.Exists(rig.Job.AcpClientLog));
    }

    private static string OkFor(SentFrame frame) => frame.Method switch
    {
        "initialize" => Initialized(frame.Id),
        "session/new" => Opened(frame.Id),
        _ => Configured(frame.Id),
    };
}
