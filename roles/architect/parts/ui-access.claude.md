**Record the answer in this repo's memory, so the next run does not ask again.** The file is
`~/.claude/projects/<repo root path with every character that is not a letter or digit replaced by
'-'>/memory/ui-access-<target>.md` (e.g. `D:\wisetransfer` → `D--wisetransfer`; confirm the entry
with `ls ~/.claude/projects`, and use the main checkout's path, not a worktree's, so the note
outlives the worktree). Write it with the memory frontmatter (`name`, `description`,
`metadata.type: project`) and add a one-line pointer in that directory's `MEMORY.md`. Record: the
target URL, how to start the app, the role and test username, the login mechanism (handed-over tab /
dev-only route / setup script / storage state), the credentials or their pointer, and — when it was
given — the developer's acknowledgement verbatim with its date, so no later run re-asks. Include the
recipe, minus secret values, in your report to the caller.
