using Claustrum.Core;
using Claustrum.Core.Backends;
using Claustrum.Core.Platform;
using Claustrum.Core.Process;
using Claustrum.Roles;

namespace Claustrum;

// One process, one set of singletons — no DI container (AGENTS.md "no reflection, no assembly
// scanning"). Shared by both front doors (Cli/ and Mcp/): every verb and every MCP tool builds its
// RunRequest/config from this shared plumbing (renamed from CliServices once Mcp/ needed it too).
internal static class AppServices
{
    public static readonly IPlatform Platform = new RealPlatform();
    public static readonly BackendRegistry Backends = BackendRegistry.CreateDefault(Platform);
    public static readonly Runner Runner = new(Platform, Backends, new ProcessRunner(Platform));
    public static readonly RoleLibrary RoleLibrary = new();
    public static readonly RoleRenderer RoleRenderer = new(RoleLibrary);
}
