using System.Text.Json;
using Claustrum.Core.Backends;
using Claustrum.Core.Backends.Acp;
using Claustrum.Core.Backends.Opencode;
using Claustrum.Core.Model;
using Claustrum.Core.Tests.Backends.Acp;
using Claustrum.Core.Tests.Testing;

namespace Claustrum.Core.Tests.Backends.Opencode;

// #144: read dirs reach opencode (native and acp-opencode, one config) as `external_directory` allows for `<dir>` and `<dir>/*`, everything else
// still denied; at the edit levels also as `edit` denies for the same two patterns, so a dir stays read-only. opencode's matcher has no escape: a
// path with `*`, `?` or `\` would name other directories too, so such a dir is refused. Measured on opencode 2.0.30, 2026-11-02 (NOTES.md
// "Read dirs and the worktree hook").
public sealed class OpencodeBackendReadDirsTests
{
    private static readonly string[] dirs = ["/srv/shared", "/srv/devkit/.git"];

    private readonly OpencodeBackend backend = new(new FakePlatform());

    private static ResolvedRun Run(PermissionLevel level, string[]? readDirs = null) => new(
        Role: new ResolvedRole("builder", "system body", "opencode", "openrouter/deepseek/deepseek-v4-pro", "high", new PermissionPolicy(level, []), Blind: false, HasReport: true),
        Brief: "do the thing",
        Cwd: "/repo",
        BudgetUsd: null,
        ResumeSession: null,
        AttachFiles: [],
        Stream: false,
        SystemPromptFilePath: "/job/system.md",
        JobDirectory: "/job",
        Env: [],
        ReadDirs: readDirs);

    private JsonDocument Config(PermissionLevel level, string[]? readDirs) => JsonDocument.Parse(backend.Build(Run(level, readDirs)).Env["OPENCODE_CONFIG_CONTENT"]);

    private static (string Pattern, string Action)[] Rules(JsonElement permission, string name) =>
        [.. permission.GetProperty(name).EnumerateObject().Select(rule => (rule.Name, rule.Value.GetString()!))];

    private static (string Pattern, string Action)[] Expected(string everywhere, string inDirs, params string[] readDirs) =>
        [("*", everywhere), .. readDirs.SelectMany(dir => new[] { (dir, inDirs), ($"{dir}/*", inDirs) })];

    // ---- external_directory --------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(PermissionLevel.ReadOnly)]
    [InlineData(PermissionLevel.Shell)]
    [InlineData(PermissionLevel.Edit)]
    [InlineData(PermissionLevel.EditShell)]
    public void ExternalDirectoryDeniesEverythingButTheDirAndItsContentsInThatOrder(PermissionLevel level)
    {
        using JsonDocument config = Config(level, dirs);

        Assert.Equal(Expected("deny", "allow", dirs), Rules(config.RootElement.GetProperty("permission"), "external_directory"));
        Assert.Equal(
            config.RootElement.GetProperty("permission").GetRawText(),
            config.RootElement.GetProperty("agent").GetProperty("claustrum-builder").GetProperty("permission").GetRawText());
    }

    [Theory]
    [InlineData(PermissionLevel.ReadOnly)]
    [InlineData(PermissionLevel.Shell)]
    [InlineData(PermissionLevel.Edit)]
    [InlineData(PermissionLevel.EditShell)]
    public void WithNoReadDirsExternalDirectoryIsTheSamePlainDenyAsBefore(PermissionLevel level)
    {
        foreach (string[]? none in new string[]?[] { null, [] })
        {
            using JsonDocument config = Config(level, none);

            Assert.Equal("deny", config.RootElement.GetProperty("permission").GetProperty("external_directory").GetString());
        }
    }

    // ---- edit ----------------------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(PermissionLevel.Edit)]
    [InlineData(PermissionLevel.EditShell)]
    public void AtTheEditLevelsEditAllowsEverywhereExceptTheDirAndItsContents(PermissionLevel level)
    {
        using JsonDocument config = Config(level, dirs);

        JsonElement permission = config.RootElement.GetProperty("permission");
        Assert.Equal(Expected("allow", "deny", dirs), Rules(permission, "edit"));
        Assert.Equal(permission.GetRawText(), config.RootElement.GetProperty("agent").GetProperty("claustrum-builder").GetProperty("permission").GetRawText());
    }

    [Theory]
    [InlineData(PermissionLevel.ReadOnly)]
    [InlineData(PermissionLevel.Shell)]
    public void BelowTheEditLevelsEditStaysTheStringDenyWhateverTheDirs(PermissionLevel level)
    {
        using JsonDocument config = Config(level, dirs);

        Assert.Equal("deny", config.RootElement.GetProperty("permission").GetProperty("edit").GetString());
    }

    [Theory]
    [InlineData(PermissionLevel.Edit)]
    [InlineData(PermissionLevel.EditShell)]
    public void AtTheEditLevelsWithoutDirsEditIsTheSamePlainAllowAsBefore(PermissionLevel level)
    {
        using JsonDocument config = Config(level, null);

        Assert.Equal("allow", config.RootElement.GetProperty("permission").GetProperty("edit").GetString());
    }

    [Fact]
    public void FullIsUnchangedByReadDirsAndStillWritesNoGuardsAtAll()
    {
        ProcessSpec with = backend.Build(Run(PermissionLevel.Full, dirs));
        ProcessSpec without = backend.Build(Run(PermissionLevel.Full));

        Assert.Equal(without.Env["OPENCODE_CONFIG_CONTENT"], with.Env["OPENCODE_CONFIG_CONTENT"]);
        using JsonDocument config = JsonDocument.Parse(with.Env["OPENCODE_CONFIG_CONTENT"]);
        Assert.Equal(/*lang=json,strict*/ """{"edit":"allow","bash":"allow"}""", config.RootElement.GetProperty("permission").GetRawText());
    }

    // The two headless guards that are not about paths stay whatever the dirs: no question to a human who is not there, no .env read.
    [Theory]
    [InlineData(PermissionLevel.ReadOnly)]
    [InlineData(PermissionLevel.Shell)]
    [InlineData(PermissionLevel.Edit)]
    [InlineData(PermissionLevel.EditShell)]
    public void TheQuestionDenyAndTheEnvGuardsAreStillThereBesideTheReadDirs(PermissionLevel level)
    {
        using JsonDocument config = Config(level, dirs);

        JsonElement permission = config.RootElement.GetProperty("permission");
        Assert.Equal("deny", permission.GetProperty("question").GetString());
        Assert.Equal([("*.env", "deny"), ("*.env.*", "deny"), ("*.env.example", "allow")], Rules(permission, "read"));
    }

    // ---- a path opencode cannot name exactly ----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("/srv/lib*")]
    [InlineData("/srv/li?")]
    [InlineData("/srv/a*b?c")]
    public void ADirWithAStarOrAQuestionMarkIsLeftOutBelowFullBecauseItWouldBeAWildcard(string odd)
    {
        foreach (PermissionLevel level in new[] { PermissionLevel.ReadOnly, PermissionLevel.Shell, PermissionLevel.Edit, PermissionLevel.EditShell })
        {
            using JsonDocument config = Config(level, [odd, "/srv/shared"]);

            Assert.Equal(Expected("deny", "allow", "/srv/shared"), Rules(config.RootElement.GetProperty("permission"), "external_directory"));
            Assert.DoesNotContain(odd, config.RootElement.GetRawText(), StringComparison.Ordinal);
            Assert.NotNull(backend.ReadDirRefusal(odd, level));
        }

        Assert.Null(backend.ReadDirRefusal(odd, PermissionLevel.Full));
    }

    [Fact]
    public void ABackslashIsRefusedOutsideWindowsWhereOpencodeReadsItAsASlash()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("a backslash is the separator on Windows, so it is not a character to refuse there.");

        using JsonDocument config = Config(PermissionLevel.EditShell, ["/srv/a\\b", "/srv/shared"]);

        Assert.Equal(Expected("deny", "allow", "/srv/shared"), Rules(config.RootElement.GetProperty("permission"), "external_directory"));
        Assert.Contains("/srv/a\\b", backend.ReadDirRefusal("/srv/a\\b", PermissionLevel.Shell), StringComparison.Ordinal);
    }

    [Fact]
    public void WhenEveryDirIsRefusedThePermissionIsTheSamePlainStringsAsWithNone()
    {
        using JsonDocument refused = Config(PermissionLevel.EditShell, ["/srv/lib*"]);
        using JsonDocument none = Config(PermissionLevel.EditShell, null);

        Assert.Equal(none.RootElement.GetRawText(), refused.RootElement.GetRawText());
    }

    // ---- acp-opencode is opencode's config, byte for byte ---------------------------------------------------------------------------------

    [Theory]
    [InlineData(PermissionLevel.ReadOnly)]
    [InlineData(PermissionLevel.Shell)]
    [InlineData(PermissionLevel.Edit)]
    [InlineData(PermissionLevel.EditShell)]
    [InlineData(PermissionLevel.Full)]
    public void TheAcpBackendWritesTheSameConfigWithTheSameReadDirs(PermissionLevel level)
    {
        FakePlatform platform = new();
        AcpBackend acp = AcpLines.Backend(platform);
        ResolvedRun run = Run(level, [.. dirs, "/srv/odd*"]);

        Assert.Equal(backend.Build(run).Env["OPENCODE_CONFIG_CONTENT"], acp.Build(run).Env["OPENCODE_CONFIG_CONTENT"]);
        Assert.Equal(backend.Build(run).Env["OPENCODE_CONFIG_CONTENT"], new OpencodeAcpHarness(platform).Prepare(run).Env["OPENCODE_CONFIG_CONTENT"]);
    }

    [Fact]
    public void BothOpencodeBackendsHonourReadDirsAndAnswerForRefusalsAlike()
    {
        AcpBackend acp = AcpLines.Backend();

        Assert.True(backend.HonoursReadDirs);
        Assert.True(acp.HonoursReadDirs);
        Assert.False(((IBackend)backend).ReadDirsWritableAt(PermissionLevel.EditShell));
        Assert.False(((IBackend)acp).ReadDirsWritableAt(PermissionLevel.EditShell));
        Assert.Equal(backend.ReadDirRefusal("/srv/lib*", PermissionLevel.Edit), acp.ReadDirRefusal("/srv/lib*", PermissionLevel.Edit));
        Assert.Null(acp.ReadDirRefusal("/srv/shared", PermissionLevel.Edit));
    }
}
