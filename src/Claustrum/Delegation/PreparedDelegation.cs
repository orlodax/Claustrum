using Claustrum.Core;
using Claustrum.Core.Model;

namespace Claustrum.Delegation;

// Everything DelegateEngine.Prepare resolves — and can refuse — with no job directory on disk: the
// config layers, the rendered role with its appendix, the backend a model alias names, the job tree
// and the three per-run numbers (issue #23). The original request rides along because the run still
// needs its brief, env and attachments. Only the job id is missing, and the one caller that must
// write it down before it exists (`coordinate`) leaves DelegateRequest.JobIdToken in its place.
public sealed record PreparedDelegation(
    DelegateRequest Request,
    ResolvedRole Role,
    RunOptions Options,
    decimal? BudgetUsd,
    int TimeoutSeconds,
    PermissionPolicy? Permission)
{
    /// <summary>
    /// This delegation bound to the job that will run it: every <see cref="DelegateRequest.JobIdToken"/>
    /// in the system prompt (`coordinate`'s `## Coordination` appendix) and in the request env (the
    /// tree id, which *is* the job id) becomes the real id. A plain replace of an exact token at the
    /// last possible moment — deliberately not a template engine, and a no-op for every other caller.
    /// </summary>
    public PreparedDelegation ForJob(string jobId) => this with
    {
        Role = Role with { SystemPrompt = Fill(Role.SystemPrompt, jobId) },
        Request = Request with { Env = Request.Env.ToDictionary(entry => entry.Key, entry => Fill(entry.Value, jobId), StringComparer.Ordinal) },
    };

    private static string Fill(string text, string jobId) => text.Replace(DelegateRequest.JobIdToken, jobId, StringComparison.Ordinal);
}
