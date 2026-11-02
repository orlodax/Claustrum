using Claustrum.Core.Model;
using Claustrum.Core.Report;
using Claustrum.Core.Tests.Testing;
using Claustrum.Core.Tracing;

namespace Claustrum.Core.Tests.Tracing;

// #145: what a role says in its own report (`failed`, `blocked_by`, `status: blocked`) becomes environment issues with `first: report`. A red gate
// it reports itself is the only test-gate signal: test-runner output in a log is never parsed. NOTES.md "Environment issues".
public sealed class EnvironmentClassifierReportTests
{
    private static EnvironmentIssue[] Classify(string reportJson, RunTrace? trace = null) =>
        EnvironmentClassifier.Classify(trace, new PermissionPolicy(PermissionLevel.EditShell, []), ReportExtractor.Extract($"done\n\n```claustrum-report\n{reportJson}\n```").Report, RunLogs.Cwd, null);

    // ---- failed (the tester's list) --------------------------------------------------------------------------------------------------------

    [Fact]
    public void FailedCountsEveryEntryWhoseFaultInIsNotTestAndTheEntriesTheRoleFixedItselfAreNot()
    {
        EnvironmentIssue issue = Assert.Single(Classify(
                                 /*lang=json,strict*/
                                 """{"status":"done","failed":[{"test":"t2"},{"test":"t3","fault_in":"code"},"t4",{"test":"t5","fault_in":"Test"},{"test":"t6","fault_in":"test | code"},{"name":"t7"}]}"""));

        Assert.Equal(EnvironmentIssueKind.TestGateRed, issue.Kind);
        Assert.Equal("the report lists failed tests", issue.What);
        Assert.Equal("t2, t3, t4, t6, t7", issue.Where);
        Assert.Equal(5, issue.Count);
        Assert.Equal("report", issue.First);
    }

    [Fact]
    public void AFailedListOfOnlyTestsTheTesterFoundWrongAndFixedIsNotRed()
    {
        Assert.Empty(Classify(/*lang=json,strict*/ """{"status":"done","passed":10,"failed":[{"test":"T1","root_cause":"x","fault_in":"test"}]}"""));
        Assert.Empty(Classify(/*lang=json,strict*/ """{"status":"done","failed":[]}"""));
    }

    [Theory]
    [InlineData("3", 3)]
    [InlineData("1", 1)]
    [InlineData("2.5", 2)]
    public void FailedAsANumberAboveZeroCountsThatMany(string failed, int count)
    {
        EnvironmentIssue issue = Assert.Single(Classify($$"""{"status":"done","failed":{{failed}}}"""));

        Assert.Equal(EnvironmentIssueKind.TestGateRed, issue.Kind);
        Assert.Equal(count, issue.Count);
        Assert.Null(issue.Where);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("\"3\"")]
    [InlineData("null")]
    [InlineData("true")]
    public void FailedThatIsZeroNegativeOrNotANumberOrAListIsNotRed(string failed)
    {
        Assert.Empty(Classify($$"""{"status":"done","failed":{{failed}}}"""));
    }

    // ---- blocked_by -------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void BlockedByTakesStringsAndWhatWhereObjectsInOrderAndCountsEach()
    {
        EnvironmentIssue[] issues = Classify(
                                 /*lang=json,strict*/
                                 """{"status":"partial","blocked_by":[{"what":"gh not logged in","where":"gh issue view 12"},{"what":"gh not logged in","where":"gh pr view 3"},"the VPN to the customer DB was down"]}""");

        Assert.Equal(2, issues.Length);
        Assert.Equal(EnvironmentIssueKind.ReportedBlocker, issues[0].Kind);
        Assert.Equal("gh not logged in", issues[0].What);
        Assert.Equal("gh issue view 12, gh pr view 3", issues[0].Where);
        Assert.Equal(2, issues[0].Count);
        Assert.Equal("report", issues[0].First);
        Assert.Equal("the VPN to the customer DB was down", issues[1].What);
        Assert.Null(issues[1].Where);
    }

    [Fact]
    public void BlockedByAsASingleObjectOrStringIsReadToo()
    {
        Assert.Single(Classify(/*lang=json,strict*/ """{"status":"done","blocked_by":{"what":"no access to /srv/shared"}}"""));
        Assert.Single(Classify(/*lang=json,strict*/ """{"status":"done","blocked_by":"no access to /srv/shared"}"""));
    }

    // Exact phrases only (item 7): a role filling in the shape is not blocked, a sentence that starts with one of the words is.
    [Theory]
    [InlineData("None")]
    [InlineData("none.")]
    [InlineData("  None  ")]
    [InlineData("n/a")]
    [InlineData("N/A")]
    [InlineData("na")]
    [InlineData("nothing")]
    [InlineData("-")]
    [InlineData("—")]
    [InlineData("No blockers")]
    [InlineData("no blockers.")]
    [InlineData("No blocker")]
    [InlineData("Nothing blocked me")]
    [InlineData("No blocking issues")]
    [InlineData("no blocking issues.")]
    [InlineData("")]
    [InlineData("   ")]
    public void ABlockedByEntryThatSaysNothingIsBlockingIsSkipped(string entry)
    {
        Assert.Empty(Classify($$"""{"status":"done","blocked_by":[{{System.Text.Json.JsonSerializer.Serialize(entry)}}]}"""));
        Assert.Empty(Classify($$"""{"status":"done","blocked_by":[{"what":{{System.Text.Json.JsonSerializer.Serialize(entry)}},"where":""}]}"""));
    }

    [Theory]
    [InlineData("No access to /x")]
    [InlineData("No blockers found in the code, but the gate could not run")]
    [InlineData("None of the tests ran")]
    [InlineData("Nothing blocked me until the docker socket refused")]
    [InlineData("No blocking issues were reported by CI, which was down")]
    [InlineData("no network")]
    public void ABlockedByEntryThatMerelyStartsLikeAPhraseIsABlocker(string entry)
    {
        EnvironmentIssue issue = Assert.Single(Classify($$"""{"status":"done","blocked_by":[{{System.Text.Json.JsonSerializer.Serialize(entry)}}]}"""));

        Assert.Equal(entry, issue.What);
    }

    [Fact]
    public void ARealBlockerAmongNoneishEntriesIsTheOnlyIssue()
    {
        EnvironmentIssue issue = Assert.Single(Classify(
                                 /*lang=json,strict*/
                                 """{"status":"done","blocked_by":["None",{"what":"n/a"},{"what":"gh not logged in","where":"gh issue view 12"},"-"]}"""));

        Assert.Equal("gh not logged in", issue.What);
    }

    // ---- status: blocked alone -----------------------------------------------------------------------------------------------------------

    [Fact]
    public void StatusBlockedAloneIsOneIssueAndAnyBlockedByReplacesIt()
    {
        EnvironmentIssue alone = Assert.Single(Classify(/*lang=json,strict*/ """{"status":"blocked"}"""));
        Assert.Equal(EnvironmentIssueKind.ReportedBlocker, alone.Kind);
        Assert.Equal("the report says \"status\": \"blocked\"", alone.What);
        Assert.Equal("report", alone.First);

        EnvironmentIssue named = Assert.Single(Classify(/*lang=json,strict*/ """{"status":"blocked","blocked_by":["the socket is root-only"]}"""));
        Assert.Equal("the socket is root-only", named.What);

        // A "None" blocked_by says nothing, so the bare status still stands.
        EnvironmentIssue unnamed = Assert.Single(Classify(/*lang=json,strict*/ """{"status":"Blocked","blocked_by":["None"]}"""));
        Assert.Equal("the report says \"status\": \"blocked\"", unnamed.What);
    }

    [Fact]
    public void ABlockedStatusWithOnlyTestsTheTesterFixedIsStillTheBareBlockerAndNoRedGate()
    {
        EnvironmentIssue issue = Assert.Single(Classify(/*lang=json,strict*/ """{"status":"blocked","failed":[{"test":"T1","fault_in":"test"}]}"""));

        Assert.Equal(EnvironmentIssueKind.ReportedBlocker, issue.Kind);
    }

    [Fact]
    public void ARedGateAndABlockedStatusAreBothReported()
    {
        EnvironmentIssue[] issues = Classify(/*lang=json,strict*/ """{"status":"blocked","failed":[{"test":"T1","fault_in":"code"}]}""");

        Assert.Equal([EnvironmentIssueKind.TestGateRed, EnvironmentIssueKind.ReportedBlocker], issues.Select(issue => issue.Kind));
    }

    [Theory]
    [InlineData(/*lang=json,strict*/ """{"status":"done"}""")]
    [InlineData(/*lang=json,strict*/ """{"status":"done","blocked_by":[]}""")]
    [InlineData(/*lang=json,strict*/ """{"status":"needs_input","questions":[{"prompt":"which?"}]}""")]
    [InlineData("[1,2,3]")]
    [InlineData("\"blocked\"")]
    public void AReportThatSaysNothingBlockingHasNoIssues(string report)
    {
        Assert.Empty(Classify(report));
    }

    [Fact]
    public void AReportThatDidNotParseOrIsMissingAddsNothing()
    {
        Assert.Empty(EnvironmentClassifier.Classify(null, new PermissionPolicy(PermissionLevel.EditShell, []), ReportExtractor.Extract("```claustrum-report\n{not json\n```").Report, RunLogs.Cwd, null));
        Assert.Empty(EnvironmentClassifier.Classify(null, new PermissionPolicy(PermissionLevel.EditShell, []), null, RunLogs.Cwd, null));
    }

    // ---- grouping and order --------------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheTraceIssuesComeBeforeTheReportOnesAndEachGroupKeepsItsFirstSightingFirst()
    {
        RunTrace trace = new(
        [
            new TracedCall("shell", ToolFamily.Shell, "docker compose up", RunLogs.Cwd, null, false, null, "PermissionError: [Errno 13] Permission denied: '/a'", 40),
            new TracedCall("shell", ToolFamily.Shell, "docker compose up", RunLogs.Cwd, null, false, null, "PermissionError: [Errno 13] Permission denied: '/b'", 12),
        ],
        []);

        EnvironmentIssue[] issues = Classify(/*lang=json,strict*/ """{"status":"done","blocked_by":["no network"]}""", trace);

        Assert.Equal([EnvironmentIssueKind.ContainerPermission, EnvironmentIssueKind.ReportedBlocker], issues.Select(issue => issue.Kind));
        Assert.Equal("/b, /a", issues[0].Where);
        Assert.Equal("stdout.log:12", issues[0].First);
        Assert.Equal("report", issues[1].First);
    }

    [Fact]
    public void ANullTraceIsFineAndTheReportStillCounts()
    {
        Assert.Single(Classify(/*lang=json,strict*/ """{"status":"blocked"}"""));
    }

    [Fact]
    public void ALongBlockerIsCutToOneLineOfAtMost120Characters()
    {
        string said = new('x', 300);

        EnvironmentIssue issue = Assert.Single(Classify($$"""{"status":"done","blocked_by":["{{said}}\nsecond line"]}"""));

        Assert.Equal(120, issue.What.Length);
        Assert.EndsWith("…", issue.What, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', issue.What);
    }
}
