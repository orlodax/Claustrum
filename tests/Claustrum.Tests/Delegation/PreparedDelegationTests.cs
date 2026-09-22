using Claustrum.Core;
using Claustrum.Core.Config;
using Claustrum.Core.Jobs;
using Claustrum.Core.Model;
using Claustrum.Delegation;

namespace Claustrum.Tests.Delegation;

// PreparedDelegation.ForJob is the last-moment substitution issue #23 needs: DelegateRequest.JobIdToken
// stands in for a job id that does not exist yet at Prepare() time, in both the rendered system prompt
// (coordinate's `## Coordination` appendix, already merged into ResolvedRole.SystemPrompt by the time a
// caller holds a PreparedDelegation) and in every request env value (CLAUSTRUM_PARENT_JOB, whose value
// *is* the tree id). Pure and dependency-free, like CoordinationBrief's own tests: no AppServices, no
// disk, just the record and a hand-built token-carrying body.
public sealed class PreparedDelegationTests
{
    private static PreparedDelegation MakePrepared(string systemPrompt, Dictionary<string, string> env) => new(
        Request: new DelegateRequest(
            Role: "architect",
            Brief: "do it",
            Cwd: "/repo",
            Tier: "xhigh",
            Overrides: new ConfigOverrides(),
            ResumeSession: null,
            AttachFiles: [],
            Env: env,
            Stream: false,
            DiffCapBytes: 64 * 1024),
        Role: new ResolvedRole("architect", systemPrompt, "claude", "opus", "high", new PermissionPolicy(PermissionLevel.Full, []), Blind: false, HasReport: true),
        Options: new RunOptions(DiffByteCapBytes: 64 * 1024),
        BudgetUsd: null,
        TimeoutSeconds: 1800,
        Permission: null);

    [Fact]
    public void ForJobSubstitutesTheTokenInTheSystemPrompt()
    {
        PreparedDelegation prepared = MakePrepared(
            $"## Coordination\n\nJob tree: {DelegateRequest.JobIdToken}\nInspect: claustrum jobs budget {DelegateRequest.JobIdToken}",
            env: []);

        PreparedDelegation bound = prepared.ForJob("job-123");

        Assert.Contains("Job tree: job-123", bound.Role.SystemPrompt, StringComparison.Ordinal);
        Assert.Contains("Inspect: claustrum jobs budget job-123", bound.Role.SystemPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public void ForJobSubstitutesTheTokenInEveryEnvValue()
    {
        PreparedDelegation prepared = MakePrepared(
            "plain system prompt",
            env: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [BudgetLedger.TreeVariable] = DelegateRequest.JobIdToken,
                ["CLAUSTRUM_HOME"] = "/fake/home",
            });

        PreparedDelegation bound = prepared.ForJob("job-123");

        Assert.Equal("job-123", bound.Request.Env[BudgetLedger.TreeVariable]);
        Assert.Equal("/fake/home", bound.Request.Env["CLAUSTRUM_HOME"]);
    }

    [Fact]
    public void ForJobLeavesNoTokenAnywhereAfterSubstitution()
    {
        PreparedDelegation prepared = MakePrepared(
            $"## Coordination\n\nJob tree: {DelegateRequest.JobIdToken}\nWork branch: claustrum/{DelegateRequest.JobIdToken}",
            env: new Dictionary<string, string>(StringComparer.Ordinal) { [BudgetLedger.TreeVariable] = DelegateRequest.JobIdToken });

        PreparedDelegation bound = prepared.ForJob("job-123");

        Assert.DoesNotContain(DelegateRequest.JobIdToken, bound.Role.SystemPrompt, StringComparison.Ordinal);
        Assert.All(bound.Request.Env.Values, value => Assert.DoesNotContain(DelegateRequest.JobIdToken, value, StringComparison.Ordinal));
    }

    // A caller that never carries the token (`run`/`delegate` with no coordinate appendix) must see
    // ForJob as a no-op on content, not merely "does not throw" — NOTES.md's own curiosity about a
    // hand-typed `--env FOO={{job_id}}` surviving verbatim on `run` depends on this staying a plain
    // string.Replace with nothing to replace, not a template engine that would reject it.
    [Fact]
    public void ForJobIsANoOpWhenNoTokenIsPresent()
    {
        PreparedDelegation prepared = MakePrepared(
            "plain system prompt, no coordination appendix",
            env: new Dictionary<string, string>(StringComparer.Ordinal) { ["FOO"] = "bar" });

        PreparedDelegation bound = prepared.ForJob("job-123");

        Assert.Equal("plain system prompt, no coordination appendix", bound.Role.SystemPrompt);
        Assert.Equal("bar", bound.Request.Env["FOO"]);
    }
}
