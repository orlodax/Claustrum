using System.Security.Cryptography;
using System.Text;
using Claustrum.Roles.Model;
using Claustrum.Roles.Sync;

namespace Claustrum.Roles.Tests.Sync;

// #82: a synced `<role>-xhigh`/`-max` file is a subagent's whole system prompt, so it carries the base
// role's full body (rendered at the stub's tier, for the stub's harness) after `## Non-negotiable`. The files
// are read back from disk through the real syncs; the RoleRenderer-level cases cover every role and harness.
public sealed class TierStubBodyTests : IDisposable
{
    private static readonly string[] generatedTiers = ["xhigh", "max"];

    private readonly string cwd = Directory.CreateTempSubdirectory("claustrum-tier-stub-").FullName;
    private readonly string fakeHome = Directory.CreateTempSubdirectory("claustrum-tier-stub-home-").FullName;
    private readonly RoleLibrary library = new();

    public void Dispose()
    {
        Directory.Delete(cwd, recursive: true);
        Directory.Delete(fakeHome, recursive: true);
    }

    private string SyncedFile(string harness, string role, string name)
    {
        HarnessSyncs.Run(harness, library, fakeHome, cwd, [role]);
        return File.ReadAllText(HarnessSyncs.AgentFile(harness, HarnessSyncs.RepoRoot(harness, cwd), name));
    }

    private static (List<(string Key, string Value)> Fields, string AfterFrontmatter) SplitFrontmatter(string content)
    {
        string[] lines = content.Split('\n');
        Assert.Equal("---", lines[0]);
        int close = Array.IndexOf(lines, "---", 1);
        Assert.True(close > 0, "frontmatter is never closed");

        List<(string Key, string Value)> fields = [];
        foreach (string line in lines[1..close])
        {
            // A nested block (opencode's `permission:`/`bash:` deny list) is folded into its parent key's value.
            if (line.StartsWith(' '))
            {
                (string key, string value) = fields[^1];
                fields[^1] = (key, $"{value}\n{line}");
                continue;
            }

            int colon = line.IndexOf(':', StringComparison.Ordinal);
            fields.Add((line[..colon], line[(colon + 1)..].TrimStart(' ')));
        }

        return (fields, string.Join('\n', lines[(close + 1)..]));
    }

    private static string Field(List<(string Key, string Value)> fields, string key) => fields.Single(field => field.Key == key).Value;

    private static int Count(string text, string needle) => text.Split(needle).Length - 1;

    [Theory]
    [InlineData("claude", "xhigh")]
    [InlineData("claude", "max")]
    [InlineData("opencode", "xhigh")]
    [InlineData("opencode", "max")]
    [InlineData("copilot", "xhigh")]
    [InlineData("copilot", "max")]
    [InlineData("cursor", "xhigh")]
    [InlineData("cursor", "max")]
    public void TheBuilderStubCarriesNonNegotiableThenTheReportFormatTheDelegationContractAndTheHouseRules(string harness, string tier)
    {
        string content = SyncedFile(harness, "builder", $"builder-{tier}");

        int nonNegotiable = content.IndexOf("\n## Non-negotiable\n", StringComparison.Ordinal);
        int reportFormat = content.IndexOf("\n## Report format\n", StringComparison.Ordinal);
        int delegation = content.IndexOf("\n## Delegation contract\n", StringComparison.Ordinal);
        int houseRules = content.IndexOf("\n## House rules\n", StringComparison.Ordinal);
        Assert.True(nonNegotiable > 0, "no ## Non-negotiable");
        Assert.True(reportFormat > nonNegotiable, "## Report format must follow ## Non-negotiable");
        Assert.True(delegation > reportFormat, "## Delegation contract must follow ## Report format");
        Assert.True(houseRules > delegation, "## House rules must follow ## Delegation contract");
        Assert.Equal(1, Count(content, "\n## Non-negotiable\n"));
        Assert.Contains($"effort `{tier}`", content, StringComparison.Ordinal);
        Assert.DoesNotContain("{{", content, StringComparison.Ordinal);
        Assert.DoesNotContain("}}", content, StringComparison.Ordinal);
    }

    [Fact]
    public void TheClaudeStubNamesTheAgentToolAndTheOpencodeStubTheCliCall()
    {
        string claude = SyncedFile("claude", "builder", "builder-xhigh");
        string opencode = SyncedFile("opencode", "builder", "builder-xhigh");

        Assert.Contains("""the `Agent` tool with `subagent_type: "architect"`""", claude, StringComparison.Ordinal);
        Assert.DoesNotContain("claustrum run", claude, StringComparison.Ordinal);
        Assert.Contains("`claustrum run --json", opencode, StringComparison.Ordinal);
        Assert.DoesNotContain("subagent_type", opencode, StringComparison.Ordinal);
        Assert.DoesNotContain("the `Agent` tool", opencode, StringComparison.Ordinal);
    }

    // The builder's delegation is a prose clause; the architect's lists a spawn per target role on claude only.
    [Fact]
    public void TheArchitectStubSpellsItsBuilderSpawnPerHarness()
    {
        string claude = SyncedFile("claude", "architect", "architect-max");
        string opencode = SyncedFile("opencode", "architect", "architect-max");

        Assert.Contains("""the `Agent` tool with `subagent_type: "builder"`""", claude, StringComparison.Ordinal);
        Assert.Contains("""the `Agent` tool with `subagent_type: "tester"`""", claude, StringComparison.Ordinal);
        Assert.Contains("`claustrum run <role> --brief-file <path> --json`", opencode, StringComparison.Ordinal);
        Assert.DoesNotContain("subagent_type", opencode, StringComparison.Ordinal);
    }

    // opencode has no effort key: `variant:` is opt-in and rides only a model spec that names one (#35), so the
    // default-alias stub is the base agent's frontmatter unchanged.
    [Theory]
    [InlineData("claude", "effort", "xhigh")]
    [InlineData("opencode", null, "xhigh")]
    [InlineData("claude", "effort", "max")]
    [InlineData("opencode", null, "max")]
    public void TheStubFrontmatterIsTheBaseAgentsWithOnlyTheNameAndTheEffortMoved(string harness, string? effortKey, string tier)
    {
        string role = "builder";
        (List<(string Key, string Value)> baseFields, _) = SplitFrontmatter(SyncedFile(harness, role, role));
        (List<(string Key, string Value)> stubFields, string rest) = SplitFrontmatter(SyncedFile(harness, role, $"{role}-{tier}"));

        Assert.Equal(baseFields.Select(field => field.Key), stubFields.Select(field => field.Key));
        if (effortKey is not null)
        {
            Assert.Equal("high", Field(baseFields, effortKey));
            Assert.Equal(tier, Field(stubFields, effortKey));
        }
        else
        {
            Assert.DoesNotContain(baseFields, field => field.Key == "variant");
            Assert.DoesNotContain(stubFields, field => field.Key == "variant");
        }

        Assert.Equal(Field(baseFields, "model"), Field(stubFields, "model"));
        if (harness == "claude")
        {
            Assert.Equal($"{role}-{tier}", Field(stubFields, "name"));
            Assert.Equal(Field(baseFields, "tools"), Field(stubFields, "tools"));
            Assert.Equal(Field(baseFields, "color"), Field(stubFields, "color"));
        }

        // Nothing of the body leaked upwards: the file is frontmatter, one marker, then the body.
        Assert.StartsWith(SyncWriter.MarkerPrefix, rest, StringComparison.Ordinal);
        Assert.Equal(1, Count(rest, SyncWriter.MarkerPrefix));
    }

    // The marker's hash covers the body (NOTES.md "Generated-file marker covers the body"), and the body now holds the
    // base role's text: a hand edit anywhere in it must still read as a change.
    [Theory]
    [InlineData("claude")]
    [InlineData("opencode")]
    [InlineData("copilot")]
    [InlineData("cursor")]
    public void TheMarkerHashCoversTheWholeStubBodyIncludingTheBaseRolesText(string harness)
    {
        (_, string rest) = SplitFrontmatter(SyncedFile(harness, "tester", "tester-xhigh"));

        int newline = rest.IndexOf('\n', StringComparison.Ordinal);
        string marker = rest[..newline];
        string body = rest[newline..].Trim();
        string hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(body)));

        Assert.EndsWith($" sha256={hash} -->", marker, StringComparison.Ordinal);
        Assert.Contains("## House rules", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("copilot")]
    [InlineData("cursor")]
    public void TheCopilotAndCursorStubsCarryTheReportFormatAndTheHouseRulesToo(string harness)
    {
        string content = SyncedFile(harness, "builder", "builder-xhigh");

        int nonNegotiable = content.IndexOf("\n## Non-negotiable\n", StringComparison.Ordinal);
        Assert.True(nonNegotiable > 0, "no ## Non-negotiable");
        Assert.True(content.IndexOf("\n## Report format\n", StringComparison.Ordinal) > nonNegotiable);
        Assert.True(content.IndexOf("\n## House rules\n", StringComparison.Ordinal) > nonNegotiable);
        Assert.Contains("`claustrum run --json", content, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryRolesStubEndsWithItsBaseRolesFullBodyForEveryHarnessAndTier()
    {
        RoleRenderer renderer = new(library);

        foreach (string role in library.ListRoles())
        {
            RoleDefinition definition = library.LoadRole(role, cwd).Definition;
            foreach (string harness in definition.Harnesses)
            {
                foreach (string tier in generatedTiers.Where(definition.Tiers.ContainsKey))
                {
                    string stub = renderer.RenderTierStub(role, tier, harness, cwd);
                    string body = renderer.Render(role, tier, harness, cwd).SystemBody.Trim();
                    string where = $"role '{role}' harness '{harness}' tier '{tier}'";
                    int heading = stub.IndexOf("\n## Non-negotiable\n", StringComparison.Ordinal);

                    Assert.True(stub.EndsWith($"\n\n{body}", StringComparison.Ordinal), $"{where}: the stub does not end with the base body");
                    Assert.True(heading > 0 && heading < stub.Length - body.Length, $"{where}: ## Non-negotiable must precede the body");
                    Assert.True(Count(stub, "\n## Non-negotiable\n") == 1, $"{where}: ## Non-negotiable appears more than once");
                    Assert.False(stub.Contains("{{", StringComparison.Ordinal), $"{where}: an unrendered token is left");
                }
            }
        }
    }

    // ui-reviewer lists only claude and has no default environment part, so its base body does not render for opencode,
    // and a stub that embeds that body inherits the failure instead of writing a half-empty file.
    [Fact]
    public void AStubForAHarnessTheRoleCannotRenderForIsARenderErrorNotAnEmptyBody()
    {
        RoleRenderer renderer = new(library);

        RoleRenderException ex = Assert.Throws<RoleRenderException>(() => renderer.RenderTierStub("ui-reviewer", "xhigh", "opencode", cwd));

        Assert.Contains("ui-reviewer", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ALocalRoleMdOverrideReachesTheStubBodyAndTheMarkerSaysSo()
    {
        string roleDir = Path.Combine(cwd, ".claustrum", "roles", "builder");
        Directory.CreateDirectory(roleDir);
        File.WriteAllText(Path.Combine(roleDir, "ROLE.md"), "You are the LOCAL builder, with locally agreed instructions.\n");

        string content = SyncedFile("claude", "builder", "builder-xhigh");

        Assert.Contains("You are the LOCAL builder, with locally agreed instructions.", content, StringComparison.Ordinal);
        Assert.DoesNotContain("You are the **builder**. You turn a design", content, StringComparison.Ordinal);
        Assert.Contains(" source=local sha256=", content, StringComparison.Ordinal);
    }

    // A repo synced before #82 holds thin, marked stubs: the next plain `sync` upgrades them, no `--force` needed.
    [Fact]
    public void AThinStubFromBeforeTheChangeIsRewrittenByAPlainSyncAndStaysStableAfterwards()
    {
        string path = HarnessSyncs.AgentFile("claude", HarnessSyncs.RepoRoot("claude", cwd), "builder-xhigh");
        Directory.CreateDirectory(HarnessSyncs.AgentDirectory("claude", HarnessSyncs.RepoRoot("claude", cwd)));
        File.WriteAllText(path, $"---\nname: builder-xhigh\n---\n{SyncWriter.MarkerPrefix} role=builder harness=claude library=1.0.0 sha256=0000 -->\n\nbuilder at xhigh reasoning effort.\n\n## Non-negotiable\n\n- old\n");

        SyncResult first = HarnessSyncs.Run("claude", library, fakeHome, cwd, ["builder"]);
        SyncResult second = HarnessSyncs.Run("claude", library, fakeHome, cwd, ["builder"]);

        Assert.Contains(path, first.Written);
        Assert.Empty(first.Foreign);
        Assert.Contains("## Report format", File.ReadAllText(path), StringComparison.Ordinal);
        Assert.Contains(path, second.Skipped);
        Assert.Empty(second.Written);
    }
}
