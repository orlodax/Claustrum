using System.Runtime.InteropServices;

namespace Claustrum.Cli;

/// <summary>
/// `run`'s and `coordinate`'s Ctrl-C and SIGTERM: the first signal of either kind cancels
/// <see cref="Token"/>, so Runner kills the backend's tree and writes a `cancelled` receipt; a second
/// one lets the runtime end the process.
/// </summary>
// With ProcessTerminationTimeout null (Program.cs, #88) nothing else ends a cancel path that hangs — the
// post-run commit runs the repo's hooks, bounded only by 5 minutes (2026-10-09, NOTES.md "Ctrl-C reaches the runner").
// SIGQUIT and SIGHUP are deliberately not registered (2026-10-12, #92, same section).
internal sealed class CancelSignalHandler : IDisposable
{
    private readonly CancellationTokenSource cts = new();
    private readonly PosixSignalRegistration sigterm;
    private int signals;

    public CancelSignalHandler()
    {
        Console.CancelKeyPress += OnCancelKeyPress;
        // Registering SIGTERM is supported on Windows too, but there the runtime cannot cancel it: the
        // process ends once the handler returns (learn.microsoft.com, PosixSignalRegistration.Create).
        sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, OnPosixSignal);
    }

    public CancellationToken Token => cts.Token;

    private void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
    {
        if (!FirstSignal())
            return;

        e.Cancel = true;
        Console.Error.WriteLine("cancelling… press Ctrl-C again to exit without a receipt");
        cts.Cancel();
    }

    private void OnPosixSignal(PosixSignalContext context)
    {
        if (!FirstSignal())
            return;

        context.Cancel = true;
        Console.Error.WriteLine("cancelling… send SIGTERM again to exit without a receipt");
        cts.Cancel();
    }

    // Cancel left false on any later signal: the runtime terminates the process here and now, with no receipt.
    private bool FirstSignal() => Interlocked.Increment(ref signals) == 1;

    public void Dispose()
    {
        Console.CancelKeyPress -= OnCancelKeyPress;
        sigterm.Dispose();
        cts.Dispose();
    }
}
