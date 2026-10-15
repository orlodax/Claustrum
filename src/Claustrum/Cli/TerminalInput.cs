using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Claustrum.Cli;

// The line a human types in answer to a live role (#124), read from the controlling terminal itself (`/dev/tty` on
// Unix, `CONIN$` on Windows) and never from the process stdin, which may have carried the brief (`--brief-file -`).
// A read blocks a thread of its own and cannot be interrupted, so callers never await it alone: a cancel is answered
// by whoever races it, and the thread (a background one) dies with the process.
internal sealed partial class TerminalInput : IDisposable
{
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint ShareReadWrite = 0x3;
    private const uint OpenExisting = 3;

    private readonly StreamReader reader;

    private TerminalInput(StreamReader reader) => this.reader = reader;

    /// <summary>Stdin and stderr are both terminals: the rule for a prompt on any verb (a redirected stream means nobody is there to type).</summary>
    public static bool IsInteractive => !Console.IsInputRedirected && !Console.IsErrorRedirected;

    /// <summary>The terminal, or null when the process has none to open (no controlling terminal, a console that cannot be opened).</summary>
    public static TerminalInput? TryOpen()
    {
        try
        {
            return OperatingSystem.IsWindows()
                ? new TerminalInput(new StreamReader(OpenConsoleInput(), Console.InputEncoding, detectEncodingFromByteOrderMarks: false))
                : new TerminalInput(new StreamReader(new FileStream("/dev/tty", FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize: 1), new UTF8Encoding(false), false));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>One line, without its line break; null at end of input. At most one read may be outstanding.</summary>
    public Task<string?> ReadLineAsync() => Task.Factory.StartNew(reader.ReadLine, TaskCreationOptions.LongRunning);

    public void Dispose() => reader.Dispose();

    [SupportedOSPlatform("windows")]
    private static FileStream OpenConsoleInput()
    {
        SafeFileHandle handle = CreateFile("CONIN$", GenericRead | GenericWrite, ShareReadWrite, 0, OpenExisting, 0, 0);
        if (handle.IsInvalid)
            throw new IOException("the console input buffer (CONIN$) could not be opened");

        return new FileStream(handle, FileAccess.Read, bufferSize: 1);
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);
}
