using Claustrum.Core.Backends;
using Claustrum.Core.Backends.Cursor;
using Claustrum.Core.Config;
using Claustrum.Core.Tests.Testing;

namespace Claustrum.Core.Tests.Backends.Cursor;

// Issue #17: cursor's prompt-only deny list is a `Doctor.Advisories` entry, not a `Problems` one, and
// DetectAsync attaches it unconditionally — before the binary is even located (CursorBackend.cs
// "The advisory holds whether or not the binary is here"). PathEntries stays empty so PATH search
// can never accidentally find a real cursor-agent on the machine running this suite; the config path
// points at a file that was never created, exercising BinaryLocator's not-found branch the same way.
public sealed class CursorBackendDetectTests
{
    [Fact]
    public async Task NotFoundConfiguredPathStillCarriesExactlyOneDenyAdvisoryAsync()
    {
        FakePlatform platform = new();
        CursorBackend backend = new(platform);
        string missingPath = Path.Combine(Path.GetTempPath(), "claustrum-cursor-detect-" + Guid.NewGuid(), "cursor-agent");

        Doctor doctor = await backend.DetectAsync(new BackendConfig(missingPath, Injection: null), TestContext.Current.CancellationToken);

        Assert.False(doctor.Found);
        Assert.Equal(
            ["""deny list is enforced by prompt only: cursor-agent has no native deny flag (NOTES.md "The cursor backend, validated against a real install", box 11)"""],
            doctor.Advisories);
    }
}
