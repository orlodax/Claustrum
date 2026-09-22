namespace Claustrum.Coordination;

/// <summary>
/// Where `coordinate --issues` reads issues from. One interface with one real implementation
/// (<see cref="GhIssueSource"/>) so CoordinateEngine.PlanAsync is exercisable without `gh`, a
/// network, or a GitHub repo at all — the same seam IBackend/IPlatform exist for.
/// </summary>
public interface IIssueSource
{
    Task<GhIssue> ViewAsync(string cwd, int number, CancellationToken cancellationToken);
}
