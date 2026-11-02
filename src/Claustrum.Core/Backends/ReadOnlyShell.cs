namespace Claustrum.Core.Backends;

// #143: what a `readonly` role may run, one list for every backend that allow-lists a shell (claude's `Bash(…)`, opencode's `bash`
// patterns). Space-anchored verbs, never `git diff*`, which also let `git difftool -x <cmd>` through; gh's read verbs only, since
// `gh pr *` merged, closed and checked out (the write verbs' history is #148). `git rev-parse` finds a linked worktree's metadata.
internal static class ReadOnlyShell
{
    public static readonly string[] Commands =
    [
        "git diff", "git diff *", "git log", "git log *", "git show", "git show *", "git status", "git status *", "git rev-parse *",
        "gh pr view *", "gh pr diff *", "gh pr list", "gh pr list *", "gh pr checks *", "gh issue view *", "gh issue list", "gh issue list *",
    ];

    // opencode only, denied after the allows (its last match wins). Each command is matched on its raw text, redirect included, so
    // whatever makes that text differ from what bash runs is refused: quotes, `$`, backticks, a backslash (`--ou\tput`), a brace
    // (`--outpu{t,x}`), redirects. ⚠ opencode turns `\` into `/` on both sides before matching, so `*\*` also refuses every `/`:
    // a path, `origin/main`. `*` and `?` cannot be refused at all. NOTES.md "opencode readonly gets a read-only git shell".
    public static readonly string[] OpencodeGuards =
        ["*\"*", "*'*", "*`*", "*$*", "*\\*", "*{*", "*>*", "*<*", "*--output*", "*--no-index*", "*--ext-diff*"];
}
