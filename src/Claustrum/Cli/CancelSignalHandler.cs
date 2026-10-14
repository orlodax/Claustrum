using System.Runtime.InteropServices;

namespace Claustrum.Cli;

/// <summary>
/// `run`'s, `coordinate`'s and `jobs clean`'s Ctrl-C, SIGTERM and SIGHUP: the first signal of any kind cancels
/// <see cref="Token"/>, so Runner kills the backend's tree and writes a `cancelled` receipt; a second
/// one lets the runtime end the process. <c>forcedExit</c> finishes the stderr line "press Ctrl-C again to …".
/// </summary>
// With ProcessTerminationTimeout null (Program.cs, #88) nothing else ends a cancel path that hangs — the
// post-run commit runs the repo's hooks, bounded only by 5 minutes (2026-10-09, NOTES.md "Ctrl-C reaches the runner").
// On Unix CancelKeyPress also fires for SIGQUIT, so it takes the Ctrl-C path. SIGHUP (#111, 2026-10-14) is
// registered on Windows too, where it is the console-close event: the OS ends the process moments after the handler.
internal sealed class CancelSignalHandler : IDisposable
{
    private readonly CancellationTokenSource cts = new();
    private readonly PosixSignalRegistration sigterm;
    private readonly PosixSignalRegistration sighup;
    private readonly string forcedExit;
    private int signals;

    public CancelSignalHandler(string forcedExit = "exit without a receipt")
    {
        this.forcedExit = forcedExit;
        Console.CancelKeyPress += OnCancelKeyPress;
        // Registering SIGTERM is supported on Windows too, but there the runtime cannot cancel it: the
        // process ends once the handler returns (learn.microsoft.com, PosixSignalRegistration.Create).
        sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, OnPosixSignal);
        sighup = PosixSignalRegistration.Create(PosixSignal.SIGHUP, OnPosixSignal);
    }

    public CancellationToken Token => cts.Token;

    private void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
    {
        if (!FirstSignal())
            return;

        e.Cancel = true;
        cts.Cancel();
        Hint("press Ctrl-C again");
    }

    private void OnPosixSignal(PosixSignalContext context)
    {
        if (!FirstSignal())
            return;

        context.Cancel = true;
        cts.Cancel();
        Hint($"send {context.Signal} again");
    }

    // A SIGHUP means the terminal is gone: writing to it fails (EIO), and an exception here would end the process
    // before the cancel it just started could write its receipt.
    private void Hint(string again)
    {
        try
        {
            Console.Error.WriteLine($"cancelling… {again} to {forcedExit}");
        }
        catch (IOException)
        {
            // Nobody is reading.
        }
    }

    // Cancel left false on any later signal: the runtime terminates the process here and now, with no receipt.
    private bool FirstSignal() => Interlocked.Increment(ref signals) == 1;

    public void Dispose()
    {
        Console.CancelKeyPress -= OnCancelKeyPress;
        sigterm.Dispose();
        sighup.Dispose();

        // cts stays undisposed: the runtime hands a handler to a new thread after it read the signal, so
        // one can still call Cancel here, and on a disposed source that is an unhandled ObjectDisposedException.
        // A source with no timer holds nothing the GC will not take.
    }
}
