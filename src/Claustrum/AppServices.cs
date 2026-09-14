using System.Runtime.CompilerServices;
using Claustrum.Core;
using Claustrum.Core.Backends;
using Claustrum.Core.Platform;
using Claustrum.Core.Process;
using Claustrum.Delegation;
using Claustrum.Roles;

// Claustrum.Tests' AppServicesHomeFixture calls OverrideForTests below (issue #3 task 2) so
// JobManagerTests/ClaustrumToolsTests never create or prune the developer's real ~/.claustrum/jobs.
[assembly: InternalsVisibleTo("Claustrum.Tests")]

namespace Claustrum;

// One process, one set of singletons — no DI container (AGENTS.md "no reflection, no assembly
// scanning"). Shared by both front doors (Cli/ and Mcp/): every verb and every MCP tool builds its
// RunRequest/config from this shared plumbing (renamed from CliServices once Mcp/ needed it too).
internal static class AppServices
{
    public static IPlatform Platform { get; private set; } = new RealPlatform();
    public static BackendRegistry Backends { get; private set; } = BackendRegistry.CreateDefault(Platform);
    public static Runner Runner { get; private set; } = new(Platform, Backends, new ProcessRunner(Platform));
    public static RoleLibrary RoleLibrary { get; } = new();
    public static RoleRenderer RoleRenderer { get; } = new(RoleLibrary);
    public static JobManager JobManager { get; private set; } = new();

    // Test-only seam (2026-09-14, issue #3 task 2): 167 real job dirs were found under
    // ~/.claustrum/jobs, 46 empty, because JobManagerTests/ClaustrumToolsTests exercised these
    // statics in-process against the real RealPlatform, and JobDirectory.Create's own Prune runs
    // against that same real tree on every call — past jobs.keep_last (200) it starts deleting the
    // user's real history. Swaps every Platform-derived singleton together so a stale one is never
    // left pointing at the real home; not exposed outside tests (internal + InternalsVisibleTo,
    // consistent with "no DI container" above).
    internal static void OverrideForTests(IPlatform platform)
    {
        Platform = platform;
        Backends = BackendRegistry.CreateDefault(platform);
        Runner = new Runner(platform, Backends, new ProcessRunner(platform));
        JobManager = new JobManager();
    }

    internal static void ResetToReal() => OverrideForTests(new RealPlatform());
}
