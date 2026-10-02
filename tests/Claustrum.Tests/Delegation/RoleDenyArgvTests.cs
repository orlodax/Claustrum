using System.Text.Json;
using Claustrum.Core.Backends;
using Claustrum.Core.Backends.Claude;
using Claustrum.Core.Backends.Copilot;
using Claustrum.Core.Backends.Cursor;
using Claustrum.Core.Backends.Opencode;
using Claustrum.Core.Config;
using Claustrum.Core.Model;
using Claustrum.Roles;
using Claustrum.Tests.Testing;

namespace Claustrum.Tests.Delegation;

// What the shipped roles' deny lists become on each backend (#43, PR #42): the demo-author moved to
// `edit+shell` with 24 git verbs denied — a deck-writer that must never move git state — and every
// verb is now a word, not a prefix, so `git merge-base` stays open while `git merge` and a bare `git
// merge` are both stopped. The backends' own tests pin the shapes with small lists; these run the real
// role.json through render, config resolution and Build, so a change to the list or to the level shows
// where it lands. The roles are rendered for claude and re-pointed at each backend with `with`: the
// demo-author cannot be rendered for another harness, which is the point of the harness check.
public sealed class RoleDenyArgvTests : IDisposable
{
    private static readonly string[] demoAuthorDeny =
    [
        "git push", "git commit", "git commit-tree", "git add", "git stage", "git rm", "git mv",
        "git apply", "git update-index", "git checkout", "git switch", "git restore", "git reset",
        "git stash", "git clean", "git merge", "git rebase", "git cherry-pick", "git revert",
        "git pull", "git am", "git read-tree", "git update-ref", "git symbolic-ref",
    ];

    private readonly string directory = Directory.CreateTempSubdirectory("claustrum-role-deny-").FullName;
    private readonly RoleRenderer renderer = new(new RoleLibrary());

    public void Dispose() => Directory.Delete(directory, recursive: true);

    private static Config BuiltInAliases() => new()
    {
        Merged = new ConfigDocument(
            Models: new Dictionary<string, string>
            {
                ["frontier-reasoning"] = "claude:opus",
                ["frontier-coding"] = "claude:opus",
                ["standard-coding"] = "claude:sonnet",
                ["cheap-coding"] = "claude:haiku",
                ["fast"] = "claude:haiku",
            },
            Roles: [],
            Backends: [],
            Defaults: null,
            Jobs: null),
        Origins = [],
    };

    private ResolvedRole Resolve(string role, ConfigOverrides? overrides = null) =>
        BuiltInAliases().Resolve(renderer.Render(role, "high", "claude", directory), overrides ?? new ConfigOverrides());

    private ResolvedRun RunOn(ResolvedRole role, string backend)
    {
        string systemPromptPath = Path.Combine(directory, "system.md");
        File.WriteAllText(systemPromptPath, "you are the role");

        return new ResolvedRun(
            Role: role with { Backend = backend, Model = "some-model" },
            Brief: "do the thing",
            Cwd: directory,
            BudgetUsd: null,
            ResumeSession: null,
            AttachFiles: [],
            Stream: false,
            SystemPromptFilePath: systemPromptPath,
            JobDirectory: directory,
            Env: []);
    }

    private static string[] ExactAndWordForms(IEnumerable<string> deny) => [.. deny.SelectMany(entry => (string[])[entry, $"{entry} *"])];

    [Fact]
    public void TheDemoAuthorResolvesToEditShellWithItsTwentyFourDenyEntries()
    {
        ResolvedRole resolved = Resolve("demo-author");

        Assert.Equal(PermissionLevel.EditShell, resolved.Permission.Level);
        Assert.Equal(demoAuthorDeny, resolved.Permission.Deny);
        Assert.Equal(24, resolved.Permission.Deny.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void ClaudeRunsTheDemoAuthorInAcceptEditsModeWithOneDisallowedToolsValueOfFortyEightRules()
    {
        ProcessSpec spec = new ClaudeBackend(new FakePlatform()).Build(RunOn(Resolve("demo-author"), "claude"));

        Assert.Equal("acceptEdits", spec.Args[Array.IndexOf(spec.Args, "--permission-mode") + 1]);
        Assert.DoesNotContain("plan", spec.Args);
        Assert.Equal("Edit,Write,Read,Glob,Grep,Bash(*)", spec.Args[Array.IndexOf(spec.Args, "--allowedTools") + 1]);

        Assert.Single(spec.Args, arg => arg == "--disallowedTools");
        string[] rules = spec.Args[Array.IndexOf(spec.Args, "--disallowedTools") + 1].Split(',');
        Assert.Equal(48, rules.Length);
        Assert.Equal(ExactAndWordForms(demoAuthorDeny).Select(entry => $"Bash({entry})"), rules);
    }

    [Fact]
    public void OpencodeWritesFortyEightDenyKeysAfterTheAllowAllOnTheAgentAndAtTheTopLevel()
    {
        ProcessSpec spec = new OpencodeBackend(new FakePlatform()).Build(RunOn(Resolve("demo-author"), "opencode"));

        using JsonDocument config = JsonDocument.Parse(spec.Env["OPENCODE_CONFIG_CONTENT"]);
        JsonElement[] permissions =
        [
            config.RootElement.GetProperty("agent").GetProperty("claustrum-demo-author").GetProperty("permission"),
            config.RootElement.GetProperty("permission"),
        ];

        foreach (JsonElement permission in permissions)
        {
            Assert.Equal("allow", permission.GetProperty("edit").GetString());
            JsonProperty[] bash = [.. permission.GetProperty("bash").EnumerateObject()];

            Assert.Equal(49, bash.Length);
            Assert.Equal(("*", "allow"), (bash[0].Name, bash[0].Value.GetString()));
            Assert.Equal(ExactAndWordForms(demoAuthorDeny), bash.Skip(1).Select(property => property.Name));
            Assert.All(bash.Skip(1), property => Assert.Equal("deny", property.Value.GetString()));
        }
    }

    [Fact]
    public void CopilotDeniesEachOfTheTwentyFourVerbsAsAShellRule()
    {
        ProcessSpec spec = new CopilotBackend(new FakePlatform()).Build(RunOn(Resolve("demo-author"), "copilot"));

        string[] denied = [.. spec.Args.Select((arg, index) => (arg, index)).Where(pair => pair.arg == "--deny-tool").Select(pair => spec.Args[pair.index + 1])];
        Assert.Equal(demoAuthorDeny.Select(entry => $"shell({entry})"), denied);
        Assert.Contains("--allow-all-tools", spec.Args);
        Assert.Contains("--allow-all-paths", spec.Args);
    }

    [Fact]
    public void CursorTellsTheDemoAuthorTwentyFourTimesNeverToRunAVerb()
    {
        ProcessSpec spec = new CursorBackend(new FakePlatform()).Build(RunOn(Resolve("demo-author"), "cursor"));

        string[] rules = [.. spec.StdinText!.Split('\n').Where(line => line.StartsWith("- Never run: ", StringComparison.Ordinal))];
        Assert.Equal(demoAuthorDeny.Select(entry => $"- Never run: {entry}"), rules);
    }

    // The builder's `git push` is the one entry its role.json carries; the word form is what claude gets.
    [Fact]
    public void TheBuilderGetsTheExactAndTheWordFormOfGitPushOnClaude()
    {
        ResolvedRole resolved = Resolve("builder");

        ProcessSpec spec = new ClaudeBackend(new FakePlatform()).Build(RunOn(resolved, "claude"));

        Assert.Equal(["git push"], resolved.Permission.Deny);
        Assert.Contains("Bash(git push),Bash(git push *)", spec.Args);
        Assert.DoesNotContain("Bash(git push*)", spec.Args);
    }

    // A user's own `--deny`/claustrum.json entry is appended to the role's, and one that ends in `*`
    // keeps it: the exact rule `Bash(git push*)` and the word rule `Bash(git push* *)` are both built.
    [Fact]
    public void AUserDenyEntryEndingInAStarYieldsBothFormsBesideTheRolesOwn()
    {
        ResolvedRole resolved = Resolve("builder", new ConfigOverrides(Deny: ["git push*"]));

        ProcessSpec spec = new ClaudeBackend(new FakePlatform()).Build(RunOn(resolved, "claude"));

        Assert.Equal(["git push", "git push*"], resolved.Permission.Deny);
        Assert.Contains("Bash(git push),Bash(git push *),Bash(git push*),Bash(git push* *)", spec.Args);
    }

    [Fact]
    public void TheUiReviewerKeepsPlanModeAndGetsTheWordFormAtTheShellRung()
    {
        ProcessSpec spec = new ClaudeBackend(new FakePlatform()).Build(RunOn(Resolve("ui-reviewer"), "claude"));

        Assert.Equal("plan", spec.Args[Array.IndexOf(spec.Args, "--permission-mode") + 1]);
        Assert.Contains("Edit,Write,NotebookEdit", spec.Args);
        Assert.Contains("Bash(git push),Bash(git push *)", spec.Args);
    }
}
