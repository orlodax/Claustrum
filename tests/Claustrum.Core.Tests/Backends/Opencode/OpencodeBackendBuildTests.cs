using System.Text.Json;
using Claustrum.Core.Backends;
using Claustrum.Core.Backends.Opencode;
using Claustrum.Core.Model;
using Claustrum.Core.Tests.Testing;

namespace Claustrum.Core.Tests.Backends.Opencode;

// Pinned against opencode 2.0.12, measured 2026-09-22 (NOTES.md "The opencode and api backends,
// validated against real endpoints", items 2/4/10/11): --dir, --variant and OPENCODE_PERMISSION are
// gone; --standalone and --auto are unconditional at every rung; the permission object moves into
// OPENCODE_CONFIG_CONTENT, written twice (agent level and top level, defect 2's subagent escape),
// and every rung but full carries the three headless guards from defect 4/item 10.
public sealed class OpencodeBackendBuildTests
{
    private readonly OpencodeBackend backend = new(new FakePlatform());

    private static ResolvedRun MakeRun(
        PermissionPolicy permission,
        string effort = "high",
        string model = "openrouter/deepseek/deepseek-v4-pro",
        string? resume = null) => new(
            Role: new ResolvedRole("builder", "system body", "opencode", model, effort, permission, Blind: false, HasReport: true),
            Brief: "do the thing",
            Cwd: "/repo",
            BudgetUsd: null,
            ResumeSession: resume,
            AttachFiles: [],
            Stream: false,
            SystemPromptFilePath: "/job/system.md",
            JobDirectory: "/job",
            Env: []);

    [Fact]
    public void BasicArgvShapeMatchesTheOpencode2RunCommand()
    {
        ProcessSpec spec = backend.Build(MakeRun(new PermissionPolicy(PermissionLevel.EditShell, [])));

        Assert.Equal(
        [
            "run", "--standalone", "--agent", "claustrum-builder",
            "--model", "openrouter/deepseek/deepseek-v4-pro",
            "--format", "json", "--auto",
            "do the thing",
        ], spec.Args);
        Assert.Equal("opencode", spec.Exe);
        Assert.Empty(spec.TempFiles);
    }

    [Fact]
    public void EnvCarriesConfigContentAndDisablesTheFilewatcherNotThePermissionVar()
    {
        ProcessSpec spec = backend.Build(MakeRun(new PermissionPolicy(PermissionLevel.EditShell, [])));

        Assert.True(spec.Env.ContainsKey("OPENCODE_CONFIG_CONTENT"));
        Assert.Equal("1", spec.Env["OPENCODE_DISABLE_FILEWATCHER"]);
        Assert.False(spec.Env.ContainsKey("OPENCODE_PERMISSION"));
    }

    [Fact]
    public void ConfigContentPointsTheAgentsPromptAtTheSystemPromptFile()
    {
        ProcessSpec spec = backend.Build(MakeRun(new PermissionPolicy(PermissionLevel.EditShell, [])));

        using JsonDocument config = JsonDocument.Parse(spec.Env["OPENCODE_CONFIG_CONTENT"]);
        JsonElement agent = config.RootElement.GetProperty("agent").GetProperty("claustrum-builder");
        Assert.Equal("primary", agent.GetProperty("mode").GetString());
        Assert.Equal("{file:/job/system.md}", agent.GetProperty("prompt").GetString());
    }

    public static IEnumerable<object[]> PermissionCases()
    {
        // ReadOnly is not here: its `bash` is an ordered object whose guards carry JSON-escaped quotes, pinned decoded by the readonly tests below.
        yield return new object[]
        {
            PermissionLevel.Shell, new[] { "git push" },
            /*lang=json,strict*/ """{"edit":"deny","bash":{"*":"allow","git push":"deny","git push *":"deny"},"external_directory":"deny","question":"deny","read":{"*.env":"deny","*.env.*":"deny","*.env.example":"allow"}}""",
        };
        yield return new object[]
        {
            PermissionLevel.Edit, Array.Empty<string>(),
            /*lang=json,strict*/ """{"edit":"allow","bash":"deny","execute":"deny","external_directory":"deny","question":"deny","read":{"*.env":"deny","*.env.*":"deny","*.env.example":"allow"}}""",
        };
        yield return new object[]
        {
            PermissionLevel.EditShell, new[] { "git push" },
            /*lang=json,strict*/ """{"edit":"allow","bash":{"*":"allow","git push":"deny","git push *":"deny"},"external_directory":"deny","question":"deny","read":{"*.env":"deny","*.env.*":"deny","*.env.example":"allow"}}""",
        };
        yield return new object[]
        {
            PermissionLevel.Full, Array.Empty<string>(),
            /*lang=json,strict*/ """{"edit":"allow","bash":"allow"}""",
        };
    }

    // NOTES.md item 10: all five permission blocks, written identically on the agent and at the top
    // level (item 4's fix for a subagent escaping the primary agent's own rules) — the property order
    // below is the order opencode's own last-match-wins matcher depends on, so it is pinned exactly
    // rather than checked property-by-property.
    [Theory]
    [MemberData(nameof(PermissionCases))]
    public void PermissionIsWrittenIdenticallyOnTheAgentAndAtTheTopLevel(PermissionLevel level, string[] deny, string expectedJson)
    {
        ProcessSpec spec = backend.Build(MakeRun(new PermissionPolicy(level, deny)));

        using JsonDocument config = JsonDocument.Parse(spec.Env["OPENCODE_CONFIG_CONTENT"]);
        JsonElement agentPermission = config.RootElement.GetProperty("agent").GetProperty("claustrum-builder").GetProperty("permission");
        JsonElement topPermission = config.RootElement.GetProperty("permission");

        Assert.Equal(expectedJson, agentPermission.GetRawText());
        Assert.Equal(expectedJson, topPermission.GetRawText());
    }

    // #143: a readonly role runs git's and gh's read verbs, as claude does, and nothing else. opencode takes the LAST matching rule and matches
    // each command on its raw text, so the order is the whole design: deny everything, the 17 allows, then the guards that refuse a text
    // that could differ from what bash runs (quotes, `$`, backticks, `\`, braces, redirects, the three git options that run or read elsewhere),
    // then the role's own denies, each as the exact command and its word form. The text is spelled out here, not read from the code.
    private static readonly string[] readOnlyAllows =
    [
        "git diff", "git diff *", "git log", "git log *", "git show", "git show *", "git status", "git status *", "git rev-parse *",
        "gh pr view *", "gh pr diff *", "gh pr list", "gh pr list *", "gh pr checks *", "gh issue view *", "gh issue list", "gh issue list *",
    ];

    private static readonly string[] readOnlyGuards = ["*\"*", "*'*", "*`*", "*$*", "*\\*", "*{*", "*>*", "*<*", "*--output*", "*--no-index*", "*--ext-diff*"];

    private static (string Pattern, string Action)[] ReadOnlyBash(params string[] denies) =>
    [
        ("*", "deny"),
        .. readOnlyAllows.Select(command => (command, "allow")),
        .. readOnlyGuards.Select(guard => (guard, "deny")),
        .. denies.SelectMany(deny => new[] { (deny, "deny"), ($"{deny} *", "deny") }),
    ];

    // The role's deny list, `;`-separated for the theory.
    [Theory]
    [InlineData("")]
    [InlineData("gh pr merge")]
    [InlineData("gh pr merge;git push")]
    public void ReadOnlyBashDeniesAllThenAllowsTheReadVerbsThenTheGuardsThenTheRolesDeniesInThatOrder(string denies)
    {
        string[] deny = denies.Length == 0 ? [] : denies.Split(';');
        ProcessSpec spec = backend.Build(MakeRun(new PermissionPolicy(PermissionLevel.ReadOnly, deny)));

        using JsonDocument config = JsonDocument.Parse(spec.Env["OPENCODE_CONFIG_CONTENT"]);
        JsonElement agent = config.RootElement.GetProperty("agent").GetProperty("claustrum-builder").GetProperty("permission");
        JsonElement top = config.RootElement.GetProperty("permission");

        Assert.Equal(top.GetRawText(), agent.GetRawText());
        (string, string)[] bash = [.. top.GetProperty("bash").EnumerateObject().Select(rule => (rule.Name, rule.Value.GetString()!))];
        Assert.Equal(ReadOnlyBash(deny), bash);
        Assert.Equal("deny", top.GetProperty("edit").GetString());
        Assert.Equal("deny", top.GetProperty("execute").GetString());
        Assert.Equal("deny", top.GetProperty("external_directory").GetString());
        Assert.Equal("deny", top.GetProperty("question").GetString());
        Assert.Equal("allow", top.GetProperty("read").GetProperty("*.env.example").GetString());
        Assert.Equal(["edit", "bash", "execute", "external_directory", "question", "read"], [.. top.EnumerateObject().Select(property => property.Name)]);
    }

    // Read the other way: the guards come after every allow, so that no allowed pattern can win over a guard for the same text.
    [Fact]
    public void EveryReadOnlyGuardComesAfterEveryAllowSoTheLastMatchRefusesTheText()
    {
        ProcessSpec spec = backend.Build(MakeRun(new PermissionPolicy(PermissionLevel.ReadOnly, [])));

        using JsonDocument config = JsonDocument.Parse(spec.Env["OPENCODE_CONFIG_CONTENT"]);
        string[] names = [.. config.RootElement.GetProperty("permission").GetProperty("bash").EnumerateObject().Select(rule => rule.Name)];

        Assert.Equal(17, readOnlyAllows.Length);
        Assert.Equal(1 + 17 + 11, names.Length);
        Assert.All(readOnlyGuards, guard => Assert.True(Array.IndexOf(names, guard) > Array.FindLastIndex(names, name => readOnlyAllows.Contains(name))));
    }

    // Every deny entry is written twice — the exact key and the " *" word form — and both come after
    // the allow-all, because opencode takes the last matching rule (the "*" allow would win over a
    // deny written before it). An entry that carries its own `*` keeps it in both keys.
    [Theory]
    [InlineData(PermissionLevel.Shell)]
    [InlineData(PermissionLevel.EditShell)]
    public void EveryDenyEntryIsWrittenExactAndAsAWordFormAfterTheAllowAll(PermissionLevel level)
    {
        ProcessSpec spec = backend.Build(MakeRun(new PermissionPolicy(level, ["git push", "rm -rf*"])));

        using JsonDocument config = JsonDocument.Parse(spec.Env["OPENCODE_CONFIG_CONTENT"]);
        JsonProperty[] agentBash = [.. config.RootElement.GetProperty("agent").GetProperty("claustrum-builder").GetProperty("permission").GetProperty("bash").EnumerateObject()];
        JsonProperty[] topBash = [.. config.RootElement.GetProperty("permission").GetProperty("bash").EnumerateObject()];

        string[] keys = ["*", "git push", "git push *", "rm -rf*", "rm -rf* *"];
        Assert.Equal(keys, agentBash.Select(property => property.Name));
        Assert.Equal(keys, topBash.Select(property => property.Name));
        Assert.Equal("allow", agentBash[0].Value.GetString());
        Assert.All(agentBash.Skip(1), property => Assert.Equal("deny", property.Value.GetString()));
    }

    // Order is load-bearing at runtime — opencode's matcher is `rules.findLast(...)`, not
    // most-specific — so it is pinned on its own rather than trusted to the raw-text equality above.
    [Fact]
    public void TheEnvExampleAllowIsTheLastReadRuleSoItWinsOverTheTwoEnvDenies()
    {
        ProcessSpec spec = backend.Build(MakeRun(new PermissionPolicy(PermissionLevel.ReadOnly, [])));

        using JsonDocument config = JsonDocument.Parse(spec.Env["OPENCODE_CONFIG_CONTENT"]);
        JsonElement read = config.RootElement.GetProperty("permission").GetProperty("read");

        Assert.Equal("*.env.example", read.EnumerateObject().Last().Name);
    }

    [Fact]
    public void TheTiersEffortNeverRidesTheModelAsAHashSuffix()
    {
        ProcessSpec spec = backend.Build(MakeRun(new PermissionPolicy(PermissionLevel.Full, []), effort: "xhigh", model: "openrouter/deepseek/deepseek-v4-flash"));

        int index = Array.IndexOf(spec.Args, "--model");
        Assert.Equal("openrouter/deepseek/deepseek-v4-flash", spec.Args[index + 1]);
    }

    [Fact]
    public void EmptyEffortLeavesTheModelAsIs()
    {
        ProcessSpec spec = backend.Build(MakeRun(new PermissionPolicy(PermissionLevel.Full, []), effort: ""));

        int index = Array.IndexOf(spec.Args, "--model");
        Assert.Equal("openrouter/deepseek/deepseek-v4-pro", spec.Args[index + 1]);
    }

    [Fact]
    public void AModelThatAlreadyNamesAVariantIsLeftAlone()
    {
        ProcessSpec spec = backend.Build(MakeRun(new PermissionPolicy(PermissionLevel.Full, []), effort: "high", model: "openrouter/deepseek/deepseek-v4-flash#custom"));

        int index = Array.IndexOf(spec.Args, "--model");
        Assert.Equal("openrouter/deepseek/deepseek-v4-flash#custom", spec.Args[index + 1]);
    }

    [Fact]
    public void ResumeSessionIsPassedAsSessionFlagBeforeTheBrief()
    {
        ProcessSpec spec = backend.Build(MakeRun(new PermissionPolicy(PermissionLevel.Full, []), resume: "ses-123"));

        int index = Array.IndexOf(spec.Args, "--session");
        Assert.True(index >= 0);
        Assert.Equal("ses-123", spec.Args[index + 1]);
        Assert.Equal("do the thing", spec.Args[^1]);
    }

    [Fact]
    public void BriefIsTheFinalArgument()
    {
        ProcessSpec spec = backend.Build(MakeRun(new PermissionPolicy(PermissionLevel.Full, [])));

        Assert.Equal("do the thing", spec.Args[^1]);
    }

    [Fact]
    public void AgentNameIsPrefixedWithClaustrumAndTheRoleName()
    {
        ProcessSpec spec = backend.Build(MakeRun(new PermissionPolicy(PermissionLevel.Full, [])));

        int index = Array.IndexOf(spec.Args, "--agent");
        Assert.Equal("claustrum-builder", spec.Args[index + 1]);
    }
}
