using Claustrum.Core.Backends;
using Claustrum.Core.Backends.Acp;

namespace Claustrum.Core.Tests.Backends.Acp;

/// <summary>
/// An <see cref="IAcpHarness"/> whose session totals are scripted: the k-th read answers with the k-th total (the last repeats), and every read is
/// kept. It stands where a second harness would, so the generic client is tried against a row that is not opencode's.
/// </summary>
internal sealed class FakeAcpHarness(string name = "fake", params AcpSessionTotal?[] totals) : IAcpHarness
{
    public string Name => name;

    public string Exe => "fake-agent";

    public string[] Args { get; } = ["serve", "--stdio"];

    public string[] VersionArgs { get; } = ["version"];

    public bool CostIsRunningTotal { get; init; } = true;

    public bool LoadsSessions { get; init; } = true;

    /// <summary>The session id, context and token of every <see cref="SessionTotalAsync"/> call, in order.</summary>
    public List<(string SessionId, PostRunContext Context, CancellationToken Token)> Reads { get; } = [];

    public AcpHarnessSetup Prepare(ResolvedRun run) =>
        new(new Dictionary<string, string> { ["FAKE_AGENT_ROLE"] = run.Role.Name }, [("model", run.Role.Model), ("mode", run.Role.Name)], Warnings: ["fake: a warning of the setup"]);

    public Task<AcpSessionTotal?> SessionTotalAsync(string sessionId, PostRunContext context, CancellationToken cancellationToken)
    {
        Reads.Add((sessionId, context, cancellationToken));
        AcpSessionTotal? answer = totals.Length == 0 ? null : totals[Math.Min(Reads.Count, totals.Length) - 1];
        return Task.FromResult(answer);
    }
}
