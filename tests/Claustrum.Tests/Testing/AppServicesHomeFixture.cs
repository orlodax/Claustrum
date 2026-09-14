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

    public AppServicesHomeFixture() => AppServices.OverrideForTests(new HomeRedirectPlatform(HomeDirectory));

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
