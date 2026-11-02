using System.Text.Json;
using System.Text.RegularExpressions;
using Claustrum.Core.Backends;
using Claustrum.Core.Backends.Opencode;
using Claustrum.Core.Model;
using Claustrum.Core.Tests.Testing;

namespace Claustrum.Core.Tests.Backends.Opencode;

// #143: the readonly `bash` object only works by opencode's matching rules, so this reads the generated rules the way opencode does and asks, command by
// command, what would run. The matcher is the one the design leans on (NOTES.md "opencode readonly gets a read-only git shell", measured on 2.0.30): the
// LAST matching rule wins; `*` matches anything, `/` included; `?` one character; the whole text must match; and `\` is turned into `/` on both the
// pattern and the command before matching, which is why `*\*` refuses every `/` too. A compound command is judged part by part by opencode itself, so
// each case here is one simple command. This does not prove opencode behaves so: it proves the generated rules mean what MANUAL says they mean if it does.
public sealed class OpencodeReadOnlyShellMatchTests
{
    private readonly OpencodeBackend backend = new(new FakePlatform());

    private static bool Matches(string pattern, string command)
    {
        string normalizedPattern = pattern.Replace('\\', '/');
        string normalizedCommand = command.Replace('\\', '/');
        string expression = "^" + Regex.Escape(normalizedPattern).Replace("\\*", ".*", StringComparison.Ordinal).Replace("\\?", ".", StringComparison.Ordinal) + "$";
        return Regex.IsMatch(normalizedCommand, expression, RegexOptions.Singleline | RegexOptions.CultureInvariant);
    }

    private string Verdict(string command, params string[] deny)
    {
        ResolvedRun run = new(
            Role: new ResolvedRole("code-reviewer", "system body", "opencode", "openrouter/deepseek/deepseek-v4-pro", "high", new PermissionPolicy(PermissionLevel.ReadOnly, deny), Blind: true, HasReport: true),
            Brief: "review",
            Cwd: "/repo",
            BudgetUsd: null,
            ResumeSession: null,
            AttachFiles: [],
            Stream: false,
            SystemPromptFilePath: "/job/system.md",
            JobDirectory: "/job",
            Env: []);
        using JsonDocument config = JsonDocument.Parse(backend.Build(run).Env["OPENCODE_CONFIG_CONTENT"]);
        string verdict = "deny";
        foreach (JsonProperty rule in config.RootElement.GetProperty("permission").GetProperty("bash").EnumerateObject())
        {
            if (Matches(rule.Name, command))
                verdict = rule.Value.GetString() ?? "deny";
        }

        return verdict;
    }

    [Theory]
    [InlineData("git diff")]
    [InlineData("git diff HEAD~3")]
    [InlineData("git diff --stat HEAD~1")]
    [InlineData("git log")]
    [InlineData("git log --oneline -20")]
    [InlineData("git show abc1234")]
    [InlineData("git status")]
    [InlineData("git status --short")]
    [InlineData("git rev-parse HEAD")]
    [InlineData("gh pr view 12")]
    [InlineData("gh pr diff 12")]
    [InlineData("gh pr list")]
    [InlineData("gh pr list --state open")]
    [InlineData("gh pr checks 12")]
    [InlineData("gh issue view 231 --comments")]
    [InlineData("gh issue list")]
    [InlineData("gh issue list -L 5")]
    public void TheReadVerbsOfGitAndGhRun(string command) => Assert.Equal("allow", Verdict(command));

    // The cost MANUAL names: a path, a ref with a slash, `-R owner/repo` are refused with every other `/`.
    [Theory]
    [InlineData("git diff -- src/x.cs")]
    [InlineData("git log origin/main..HEAD")]
    [InlineData("gh issue view 1 -R owner/repo")]
    [InlineData("git show HEAD:src/x.cs")]
    public void ASlashAnywhereInAnAllowedCommandIsRefused(string command) => Assert.Equal("deny", Verdict(command));

    [Theory]
    [InlineData("git diff --output=x")]
    [InlineData("git log --output x")]
    [InlineData("git show --output=x HEAD")]
    [InlineData("git diff --no-index a b")]
    [InlineData("git log --ext-diff")]
    [InlineData("git diff --ext-diff")]
    [InlineData("git diff > x")]
    [InlineData("git diff >x")]
    [InlineData("git status < x")]
    [InlineData("git diff \"x\"")]
    [InlineData("git diff 'x'")]
    [InlineData("git diff `id`")]
    [InlineData("git diff $HOME")]
    [InlineData("git diff $(id)")]
    [InlineData("git diff {a,b}")]
    [InlineData("git diff --ou\\tput=x")]
    [InlineData("git diff --outpu{t,x}=x")]
    public void WhateverMakesTheRawTextDifferFromWhatBashRunsIsRefused(string command) => Assert.Equal("deny", Verdict(command));

    [Theory]
    [InlineData("git difftool")]
    [InlineData("git difftool -x id")]
    [InlineData("git logfoo")]
    [InlineData("git push")]
    [InlineData("git commit -m x")]
    [InlineData("git -c core.pager=id diff")]
    [InlineData("git -C /tmp diff")]
    [InlineData("git checkout x")]
    [InlineData("gh pr merge 1")]
    [InlineData("gh pr close 1")]
    [InlineData("gh pr checkout 1")]
    [InlineData("gh pr create")]
    [InlineData("gh issue create")]
    [InlineData("gh issue close 1")]
    [InlineData("gh api repos/x/y")]
    [InlineData("cat file")]
    [InlineData("rm -rf x")]
    [InlineData("python3 -c 1")]
    [InlineData("head -5")]
    public void EverythingElseIsDeniedByTheFirstRule(string command) => Assert.Equal("deny", Verdict(command));

    // Space-anchored verbs: `git diff*` once let `git difftool -x <cmd>` through, and `gh pr *` merged and closed.
    [Fact]
    public void NoAllowPatternIsAPrefixThatSwallowsASiblingVerb()
    {
        using JsonDocument config = JsonDocument.Parse(backend.Build(new ResolvedRun(
            new ResolvedRole("code-reviewer", "s", "opencode", "m/m", "high", new PermissionPolicy(PermissionLevel.ReadOnly, []), Blind: true, HasReport: true),
            "b", "/repo", null, null, [], false, "/job/system.md", "/job", [])).Env["OPENCODE_CONFIG_CONTENT"]);

        string[] allows = [.. config.RootElement.GetProperty("permission").GetProperty("bash").EnumerateObject().Where(rule => rule.Value.GetString() == "allow").Select(rule => rule.Name)];

        Assert.Equal(17, allows.Length);
        Assert.DoesNotContain(allows, allow => allow is "git diff*" or "git log*" or "git show*" or "git status*" or "gh pr *" or "gh issue *");
        Assert.All(allows, allow => Assert.True(!allow.Contains('*', StringComparison.Ordinal) || allow.EndsWith(" *", StringComparison.Ordinal), allow));
    }

    // The role's own deny list comes after the guards, so a verb a role forbids is refused even where an allow would admit it.
    [Theory]
    [InlineData("gh pr view 12", "gh pr view")]
    [InlineData("git log --oneline", "git log")]
    [InlineData("git status", "git status")]
    public void ARolesDenyEntryWinsOverAnAllowItNamesInItsExactAndItsWordForm(string command, string deny)
    {
        Assert.Equal("allow", Verdict(command));
        Assert.Equal("deny", Verdict(command, deny));
    }
}
