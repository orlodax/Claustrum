# Claustrum

## First time in this repo (or asked to set up/change a cast)
Run `claustrum cast questions --json` (or the MCP `cast_questions` tool) and ask the user
each question with your host's native question mechanism (e.g. `AskUserQuestion` in
Claude Code). Write the answers to a file keyed by each question's `key`, then
`claustrum cast create --answers <file>` (or the MCP `cast_create` tool). The identical
questions are asked in every harness this library supports; only the picker fidelity
differs.

## Delegating
Write the brief to a file first — fixed H2 sections `## Task`, `## Scope`,
`## Must still work`, `## Diff`, `## Access` (ui-reviewer: how to start and reach the
running app — command, URL, viewports, credentials), `## Context` (non-blind roles only);
see this repo's `docs/PLAN.md` §B3 for the exact convention — then invoke:

```
claustrum run <role> --brief-file <path> --json [--cast <name>]
```

Parse the single JSON document Claustrum prints to stdout for `status`, `changed_files`,
`diff`, and `report`. When the `claustrum` MCP server is connected, use the `delegate`
tool instead of the shell command: `{role, brief, cwd?, backend?, model?, effort?, tier?,
permission?, cast?, ...}`, still with the brief written to a file first if you already
have one. With no `--cast`/`cast` given, a repo's `.claustrum/casts/default.json` applies
itself automatically if present.

## Coordinating (a spawned architect)
If the cast's `architect.mode` is `spawned`, or the user asks Claustrum to coordinate issues
end-to-end, run `claustrum coordinate --cast <name> --issues <n,m> --json` (or `--brief-file
<path>`), or the MCP `coordinate` tool, which returns `{job_id, log_path}` at once; poll the
`job_status` tool and fetch the `job_result` tool when it reports `done`. The architect runs
headlessly on the cast's model, delegates builders → blind reviewer → tester itself, and leaves
its work on branch `claustrum/<job_id>`; `claustrum jobs budget <job_id>` shows what the tree
spent. If `architect.mode` is `host`, you are the architect: adopt the synced `architect` role
and delegate with `--cast` yourself.
