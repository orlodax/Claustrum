namespace Claustrum.Cli;

/// <summary>
/// `run`'s and `coordinate`'s Ctrl-C: the first press cancels <see cref="Token"/>, so Runner kills the
/// backend's tree and writes a `cancelled` receipt; a second one lets the runtime end the process.
/// </summary>
// With ProcessTerminationTimeout null (Program.cs, #88) nothing else ends a cancel path that hangs — the
// post-run commit runs the repo's hooks, bounded only by 5 minutes (2026-10-09, NOTES.md "Ctrl-C reaches the runner").
internal sealed class CancelKeyHandler : IDisposable
{
    private readonly CancellationTokenSource cts = new();
    private int presses;

    public CancelKeyHandler() => Console.CancelKeyPress += OnCancelKeyPress;

    public CancellationToken Token => cts.Token;

    private void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
    {
        // e.Cancel left false: the runtime terminates the process here and now, with no receipt.
        if (Interlocked.Increment(ref presses) > 1)
            return;

        e.Cancel = true;
        Console.Error.WriteLine("cancelling… press Ctrl-C again to exit without a receipt");
        cts.Cancel();
    }

    public void Dispose()
    {
        Console.CancelKeyPress -= OnCancelKeyPress;
        cts.Dispose();
    }
}
