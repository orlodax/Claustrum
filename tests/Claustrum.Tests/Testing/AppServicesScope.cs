namespace Claustrum.Tests.Testing;

/// <summary>
/// Points AppServices at a platform of the test's own for its lifetime and puts back the one that was there. For the
/// in-process tests of code that reads <c>AppServices.Platform</c> (CastApplication, the MCP tools) and needs a user config
/// the shared "AppServices home" fixture does not have - or none at all. Only for classes in that collection: it is a shared
/// mutable static, and the collection is what runs its classes one at a time.
/// </summary>
internal sealed class AppServicesScope : IDisposable
{
    private readonly Core.Platform.IPlatform previous = AppServices.Platform;

    public AppServicesScope(Core.Platform.IPlatform platform) => AppServices.OverrideForTests(platform);

    public void Dispose() => AppServices.OverrideForTests(previous);
}
