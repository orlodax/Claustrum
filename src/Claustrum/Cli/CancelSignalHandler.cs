using System.Runtime.InteropServices;

namespace Claustrum.Cli;

/// <summary>
/// `run`'s, `coordinate`'s and `jobs clean`'s Ctrl-C, SIGTERM and SIGHUP: the first signal of any kind cancels
/// <see cref="Token"/>, so Runner kills the backend's tree and writes a `cancelled` receipt. A later Ctrl-C,
/// SIGQUIT or SIGTERM lets the runtime end the process; a later SIGHUP never does. <c>forcedExit</c> finishes
/// the stderr line "press Ctrl-C again to …".
/// </summary>
// With ProcessTerminationTimeout null (Program.cs, #88) nothing else ends a cancel path that hangs — the
// post-run commit runs the repo's hooks, bounded only by 5 minutes (2026-10-09, NOTES.md "Ctrl-C reaches the runner").
// On Unix CancelKeyPress also fires for SIGQUIT, so it takes the Ctrl-C path. NOTES.md "A cancel during `worktree add`
// and a closed terminal" has why SIGHUP never forces (a closing terminal sends two) and the Windows console-close wait.
internal sealed class CancelSignalHandler : IDisposable
{
    // Windows ends the process once the CTRL_CLOSE_EVENT handler returns, and gives a closing console about 5 s.
    private static readonly TimeSpan closeWait = TimeSpan.FromSeconds(4);

    private readonly CancellationTokenSource cts = new();
    private readonly ManualResetEventSlim settled = new(initialState: false);
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

    /// <summary>The verb's receipt is written (or it has finished): releases a Windows console close waiting on it.</summary>
    public void Settled() => settled.Set();

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
        if (context.Signal == PosixSignal.SIGHUP)
        {
            OnHangup(context);
            return;
        }

        if (!FirstSignal())
            return;

        context.Cancel = true;
        cts.Cancel();
        Hint($"send {context.Signal} again");
    }

    // Never the signal that forces: closing a terminal delivers two within 0.3 ms (the shell's hangup handler, then the
    // kernel's to the foreground group), and the second killed the run before its receipt in 3 of 4 trials (2026-10-14).
    private void OnHangup(PosixSignalContext context)
    {
        context.Cancel = true;
        if (Interlocked.CompareExchange(ref signals, 1, 0) != 0)
            return;

        cts.Cancel();
        Hint("send SIGTERM or press Ctrl-C again");

        // The console is closing and the OS ends the process when this returns: hold it for the receipt.
        if (OperatingSystem.IsWindows())
            settled.Wait(closeWait);
    }

    // A SIGHUP means the terminal is gone: writing to it fails (EIO), and an exception here would end the process
    // before the cancel it just started could write its receipt.
    private void Hint(string again) => SafeConsole.Error($"cancelling… {again} to {forcedExit}");

    // Cancel left false on any later signal: the runtime terminates the process here and now, with no receipt.
    private bool FirstSignal() => Interlocked.Increment(ref signals) == 1;

    public void Dispose()
    {
        Console.CancelKeyPress -= OnCancelKeyPress;
        sigterm.Dispose();
        sighup.Dispose();
        settled.Set();

        // cts stays undisposed: the runtime hands a handler to a new thread after it read the signal, so
        // one can still call Cancel here, and on a disposed source that is an unhandled ObjectDisposedException.
        // A source with no timer holds nothing the GC will not take.
    }
}
