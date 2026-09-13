using Claustrum.Core;
using Claustrum.Core.Config;
using Claustrum.Core.Process;
using Claustrum.Roles;

namespace Claustrum.Cli;

// Single top-level catch around `ParseResult.InvokeAsync()` (Program.cs) so no verb's failure path
// prints a raw stack trace (review finding #1) — each verb used to map its own exceptions, or not at
// all, so anything RunCommand.cs did not special-case (and everything in Roles/Backends/Jobs/Sync)
// crashed straight through. CLAUSTRUM_DEBUG=1 opts back into the trace for diagnosis.
public static class ExceptionBoundary
{
    public static int Handle(Exception exception)
    {
        switch (exception)
        {
            case ConfigException or RoleRenderException or CliUsageException or BlindGateException or ArgumentException:
                Console.Error.WriteLine(exception.Message);
                return ExitCodes.Usage;
            case BackendNotFoundException:
                Console.Error.WriteLine(exception.Message);
                return ExitCodes.BackendMissing;
            default:
                Console.Error.WriteLine($"error: {exception.Message}");
                if (Environment.GetEnvironmentVariable("CLAUSTRUM_DEBUG") == "1")
                    Console.Error.WriteLine(exception.ToString());
                return ExitCodes.BackendFailure;
        }
    }
}
