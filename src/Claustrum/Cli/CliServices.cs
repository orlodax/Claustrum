using Claustrum.Core;
using Claustrum.Core.Backends;
using Claustrum.Core.Platform;
using Claustrum.Core.Process;
using Claustrum.Roles;

namespace Claustrum.Cli;

// One process, one set of singletons — no DI container (AGENTS.md "no reflection, no assembly
// scanning"). Every verb builds its RunRequest/config from this shared plumbing.
internal static class CliServices
{
    public static readonly IPlatform Platform = new RealPlatform();
    public static readonly BackendRegistry Backends = BackendRegistry.CreateDefault(Platform);
    public static readonly Runner Runner = new(Platform, Backends, new ProcessRunner(Platform));
    public static readonly RoleLibrary RoleLibrary = new();
    public static readonly RoleRenderer RoleRenderer = new(RoleLibrary);
}
