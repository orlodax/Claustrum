using Claustrum.Cli;

namespace Claustrum.Tests.Cli;

// T1: a cancel whose message says more than the framework's default — JobWorktree's undo naming what a
// cancelled `git worktree add` could not remove — is printed as a warning, still exits 130; a plain Ctrl-C
// stays silent. Console.Error is process-wide, so the class takes the collection nothing else shares.
[Collection("console redirect")]
public sealed class RunCommandCancelTests : IDisposable
{
    private readonly TextWriter original = Console.Error;
    private readonly StringWriter captured = new();

    public RunCommandCancelTests() => Console.SetError(captured);

    public void Dispose()
    {
        Console.SetError(original);
        captured.Dispose();
    }

    [Fact]
    public void ACancelThatNamesWhatItLeftBehindIsPrintedAsAWarningAndStillExits130()
    {
        OperationCanceledException inner = new();
        OperationCanceledException cancel = new("git worktree add was cancelled; left behind: /repo/.claustrum/worktrees/j1 (fatal)", inner);

        int exitCode = RunCommand.Cancelled(cancel);

        Assert.Equal(ExitCodes.Cancelled, exitCode);
        Assert.Contains("warning: git worktree add was cancelled; left behind: /repo/.claustrum/worktrees/j1 (fatal)", captured.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ABareOperationCanceledExceptionPrintsNothing()
    {
        int exitCode = RunCommand.Cancelled(new OperationCanceledException());

        Assert.Equal(ExitCodes.Cancelled, exitCode);
        Assert.Equal("", captured.ToString());
    }

    [Fact]
    public void ABareTaskCanceledExceptionPrintsNothing()
    {
        int exitCode = RunCommand.Cancelled(new TaskCanceledException());

        Assert.Equal(ExitCodes.Cancelled, exitCode);
        Assert.Equal("", captured.ToString());
    }

    [Fact]
    public void ACancelRaisedFromATokenPrintsNothing()
    {
        using CancellationTokenSource source = new();
        source.Cancel();

        int exitCode = RunCommand.Cancelled(new OperationCanceledException(source.Token));

        Assert.Equal(ExitCodes.Cancelled, exitCode);
        Assert.Equal("", captured.ToString());
    }
}
