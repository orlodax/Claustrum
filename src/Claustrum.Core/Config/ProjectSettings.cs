namespace Claustrum.Core.Config;

// The "projects.<glob>" section of the user config (#144, #146), matched on the main checkout's name (ProjectAccess.Resolve).
// ReadDirs adds to the global read_dirs; WorktreeHook is an argv run after a job worktree is cut, `{worktree}` its path.
public sealed record ProjectSettings(string[]? ReadDirs = null, string[]? WorktreeHook = null);
