using Claustrum.Casts;
using Claustrum.Core;
using Claustrum.Core.Config;
using Claustrum.Core.Process;
using Claustrum.Roles;
using ModelContextProtocol;

namespace Claustrum.Mcp;

// Equivalent to Cli/ExceptionBoundary.cs, but per-call rather than one process-wide catch: there is
// no single seam here the way Program.cs wraps every CLI verb, and the MCP SDK's own tool dispatch
// already catches whatever a tool method throws, reporting a generic "An error occurred invoking
// '<tool>'" to the client with the real message only in the server's stderr log (verified 2026-09-14:
// `delegate` with an unknown role and a blind-gate violation both produced that string). `McpException`
// is the one type the SDK passes through with its `Message` intact, so every `ClaustrumTools` method
// wraps its body in `Guard`/`GuardAsync` to translate a known domain exception into one first.
public static class McpExceptionBoundary
{
    public static async Task<string> GuardAsync(Func<Task<string>> body)
    {
        try
        {
            return await body();
        }
        catch (Exception ex) when (IsDomainException(ex))
        {
            throw new McpException(ex.Message);
        }
    }

    public static string Guard(Func<string> body)
    {
        try
        {
            return body();
        }
        catch (Exception ex) when (IsDomainException(ex))
        {
            throw new McpException(ex.Message);
        }
    }

    private static bool IsDomainException(Exception ex) =>
        ex is ConfigException or RoleRenderException or CastException or BlindGateException or RunRequestException or BackendNotFoundException;
}
