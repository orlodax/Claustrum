**Record the answer where the next run will find it, so it does not ask again** — the agent-notes
file this repo keeps for that purpose if it has one *and* that file is untracked (check
`.gitignore`), otherwise your harness's own per-project memory, outside the repository tree.
**Never in a tracked file**, and never with a secret value in it unless the developer gave the
acknowledgement above — that value lives outside the tree, with them. Record: the target URL, how
to start the app, the role and test username, the login mechanism (handed-over tab / dev-only route
/ setup script / storage state), the credentials or their pointer, and the acknowledgement verbatim
with its date when there is one. Include the recipe, minus secret values, in your report to the
caller.
