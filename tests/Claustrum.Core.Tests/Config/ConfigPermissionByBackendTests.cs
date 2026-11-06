using Claustrum.Core.Config;
using Claustrum.Core.Model;
using Claustrum.Core.Platform;
using Claustrum.Core.Tests.Testing;
using CoreConfig = Claustrum.Core.Config.Config;

namespace Claustrum.Core.Tests;

// RenderedRole.PermissionByBackend sits below config and flags: it replaces role.json's own `permission` on its backend only.
public sealed class ConfigPermissionByBackendTests
{
    private static readonly string repoDir = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "claustrum-fake-repo"));
    private static readonly string homeDir = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "claustrum-fake-home"));

    private static readonly RenderedRole architect = new(
        "architect", "body", "frontier-reasoning", "high", "edit+shell", [], "architect", Blind: false,
        PermissionByBackend: new Dictionary<string, string> { ["codex"] = "full" });

    private static CoreConfig Load(string? user = null)
    {
        FakePlatform platform = new() { Os = ClaustrumOs.Linux, HomeDirectory = homeDir };
        if (user is not null)
            platform.Files[Path.Combine(homeDir, ".config", "claustrum", "config.json")] = user;
        return CoreConfig.Load(platform, repoDir);
    }

    [Theory]
    [InlineData("codex:gpt-6.1-sol", PermissionLevel.Full)]
    [InlineData("claude:opus", PermissionLevel.EditShell)]
    [InlineData("opencode:openrouter/deepseek/deepseek-v4-pro", PermissionLevel.EditShell)]
    public void TheEntryAppliesOnItsBackendOnly(string model, PermissionLevel expected)
    {
        ResolvedRole resolved = Load().Resolve(architect, new ConfigOverrides(Model: model));

        Assert.Equal(expected, resolved.Permission.Level);
    }

    [Fact]
    public void AConfigLevelAndAFlagBothWinOverIt()
    {
        CoreConfig config = Load(/*lang=json,strict*/ """{"roles":{"architect":{"permission":"edit"}}}""");

        Assert.Equal(PermissionLevel.Edit, config.Resolve(architect, new ConfigOverrides(Model: "codex:gpt-6.1-sol")).Permission.Level);
        Assert.Equal(PermissionLevel.ReadOnly, config.Resolve(architect, new ConfigOverrides(Model: "codex:gpt-6.1-sol", Permission: "readonly")).Permission.Level);
    }
}
