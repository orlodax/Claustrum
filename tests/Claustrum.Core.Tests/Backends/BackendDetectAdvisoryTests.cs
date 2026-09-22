using Claustrum.Core.Backends;
using Claustrum.Core.Tests.Testing;

namespace Claustrum.Core.Tests.Backends;

// Issue #17's other half: `Doctor.Advisories` defaults to `[]` for every backend that never sets it,
// so claude/api/opencode/copilot must all detect with an empty advisory list — cursor is the one and
// only backend with something to say (CursorBackendDetectTests covers it). Driven off
// BackendRegistry.CreateDefault so a sixth backend added later is covered automatically, the same
// reasoning RoleLibraryInvariantTests uses for RoleLibrary.ListRoles().
public sealed class BackendDetectAdvisoryTests
{
    public static IEnumerable<object[]> NonCursorBackendNames() =>
        BackendRegistry.CreateDefault(new FakePlatform()).All
            .Where(backend => backend.Name != "cursor")
            .Select(backend => new object[] { backend.Name });

    [Theory]
    [MemberData(nameof(NonCursorBackendNames))]
    public async Task NonCursorBackendsDetectWithNoAdvisoriesAsync(string backendName)
    {
        FakePlatform platform = new();
        BackendRegistry registry = BackendRegistry.CreateDefault(platform);
        registry.TryGet(backendName, out IBackend? backend);

        Doctor doctor = await backend!.DetectAsync(config: null, TestContext.Current.CancellationToken);

        Assert.Empty(doctor.Advisories);
    }
}
