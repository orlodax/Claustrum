namespace Claustrum.Core.Backends.Acp;

// One row of the ACP table (#130): what differs between harnesses the generic client drives. The protocol, the turn parsing, the
// stream format and the live wiring are AcpBackend's; each harness keeps its own role-prompt mechanism and its own native deny config.
public interface IAcpHarness
{
    /// <summary>The harness family (<see cref="IBackend.Harness"/>): <c>opencode</c>. The backend is <c>acp-&lt;Name&gt;</c>.</summary>
    string Name { get; }

    string Exe { get; }

    string[] Args { get; }

    string[] VersionArgs { get; }

    /// <summary>Whether <c>usage_update.cost</c> is the session's running total (opencode, measured 2026-10-16) rather than a per-turn figure.</summary>
    bool CostIsRunningTotal { get; }

    /// <summary>Whether the agent can <c>session/load</c> a session an earlier run left (<c>agentCapabilities.loadSession</c>): <see cref="IBackend.CanResume"/>.</summary>
    bool LoadsSessions { get; }

    /// <summary>The role's environment and config options for a run. Pure and repeatable: Build and CreateDriver both call it.</summary>
    AcpHarnessSetup Prepare(ResolvedRun run);

    /// <summary>The session's running total read outside the stream: what a run is charged, and a resumed run's baseline. Null when the harness has no way to read it.</summary>
    Task<AcpSessionTotal?> SessionTotalAsync(string sessionId, PostRunContext context, CancellationToken cancellationToken);
}

// Env goes over the run's own (it wins, as the native backends' does); ConfigOptions are set in order after the session opens.
public sealed record AcpHarnessSetup(Dictionary<string, string> Env, (string ConfigId, string Value)[] ConfigOptions, string[]? Warnings = null);

// Failure says why the total could not be read; the cost is null then.
public sealed record AcpSessionTotal(decimal? CostUsd, string? Failure);
