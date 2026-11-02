using Claustrum.Core.Model;
using Claustrum.Core.Tests.Backends.Acp;
using Claustrum.Core.Tests.Testing;

namespace Claustrum.Core.Tests.Tracing;

// #145 against the transcripts already recorded under tests/fixtures (opencode 2.0.x, claude 2.1.x): the recordings of healthy runs must classify as clean
// whatever tool calls they hold (a false stop is dearer than a missed issue), and the one recording with a refused call counts exactly when the
// role's deny list does not name the command.
public sealed class EnvironmentClassifierRecordedFixturesTests
{
    private static string Fixture(string directory, string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", directory, name));

    [Theory]
    [InlineData("opencode-success.jsonl")]
    [InlineData("opencode-three-turns.jsonl")]
    [InlineData("opencode-load-replay.jsonl")]
    [InlineData("opencode-cancelled.jsonl")]
    [InlineData("opencode-cut-off.jsonl")]
    [InlineData("opencode-effort-refused.jsonl")]
    [InlineData("opencode-provider-error.jsonl")]
    public void ARecordedAcpRunWithoutARefusedCallHasNoIssues(string name)
    {
        Assert.Empty(RunLogs.ClassifyAcp(AcpLines.Fixture(name), PermissionLevel.EditShell, cwd: "/repo"));
    }

    [Theory]
    [InlineData("stream-tools.jsonl")]
    [InlineData("success.jsonl")]
    [InlineData("success.json")]
    [InlineData("error.txt")]
    [InlineData("error-max-budget.json")]
    public void ARecordedClaudeRunHasNoIssues(string name)
    {
        Assert.Empty(RunLogs.ClassifyClaude(Fixture("claude", name), PermissionLevel.EditShell, cwd: "/repo"));
    }

    [Theory]
    [InlineData("success.jsonl")]
    [InlineData("success-no-final-step-finish.jsonl")]
    [InlineData("error.jsonl")]
    [InlineData("error-v1.jsonl")]
    [InlineData("quota-402.jsonl")]
    public void ARecordedNativeOpencodeRunHasNoIssues(string name)
    {
        Assert.Empty(RunLogs.ClassifyOpencode(Fixture("opencode", name), PermissionLevel.EditShell, cwd: "/repo"));
    }

    // opencode-report.jsonl:34, a builder under the deny config: `git push origin HEAD` answered `Permission denied: shell`. With `git push` on the role's
    // deny list that is the policy working; without it, the environment refused the shell.
    [Fact]
    public void TheRecordedGitPushRefusalCountsOnlyWhenTheDenyListDoesNotNameIt()
    {
        string log = AcpLines.Fixture("opencode-report.jsonl");

        Assert.Empty(RunLogs.ClassifyAcp(log, PermissionLevel.EditShell, cwd: "/repo", deny: ["git push"]));
        EnvironmentIssue issue = Assert.Single(RunLogs.ClassifyAcp(log, PermissionLevel.EditShell, cwd: "/repo"));
        Assert.Equal(EnvironmentIssueKind.PermissionDenied, issue.Kind);
        Assert.Equal("Permission denied: shell", issue.What);
        Assert.StartsWith("git push origin HEAD", issue.Where, StringComparison.Ordinal);
        Assert.Equal("stdout.log:34", issue.First);
    }
}
