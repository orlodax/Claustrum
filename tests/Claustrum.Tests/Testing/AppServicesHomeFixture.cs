namespace Claustrum.Tests.Testing;

// JobManagerTests and ClaustrumToolsTests exercise AppServices' real singletons in-process (unlike
// CliEndToEndTests/McpStdioServerTests, which spawn a subprocess and set CLAUSTRUM_HOME on just that
// child's environment) and must never touch the developer's real ~/.claustrum/jobs (issue #3 task 2).
// AppServices.OverrideForTests points every Platform-derived singleton at this fixture's own temp
// home instead. Both test classes share the "AppServices home" collection below so xunit — which
// parallelizes across collections but runs classes inside one collection sequentially — never lets
// a test in one class see the other class's mutation of this shared mutable static mid-flight.
public sealed class AppServicesHomeFixture : IDisposable
{
    public string HomeDirectory { get; } = Directory.CreateTempSubdirectory("claustrum-appservices-home-").FullName;

    // Exposed so a test can set e.g. Platform.Environment["CLAUSTRUM_PARENT_JOB"] for its own
    // duration — one instance for the whole collection (this class's own doc comment above), so a
    // test that sets it must put it back (null, the default) before it finishes, not just when it
    // happens to pass, or a sibling test in another class of this collection inherits it.
    public HomeRedirectPlatform Platform { get; }

    public AppServicesHomeFixture()
    {
        Platform = new HomeRedirectPlatform(HomeDirectory);
        AppServices.OverrideForTests(Platform);
    }

    public void Dispose()
    {
        AppServices.ResetToReal();
        Directory.Delete(HomeDirectory, recursive: true);
    }
}

[CollectionDefinition(Name)]
public sealed class AppServicesHomeCollectionDefinition : ICollectionFixture<AppServicesHomeFixture>
{
    public const string Name = "AppServices home";
}
