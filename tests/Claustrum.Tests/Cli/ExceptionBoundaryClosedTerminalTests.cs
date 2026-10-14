using System.Text;
using Claustrum.Casts;
using Claustrum.Cli;
using Claustrum.Core.Config;
using Claustrum.Core.Process;

namespace Claustrum.Tests.Cli;

// The error boundary reports through SafeConsole (2026-10-14, found by review): after the terminal is gone every write throws EIO
// (an IOException), and one escaping the boundary's own catch would replace the exit code the failure maps to with a crash.
// Console.Error is redirected process-wide, hence the shared collection (see ExceptionBoundaryTests). The same through the real
// binary with a closed pty is CancelPtyEndToEndTests.
[Collection("console redirect")]
public sealed class ExceptionBoundaryClosedTerminalTests : IDisposable
{
    private sealed class DeadTerminal : TextWriter
    {
        public int Attempts { get; private set; }

        public override Encoding Encoding => Encoding.UTF8;

        public override void WriteLine(string? value)
        {
            Attempts++;
            throw new IOException("Input/output error");
        }
    }

    private readonly TextWriter originalError = Console.Error;
    private readonly DeadTerminal dead = new();

    public ExceptionBoundaryClosedTerminalTests() => Console.SetError(dead);

    public void Dispose() => Console.SetError(originalError);

    public static TheoryData<Exception, int> Failures() => new()
    {
        { new ConfigException("bad config"), ExitCodes.Usage },
        { new CastException("bad cast"), ExitCodes.Usage },
        { new ArgumentException("bad argument"), ExitCodes.Usage },
        { new BackendNotFoundException("claude"), ExitCodes.BackendMissing },
        { new InvalidOperationException("something else"), ExitCodes.BackendFailure },
    };

    [Theory]
    [MemberData(nameof(Failures))]
    public void AFailureStillMapsToItsExitCodeWhenTheTerminalIsGone(Exception exception, int expectedExit)
    {
        int exit = ExceptionBoundary.Handle(exception);

        Assert.Equal(expectedExit, exit);
        Assert.True(dead.Attempts > 0, "the boundary did try to report");
    }

    [Fact]
    public void TheDebugTraceOfAnUnexpectedFailureIsBestEffortToo()
    {
        string? before = Environment.GetEnvironmentVariable("CLAUSTRUM_DEBUG");
        Environment.SetEnvironmentVariable("CLAUSTRUM_DEBUG", "1");
        try
        {
            Assert.Equal(ExitCodes.BackendFailure, ExceptionBoundary.Handle(new InvalidOperationException("boom")));
            Assert.Equal(2, dead.Attempts);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CLAUSTRUM_DEBUG", before);
        }
    }
}
