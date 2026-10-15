# Claustrum

## First time in this repo (or asked to set up/change a cast)
If a command exits 2 saying "this repo isn't initialized", ask the user with the host's
question mechanism (e.g. `AskUserQuestion` in Claude Code) whether to run `claustrum init`.
If yes:
1. Run `claustrum init questions --json` (CLI only — there is no MCP tool for it) — one question per built-in
   model alias the user config does not yet define.
2. Ask each question with the host's native question mechanism. Write the answers to a
   JSON file keyed by each alias.
3. Run `claustrum init --answers <file>` (or the same programmatically) to write the
   aliases to the user config (`~/.config/claustrum/config.json`).

Then (or if already initialized), run `claustrum cast questions --json` (or the MCP
`cast_questions` tool) and ask the user each question with your host's native question
mechanism. Write the answers to a file keyed by each question's `key`, then
`claustrum cast create --answers <file>` (or the MCP `cast_create` tool). The identical
questions are asked in every harness this library supports; only the picker fidelity
differs.

The cast is per-developer (stored in `.claustrum/casts/`, git-ignored) and the model
aliases live in the user config.

## Delegating
Write the brief to a file first — fixed H2 sections `## Task`, `## Scope`,
`## Must still work`, `## Diff`, `## Access` (ui-reviewer and demo-author: how to start and
reach the running app — command, URL, viewports, credentials), `## Context` (non-blind roles
only); see this repo's `docs/PLAN.md` §B3 for the exact convention — then invoke:

```
claustrum run <role> --brief-file <path> --json [--cast <name>]
```

Parse the single JSON document Claustrum prints to stdout for `status`, `changed_files`,
`diff`, and `report`. When the `claustrum` MCP server is connected, use the `delegate`
tool instead of the shell command: `{role, brief, cwd?, backend?, model?, effort?, tier?,
permission?, cast?, ...}`, still with the brief written to a file first if you already
have one. With no `--cast`/`cast` given, a repo's `.claustrum/casts/default.json` applies
itself automatically if present.

A role can stop to ask the user something. When `job_status` (after `delegate_async`) shows
`state: waiting_for_input`, its `question` is what the role asked: put it to the user with the
host's question mechanism and send the answer with `job_say {job_id, text}`. The blocking
`delegate` cannot be answered: the role is told nobody is there, decides, and records the choice.

## Coordinating (a spawned architect)
If the cast's `architect.mode` is `spawned`, or the user asks Claustrum to coordinate issues
end-to-end, run `claustrum coordinate --cast <name> --issues <n,m> --json` (or `--brief-file
<path>`), or the MCP `coordinate` tool, which returns `{job_id, log_path}` at once; poll the
`job_status` tool and fetch the `job_result` tool when it reports `done`. When you run `coordinate` through the
MCP tool, its architect may escalate a question to you: `job_status` then shows `waiting_for_input` with the
question. Ask the user with the host's question mechanism, then send the answer with `job_say {job_id, text}`. The architect runs
headlessly on the cast's model, delegates builders → blind reviewer → tester (→ demo-author,
for a browser-facing feature) itself, and leaves its work on branch `claustrum/<job_id>`;
`claustrum jobs budget <job_id>` shows what the tree spent. `coordinate` no longer needs the
cast committed — an untracked or git-ignored cast works, provided the `.gitignore` rule for
`.claustrum/casts/` (`claustrum init` writes it) is committed. If `architect.mode` is `host`, you
are the architect: adopt the synced `architect` role and delegate with `--cast` yourself.