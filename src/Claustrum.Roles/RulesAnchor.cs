namespace Claustrum.Roles;

/// <summary>
/// Where an isolated run's worktree must be: <paramref name="JobId"/>'s directory under <paramref name="MainCheckout"/>'s
/// own `.claustrum/worktrees`, links inside the repository followed. The api rules are inlined only from there.
/// </summary>
public sealed record RulesAnchor(string MainCheckout, string JobId);
