using System.Text;
using Claustrum.Cli;

namespace Claustrum.Tests.Cli;

// A closed terminal makes every later console write throw EIO (an IOException); on the --stream echo's reader thread that was an
// unhandled exception (SIGABRT, no receipt) and in the final output it turned a cancel's exit 130 into 1. SafeConsole swallows
// that and only that. The end-to-end proof is the pty cases (CancelPtyEndToEndTests); these are the writer-level facts.
public sealed class SafeConsoleTests
{
    private sealed class ThrowingWriter(Exception failure) : TextWriter
    {
        public int Attempts { get; private set; }

        public override Encoding Encoding => Encoding.UTF8;

        public override void WriteLine(string? value)
        {
            Attempts++;
            throw failure;
        }
    }

    [Fact]
    public void AnIoErrorFromTheWriterIsSwallowed()
    {
        ThrowingWriter writer = new(new IOException("Input/output error"));

        SafeConsole.WriteLine(writer, "line");

        Assert.Equal(1, writer.Attempts);
    }

    [Fact]
    public void AnIoErrorOnAnotherThreadDoesNotEscapeItLikeTheStreamEchoesReader()
    {
        ThrowingWriter writer = new(new IOException("Input/output error"));
        Exception? escaped = null;
        Thread reader = new(() =>
        {
            try
            {
                for (int i = 0; i < 100; i++)
                    SafeConsole.WriteLine(writer, "stream line");
            }
            catch (Exception ex)
            {
                escaped = ex;
            }
        });

        reader.Start();
        reader.Join();

        Assert.Null(escaped);
        Assert.Equal(100, writer.Attempts);
    }

    [Theory]
    [MemberData(nameof(NotIoErrors))]
    public void AnythingThatIsNotAnIoErrorStillSurfaces(Exception failure)
    {
        ThrowingWriter writer = new(failure);

        Exception thrown = Assert.ThrowsAny<Exception>(() => SafeConsole.WriteLine(writer, "line"));

        Assert.Same(failure, thrown);
    }

    public static TheoryData<Exception> NotIoErrors() =>
    [
        new InvalidOperationException("a bug"),
        new ObjectDisposedException("writer"),
        new UnauthorizedAccessException("denied"),
    ];

    [Fact]
    public void AWorkingWriterGetsTheLine()
    {
        StringWriter writer = new();

        SafeConsole.WriteLine(writer, "hello");

        Assert.Equal("hello" + writer.NewLine, writer.ToString());
    }
}
