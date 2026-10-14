namespace Claustrum.Core.Process;

// On Unix a child shares Claustrum's process group, so a terminal's SIGHUP/SIGINT (or `kill -- -<pgid>`) can kill it
// before CancelSignalHandler, which runs on the ThreadPool, has cancelled the token (measured 2026-10-14: `failed`, 4 of 4).
// .NET reports such a death as 128 + the signal; whether it was a cancel is only known once the token says so.
internal static class SignalExit
{
    private static readonly TimeSpan grace = TimeSpan.FromSeconds(1);

    // HUP, INT, QUIT, TERM: the four CancelSignalHandler turns into a cancel. Never on Windows, where exit codes carry no signal.
    public static bool IsCancelSignal(int exitCode) => !OperatingSystem.IsWindows() && exitCode is 129 or 130 or 131 or 143;

    // True when the token is cancelled now or within the grace. Otherwise something else killed the child: a failure.
    public static async Task<bool> CancelArrivesAsync(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return true;

        try
        {
            await Task.Delay(grace, cancellationToken);
            return false;
        }
        catch (OperationCanceledException)
        {
            return true;
        }
    }
}
