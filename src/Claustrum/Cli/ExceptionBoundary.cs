using Claustrum.Casts;
using Claustrum.Core;
using Claustrum.Core.Config;
using Claustrum.Core.Process;
using Claustrum.Delegation;
using Claustrum.Roles;

namespace Claustrum.Cli;

// Single top-level catch around `ParseResult.InvokeAsync()` (Program.cs) so no verb's failure path
// prints a raw stack trace (review finding #1) — each verb used to map its own exceptions, or not at
// all, so anything RunCommand.cs did not special-case (and everything in Roles/Backends/Jobs/Sync)
// crashed straight through. CLAUSTRUM_DEBUG=1 opts back into the trace for diagnosis. Its writes are SafeConsole's: an
// exception after the terminal is gone would otherwise die of EIO inside this catch (2026-10-14, found by review).
public static class ExceptionBoundary
{
    public static int Handle(Exception exception)
    {
        switch (exception)
        {
            case ConfigException or RoleRenderException or CliUsageException or MachineryRefusedException or BlindGateException or RunRequestException or CastException or ArgumentException:
                SafeConsole.Error(exception.Message);
                return ExitCodes.Usage;
            case BackendNotFoundException:
                SafeConsole.Error(exception.Message);
                return ExitCodes.BackendMissing;
            default:
                SafeConsole.Error($"error: {exception.Message}");
                if (Environment.GetEnvironmentVariable("CLAUSTRUM_DEBUG") == "1")
                    SafeConsole.Error(exception.ToString());
                return ExitCodes.BackendFailure;
        }
    }
}
