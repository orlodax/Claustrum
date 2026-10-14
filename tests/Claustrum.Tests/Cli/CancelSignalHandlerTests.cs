using Claustrum.Cli;

namespace Claustrum.Tests.Cli;

// #111: the handler registers SIGHUP next to SIGTERM and Ctrl-C. On Windows SIGHUP maps to the console-close event, which the
// runtime accepts for registration even though no test can send it, so this proves construction and disposal on every OS
// (macOS included); the signals themselves are the end-to-end files' (CancelSighupEndToEndTests and its siblings).
public sealed class CancelSignalHandlerTests
{
    [Fact]
    public void ConstructedWithTheDefaultOrACallerHintItStartsUncancelledAndDisposesCleanly()
    {
        using CancelSignalHandler byDefault = new();
        using CancelSignalHandler withHint = new("stop now");

        Assert.False(byDefault.Token.IsCancellationRequested);
        Assert.False(withHint.Token.IsCancellationRequested);
    }

    [Fact]
    public void ADisposedHandlerCanBeReplacedByAnotherAndNothingIsCancelled()
    {
        CancelSignalHandler first = new();
        CancellationToken token = first.Token;
        first.Dispose();
        using CancelSignalHandler second = new();

        Assert.False(token.IsCancellationRequested);
        Assert.False(second.Token.IsCancellationRequested);
    }
}
