using Claustrum.Core.Backends;
using Claustrum.Core.Backends.Acp;
using Claustrum.Core.Live;

namespace Claustrum.Core.Tests.Backends.Acp;

// AcpSession hands each answered turn to LiveSession as text (stdout), error (stderr) and 0/1 (exit code), and carries the next message back in
// a ProcessSpec that is never launched. These two small pieces are the whole of that seam.
public sealed class AcpLiveProtocolTests
{
    private readonly AcpLiveProtocol protocol = AcpLiveProtocol.Instance;

    [Fact]
    public void AnAnsweredTurnIsItsTextAndNamesThePlaceholderSession()
    {
        LiveTurn turn = protocol.ReadTurn("which database?", "", 0);

        Assert.Equal(new LiveTurn("which database?", IsError: false, "acp"), turn);
        Assert.Equal(AcpLiveProtocol.SessionPlaceholder, turn.SessionId);
    }

    // LiveSession only needs a non-null session id to ask for a next turn; the carrier ignores it.
    [Fact]
    public void EveryTurnEvenAFailedOneNamesASessionSoAnotherTurnCanBeAsked()
    {
        Assert.NotNull(protocol.ReadTurn("text", "", 0).SessionId);
        Assert.NotNull(protocol.ReadTurn("", "boom", 1).SessionId);
        Assert.NotNull(protocol.ReadTurn("", "", 0).SessionId);
    }

    [Fact]
    public void ATurnTheAgentFailedIsAnErrorWithItsStderrAsTheText()
    {
        Assert.Equal(new LiveTurn("the provider is down", IsError: true, "acp"), protocol.ReadTurn("partial text", "the provider is down", 1));
    }

    [Fact]
    public void AFailedTurnWithNoStderrStillHasAReasonInTheAgentsOwnWords()
    {
        Assert.Equal(new LiveTurn("the agent ended the turn with an error", IsError: true, "acp"), protocol.ReadTurn("", "", 1));
    }

    // A turn that ends clean but says nothing leaves a person nothing to answer.
    [Fact]
    public void ACleanTurnWithNoTextIsAnErrorBecauseNoOneCanAnswerNothing()
    {
        Assert.Equal(new LiveTurn("the agent ended the turn without any text", IsError: true, "acp"), protocol.ReadTurn("", "", 0));
    }

    [Fact]
    public void WhitespaceAloneIsTextAndIsNotCalledEmpty()
    {
        Assert.False(protocol.ReadTurn(" ", "", 0).IsError);
    }

    [Fact]
    public void ItIsASingletonOfThePerTurnShapeAndNotTheStreamOne()
    {
        Assert.Same(AcpLiveProtocol.Instance, protocol);
        Assert.IsAssignableFrom<IResumeLiveProtocol>(protocol);
        Assert.IsNotAssignableFrom<IStreamLiveProtocol>(protocol);
    }

    // ---- the carrier ---------------------------------------------------------------------------------------------------

    [Fact]
    public void ACarriedMessageIsThePlainTextOfTheSpecsStdinAndTheSpecIsNeverALaunch()
    {
        ProcessSpec carried = AcpLiveProtocol.Carry("sqlite, please");

        Assert.Equal("sqlite, please", carried.StdinText);
        Assert.Equal("sqlite, please", AcpLiveProtocol.Message(carried));
        Assert.Empty(carried.Args);
        Assert.Empty(carried.Env);
        Assert.Empty(carried.TempFiles);
        Assert.Equal("", carried.Cwd);
    }

    [Theory]
    [InlineData("")]
    [InlineData("line one\nline two\n\nline four")]
    [InlineData("quotes \" and \\ backslashes and é and 😀")]
    public void AMessageSurvivesTheCarrierExactly(string message)
    {
        Assert.Equal(message, AcpLiveProtocol.Message(AcpLiveProtocol.Carry(message)));
    }

    [Fact]
    public void ASpecWithNoStdinCarriesTheEmptyMessage()
    {
        Assert.Equal("", AcpLiveProtocol.Message(new ProcessSpec("acp", [], "", [], [])));
    }

    // The runner builds the carrier through the backend (Runner's resumeTurn); the session id it is given is the placeholder or the real one, and is not used.
    [Fact]
    public void TheBackendsCarryTurnIsTheSameCarrierWhateverSessionItIsGiven()
    {
        AcpBackend backend = AcpLines.Backend();

        foreach (string session in new[] { "acp", "ses_real", "" })
        {
            ProcessSpec carried = backend.CarryTurn(session, "hi");

            Assert.Equal("hi", AcpLiveProtocol.Message(carried));
            Assert.Equal(AcpLiveProtocol.Carry("hi").Exe, carried.Exe);
            Assert.Empty(carried.Args);
        }
    }
}
