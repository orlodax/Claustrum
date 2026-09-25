# Claustrum manual

The reference for using Claustrum — every verb, file, field and rule, in one place. It restates
what the code does today (binary `0.1.0-alpha`, role library `1.0.0`, 2026-09-22); the *why* stays
in [NOTES.md](../NOTES.md) and the plan in [PLAN.md](PLAN.md). Getting the binary and the backends
onto a machine is [INSTALL.md](INSTALL.md); running it on a real task for the first time is
[TEST-DRIVE.md](TEST-DRIVE.md).

## 1. What it is, in five words each

| Word | Meaning |
|---|---|
| **harness** | a coding-agent product with a CLI: Claude Code, opencode, Cursor's `cursor-agent`, GitHub Copilot CLI |
| **backend** | Claustrum's adapter for one harness (`claude`, `opencode`, `cursor`, `copilot`) or for a bare HTTP call (`api`) |
| **role** | a job description shipped in the binary: `architect`, `builder`, `code-reviewer`, `ui-reviewer`, `tester` |
| **tier** | how hard a role thinks: `high` (base), `xhigh`, `max`; each tier names a model class and an effort |
| **model class** | an alias a role names (`frontier-coding`, `cheap-coding`, …) that `claustrum.json` maps to a real `backend:model` |
| **cast** | a committed file saying who plays which role, how many builders run at once, and the budget |
| **brief** | the work order: markdown with fixed `##` sections |
| **receipt** | the `RunResult` JSON every run returns, whoever did the work |
| **job** | one run, with a directory under `~/.claustrum/jobs/<id>/` |
| **tree** | a job and every child it spawned, named by `CLAUSTRUM_PARENT_JOB`, sharing one budget |
| **blind** | a role that must not be told the rationale; the runner refuses a brief that carries it |

The core does the same five things on every door: prepare the role's instructions, snapshot the
repository with git, launch the harness and wait, snapshot again, read the final report.

## 2. Two doors

**Door 1, the CLI.** One command, blocks, prints one JSON document on stdout with `--json`
(human-readable status otherwise). Anything an agent can run in a shell can use it.

**Door 2, the MCP server.** `claustrum mcp` speaks stdio MCP; a host that registers it gets the
same operations as tools. `sync` writes the registration for each host (§12).

| MCP tool | Same as | Returns |
|---|---|---|
| `delegate` | `claustrum run` | the receipt (diff capped at 64 KB) |
| `delegate_async` | `claustrum run` in the background | `{job_id, log_path}` |
| `job_status` | — | `{state: queued\|running\|done, elapsed_seconds, last_line}` |
| `job_result` | `claustrum jobs show` | the receipt, once done (throws before) |
| `coordinate` | `claustrum coordinate` | `{job_id, log_path}` — always async; poll `job_status` |
| `list_roles`, `list_backends`, `doctor` | `roles list`, `backends list`, `backends doctor` | summaries |
| `cast_questions`, `cast_create`, `cast_list` | `cast questions`, `cast create`, `cast list` | the questionnaire / the written cast / names |

The server's configuration root is the directory it was launched in (hosts launch MCP servers in
the workspace); `--cwd` on the server and `cwd` on each call override it.

## 3. CLI reference

Exit codes are fixed: `0` ok · `1` backend failure · `2` usage or config error (a bad flag, a
refused brief, a missing cast, a `gh` failure) · `3` backend not found · `4` timeout · `5` budget
· `130` cancelled.

### `claustrum run <role>`

```
claustrum run <role> [--brief <text> | --brief-file <path|->]
    [--cwd <dir>] [--backend <name>] [--model <alias|backend:id>] [--effort <e>] [--tier high|xhigh|max]
    [--permission readonly|shell|edit|edit+shell|full] [--deny <pattern>]…
    [--budget <usd>] [--timeout <sec>] [--resume <session>] [--file <path>]… [--env K=V]…
    [--cast <name>] [--json] [--stream] [--raw]
```

- The brief is mandatory: `--brief`, `--brief-file`, or `--brief-file -` for stdin.
- Precedence for model and tier: explicit flag > cast entry > role's tier default > built-in
  aliases. `--backend` alone changes the harness and keeps the model id, which is rarely what you
  want (`opus` is not a copilot id): prefer `--model backend:id`.
- `--cast` defaults to `.claustrum/casts/default.json` when that file exists. With a cast whose
  builder has `max_parallel > 1`, **every** builder run gets its own worktree and branch (§9).
- `--stream` echoes the backend's output to **stderr** while it runs; stdout still carries only
  the final document. `--raw` adds the backend's untouched response under `raw`.

### `claustrum coordinate`

```
claustrum coordinate [--cast <name>] (--issues 12,13 | --brief <text> | --brief-file <path|->)
    [--cwd <dir>] [--tier …] [--model <architect model>] [--budget <usd>] [--timeout <sec>]
    [--json] [--stream] [--raw]
```

Runs the cast's **architect** headlessly (§11). `--issues` needs `gh` authenticated and a GitHub
remote; a failure there is exit 2 and nothing is spent. It never reads a bare stdin: `--brief-file -`
is the only way to pipe. Its `--budget` caps the architect's own run; the cast's `budget_usd` caps
the children.

### `claustrum roles list | show <name>`

The embedded library: description, `blind`, permission, deny list, `mayDelegate`, report schema,
supported harnesses, and the three tiers with their model class and effort.

### `claustrum backends list | doctor [<name>] [--probe]`

`doctor` is free: found, path, version, problems, plus a `gh:` block (bare form only) and the
merged configuration with the layer each value came from (`[Builtin]`, `[User]`, `[Repo]`,
`[Env]`, `[Flag]`). `--probe` adds auth presence (never a value), MCP registration, OS/path
checks, and **one paid request per installed backend**, capped at $0.50 each, run through the
cheapest configured alias that resolves to that backend; `CLAUSTRUM_SKIP_PROBE=1` turns the paid
part into a named skip. The probe needs a `claustrum.json` at the git root whose aliases reach the
backend, or it skips with "no model alias … resolves to this backend".

### `claustrum jobs list | show <id> | logs <id> [--stderr] | clean [--cwd] | budget <tree> [--reset]`

- `list`: recent jobs, newest first — id, status, `role/backend`.
- `show`: the receipt (`result.json`), or the request if it has not finished.
- `logs`: the captured stdout (readable while the job runs); `--stderr` for the other stream.
- `clean`: removes the **worktrees** of finished parallel jobs; their branches stay.
- `budget <tree>`: the ledger of one tree (§10); `--reset` deletes it, refused while a member runs.

### `claustrum cast questions [--json] | create --answers <file> [--name n] | new [--name n] | list | show <name> | use <name>`

§8. `new` asks on the terminal; `create` takes a JSON file `{question key: answer}`; `use` copies a
cast over `default.json`.

### `claustrum sync`

```
claustrum sync [--only claude,opencode,copilot,cursor] [--roles a,b] [--global] [--force] [--check] [--dry-run]
```

§12. `--check` exits 2 on a hand-edited, stale or missing generated file (for CI); `--dry-run`
prints the unified diff; `--force` adopts a file Claustrum did not generate.

### `claustrum init [--all]`

Scaffolds the current directory (no `--cwd`): writes `claustrum.json` if absent, creates
`.claustrum/casts/` and git-ignores `.claustrum/{briefs,worktrees,locks}/`, runs `sync` for the
harnesses the repo already shows signs of (`.claude/`, `.cursor/`, `.github/`, `opencode.json`;
`--all` for every one), appends a "Claustrum delegation" pointer to an existing `AGENTS.md`. It
never creates `AGENTS.md`/`CLAUDE.md` and never overwrites a file it did not generate; a rerun
reports "already present, left unchanged".

### `claustrum mcp [--cwd <dir>]`

The stdio server. Logging goes to stderr; stdout is the protocol.

## 4. Roles

| Role | Blind | Permission | Deny | May delegate to | Harnesses | Tier `high` → `xhigh`/`max` |
|---|---|---|---|---|---|---|
| `architect` | no | edit+shell | `git push` | builder, code-reviewer, ui-reviewer, tester | claude, opencode, cursor, copilot | frontier-reasoning at every tier |
| `builder` | no | edit+shell | `git push` | architect (to ask, not to delegate work) | claude, opencode, cursor, copilot | frontier-coding at every tier |
| `code-reviewer` | **yes** | readonly | — | — | claude, opencode, cursor, copilot, **api** | standard-coding → frontier-coding |
| `ui-reviewer` | **yes** | shell (run, never edit) | `git push` | — | claude only (needs the Browser MCP) | standard-coding → frontier-coding |
| `tester` | no | edit+shell | `git push` | — | claude, opencode, cursor, copilot | standard-coding → frontier-coding |

The pipeline order is fixed and the architect owns every step of it: **builder(s) → code-reviewer
(‖ ui-reviewer when something renders in a browser) → tester**. Builders do not test and do not
call reviewers; the reviewers and the tester never fix code. "The architect never picks a model, it
picks the tier": the model comes from the library class and the cast; a heavier tier also buys a
stronger class for the three reviewing/testing roles.

A role's source is `roles/<role>/ROLE.md` + `role.json` + `parts/<part>.<harness>.md`, embedded in
the binary. A repo may override one under `.claustrum/roles/<role>/` (deep-merged, header says
`source=local`). Each role's system body is: the rendered `ROLE.md` for the target harness, then
`## House rules` ("read `AGENTS.md` and `CLAUDE.md` first; they are law"), then `## Report format`.
The brief always goes in the **user** prompt.

## 5. The brief

Markdown with fixed H2 sections; the runner reads them, the roles are told to expect them.

| Section | Who | What |
|---|---|---|
| `## Task` | all | the requester's words, verbatim — the requirement, not your reading of it |
| `## Scope` | all | the paths in play |
| `## Must still work` | all | behaviour that has to survive the change, stated behaviourally |
| `## Diff` | reviewers | how to obtain it: branch and base, commit range, PR number, "the working diff" |
| `## Access` | ui-reviewer | how to start and reach the app logged in, as a role, credential-free |
| `## Context` | **non-blind roles only** | plan, rationale, house-style reminders, previous reports |

**The blind gate.** For `blind: true` roles the runner refuses, with exit 2 and
`blind role: brief carries rationale`, any brief containing `## Context`, `## Plan`,
`## Rationale`, or a pasted `claustrum-report` block. It is refusal, not advice: a blind reviewer
gets the task as the user framed it and the diff, nothing else, so that it has to decide for itself
whether they match. What it must **not** lack is `## Must still work` — a brief made only of
defects makes a reviewer argue for the stricter gate every time.

Write briefs to files (`.claustrum/briefs/<n>-<role>.md`, git-ignored) and pass `--brief-file`;
the MCP `brief` argument takes the same text inline.

## 6. The receipt (`RunResult`)

One JSON document, `schema_version: "1"`, snake_case, additive changes only:

| Field | Source | Notes |
|---|---|---|
| `job_id` | runner | `yyyyMMdd-HHmmss-8hex`; also the directory name under the jobs home |
| `status` | runner | `success`, `failed`, `timeout`, `cancelled`, `backend_missing`, `budget_exceeded` |
| `error` | runner | **read it whenever `status` is not `success`** — it says which refusal or failure |
| `backend`, `model`, `role` | resolved request | what actually ran |
| `final_message` | backend | the agent's last message, report fence included |
| `report` | extracted | `{data: <parsed JSON>, raw_text}` from the `claustrum-report` fence |
| `report_status` | extracted | `ok`, `missing` (no fence), `unparsed` (fence, invalid JSON — `raw_text` kept) |
| `changed_files[{path, kind}]` | **git**, before/after snapshots | `A`/`M`/`D`; right even when the harness does not list edits |
| `diff`, `diff_truncated` | git | `git diff HEAD` on those paths plus `--no-index` for new files; 200 KB cap (64 KB via MCP) |
| `worktree`, `branch` | runner | only for a parallel builder: where it ran, `claustrum/<job_id>` |
| `cost_usd`, `usage` | backend | `null` for copilot and cursor, which report neither |
| `session_id` | backend | for `--resume` |
| `exit_code`, `duration_seconds`, `log_path` | runner | `log_path` is the job's `stdout.log` |
| `warnings[]` | runner | e.g. several report fences, last one won |
| `raw` | backend | only with `--raw` / `include_raw` |

Renames arrive as an `A` plus a `D`; a non-git working directory falls back to an mtime/size scan
with `diff: null`.

### Report schemas per role

Every role must end its final message with exactly one fenced block tagged `claustrum-report`:

- **builder**: `{status: done|partial|blocked, summary, files_changed[{path, why}], behaviour_to_cover[], open_decisions[], shaky[], departed_from_brief[]}` — `shaky` is what the coordinator reads first.
- **code-reviewer / ui-reviewer**: `{status, findings[{severity, verdict: CONFIRMED|PLAUSIBLE, file, line, scenario, steps, evidence}], would_change_if_broader[]}`, most severe first; an empty list is a legitimate clean result.
- **tester**: `{status, commands_run[], passed, failed[{test, root_cause, fault_in: test|code}], skipped[]}`.
- **architect**: `{status, summary, delegations[{role, tier, job_id, status}], branch, closes[], findings_open[], open_decisions[], shaky[]}` — for a spawned architect this block is the entire result the caller sees.

## 7. Configuration

Layers, later wins per key, `deny` lists concatenate:

1. built-in defaults;
2. `~/.config/claustrum/config.json` (Windows `%APPDATA%\claustrum\config.json`);
3. `<git root>/claustrum.json`;
4. `CLAUSTRUM_BUDGET_USD`, `CLAUSTRUM_TIMEOUT_SECONDS`, `CLAUSTRUM_ENV_PASSTHROUGH`;
5. flags.

```json
{
  "models":   { "frontier-reasoning": "claude:opus", "frontier-coding": "claude:opus",
                "standard-coding": "claude:sonnet", "cheap-coding": "opencode:openrouter/deepseek/deepseek-v4-flash",
                "fast": "claude:haiku", "reviewer": "api:anthropic:claude-sonnet-5" },
  "roles":    { "builder": { "model": "cheap-coding", "effort": "medium", "permission": "edit+shell", "deny": ["git push"] } },
  "backends": { "claude": { "path": null, "injection": "append-file" }, "copilot": { "path": "/opt/copilot/bin/copilot" } },
  "defaults": { "timeout_seconds": 1800, "budget_usd": 5, "env_passthrough": "allowlist" },
  "jobs":     { "keep_last": 200 }
}
```

**Model grammar**: `[backend:]<model-id>`; a bare name is looked up as an alias (recursively,
depth 3), then handed to the backend as an id. `claustrum init` writes only two aliases
(`frontier-coding: claude:opus`, `cheap-coding: opencode:openrouter/deepseek/deepseek-v4-flash`);
the built-ins fill the rest with `claude:*`. What each backend accepts as an id:

| Backend | Model id form | Verified ids |
|---|---|---|
| `claude` | Claude Code's own names | `opus`, `sonnet`, `haiku` |
| `opencode` | `provider/model`, e.g. `openrouter/deepseek/deepseek-v4-flash` | fixture-only on this machine (no key) |
| `copilot` | Copilot's names | `auto` — the built-in default `opus` is refused before any API call |
| `cursor` | Cursor's names | `auto` — the only id a Free plan accepts |
| `api` | `openrouter:<model>` or `anthropic:<model>` (so `api:anthropic:claude-sonnet-5`) | keyed by `OPENROUTER_API_KEY` / `ANTHROPIC_API_KEY` |

**Environment passed to backends** is an allow-list: `PATH HOME USERPROFILE APPDATA LOCALAPPDATA
TEMP TMP SystemRoot ComSpec LANG SHELL TERM XDG_* *_API_KEY ANTHROPIC_* OPENROUTER_* OPENCODE_*
CURSOR_* COPILOT_* GH_TOKEN GITHUB_TOKEN NODE_*`, plus `--env K=V`; `env_passthrough: all` disables
the filter. `CLAUSTRUM_*` is deliberately **not** forwarded except `CLAUSTRUM_PARENT_JOB` and
`CLAUSTRUM_HOME`, which the runner sets itself for a tree.

**Claustrum's own variables**: `CLAUSTRUM_HOME` (jobs, budget ledgers; default `~/.claustrum`),
`CLAUSTRUM_PARENT_JOB` (membership in a tree, §10), `CLAUSTRUM_SKIP_PROBE=1`,
`CLAUSTRUM_SMOKE_MODEL_<BACKEND>` and `CLAUSTRUM_SMOKE_COORDINATE_MODEL` (the smoke scripts' paid
rows), and the three config overrides above.

Keys never go in `claustrum.json` or a cast: both are committable. `doctor` prints only whether a
variable is present.

## 8. Casts

`.claustrum/casts/<name>.json`, committable, shared with the team:

```json
{ "name": "default", "library": "1.0.0",
  "architect": { "mode": "host" | "spawned", "model": "<alias|backend:id>" | null, "tier": "high|xhigh|max" | null },
  "roles": {
    "builder":       { "model": "standard-coding", "backend": null, "tier": null, "max_parallel": 2 },
    "code-reviewer": { "model": "frontier-coding", "backend": null, "tier": "xhigh", "max_parallel": null },
    "tester":        { "model": "standard-coding", "backend": null, "tier": null, "max_parallel": null },
    "ui-reviewer":   null },
  "budget_usd": 15 }
```

- `architect.mode`: `host` — the agent you chat with adopts the role; `spawned` — `coordinate` runs
  it headlessly on `architect.model`.
- A role set to `null` is "not needed": an architect under that cast neither delegates to it nor
  does its work.
- `max_parallel` on the builder > 1 turns on worktree isolation for every builder run (§9).
- `budget_usd: null` disables the cap (`cast show` prints `budget: unlimited`); a per-call
  `--budget` still applies.
- Per-call `--model`/`--backend`/`--tier` flags override the cast for that call.

**The questionnaire** is owned by the tool so it is identical in every host: `claustrum cast
questions --json` returns seven questions with their live options (the aliases in `claustrum.json`;
`not needed` where allowed; free-form `backend:id`; `no cap` for the budget):

| key | prompt | allows |
|---|---|---|
| `architect` | `host` or `spawned on <alias>` | free form |
| `builder` | model for the builder | free form |
| `code-reviewer`, `tester`, `ui-reviewer` | model for the role | free form, `not needed` |
| `builder_max_parallel` | `1`, `2`, `3`, … | free form |
| `budget` | USD, or `no cap` | free form |

Answer them with `claustrum cast new` on a terminal, `claustrum cast create --answers file.json`
from a script, or `/claustrum` in a chat host (the synced skill asks with the host's own picker,
then calls `cast_create`).

## 9. Parallel builders

With `max_parallel > 1` each builder job runs in `git worktree add .claustrum/worktrees/<job> -b
claustrum/<job>` from the repo's `HEAD`; `changed_files`/`diff` are computed inside the worktree and
the receipt carries `worktree` and `branch`. The cap is a per-cast semaphore shared by the CLI and
the MCP door: a job past the cap **waits** for a slot, up to its `--timeout` (default 1800 s), and
then comes back `status: failed` with `all N '<cast>__<role>' slots … stayed unavailable`, having
done nothing.

Integration is the architect's and it is by **rebase onto the work branch, then fast-forward** —
never a merge commit, never `git push` (every role's deny list). `claustrum jobs clean` removes the
worktrees of finished jobs and keeps the branches.

## 10. Budget

Three different caps, and only one of them is shared:

1. `--budget` on a call (or `defaults.budget_usd`, built-in $5) is the per-run cap handed to the
   backend (`--max-budget-usd` on claude); the cast's `budget_usd` becomes this cap when no flag is
   given.
2. `--probe`'s $0.50 is per probe request.
3. **The tree ledger** is what `budget_usd` really caps: every process that carries
   `CLAUSTRUM_PARENT_JOB=<tree id>` is a member, `<CLAUSTRUM_HOME>/budget/<tree>/` holds one entry
   per member, and a child that would exceed what is left is refused with `status:
   budget_exceeded` **before it starts**. No variable, no tree, no accounting — a plain `claustrum
   run` from a shell is its own world. `claustrum coordinate` is what sets the variable.

The admission rule: a child is granted `min(requested cap ?? remaining / share, remaining)`, where
`share` is the role's `max_parallel` (1 for every role without one). The grant is **reserved** for
the child's whole lifetime, so a child started without `--budget` reserves the entire remainder and
a sibling started alongside it is refused in milliseconds having spent nothing. Hence the rule the
architect is given: when starting two children at once, pass each an explicit `--budget` that
together fit the remainder. A backend that reports no cost (copilot, cursor) is charged its whole
cap.

`budget_exceeded` carries one of four messages in `error`, and only one means stop:

| `error` says | Do |
|---|---|
| "… while N running job(s) hold …" | wait for a running child to finish, retry |
| "$R remaining; --budget X exceeds it" | retry with `--budget` ≤ R, or wait for a sibling |
| "rounds to $0.00 — pass --budget (at most $Y)" | retry with that explicit `--budget` |
| "$0.00 remaining" and nothing of yours running | the tree is spent: stop and report |

`claustrum jobs budget <tree>` prints the ledger; `--reset` clears it. The architect spawned by
`coordinate` is **not** a member of its own tree (it would otherwise hold the whole cap for its
whole life); its own run is capped at the cast's `budget_usd` separately, so the worst case of one
`coordinate` is 2× the cast budget, and the architect's cost is recorded next to the ledger.

## 11. Coordinate: the spawned architect

`claustrum coordinate --cast <name> (--issues n,m | --brief …)` is not a second pipeline. It builds
the same request `run` builds, for the `architect` role, and adds exactly three things:

1. **The cast appended to the system body** as a `## Coordination` section: the cast line by
   line ("builder: model …, tier …, max_parallel …", "ui-reviewer: not needed — do not delegate
   to it"), the budget across the tree, the delegate command (`claustrum run <role> --cast "<name>"
   --brief-file <path> --json --cwd "<dir>"`), the four `budget_exceeded` routes, "write each brief
   to `.claustrum/briefs/<n>-<role>.md`", and the work branch: **`claustrum/<job id>`, created from
   `HEAD` before delegating anything**, into which each parallel builder's branch is rebased and
   fast-forwarded — never a merge, never a push.
2. **`CLAUSTRUM_PARENT_JOB=<job id>`** in the architect's environment, so every `claustrum` it runs
   joins the tree.
3. **`gh issue view <n> --json title,body,labels`** folded into the brief's `## Task` (`--issues`),
   with the instruction that the resolving commit cites `Closes #<n>`.

On `claude` the architect's native `Agent` tool is disallowed, so it cannot fan out outside the
cast. Everything that can be refused (flags, cast, `gh`, the brief) is refused **before** a job
directory exists. The result is the architect's receipt: its `report.data.delegations[]` lists
every child with a job id, `branch` names the work branch, and `claustrum jobs budget <job id>` is
the tree's ledger. It leaves the work on the branch; pushing and the PR are yours.

From MCP, `coordinate` returns `{job_id, log_path}` immediately; poll `job_status`, then
`job_result`.

## 12. Sync: one role library, every harness

`claustrum sync` renders the library into each harness's own format and registers the MCP server.
Generated markdown carries `<!-- claustrum:generated role=… harness=… library=… sha256=… -->`
as its first body line; generated JSON keys are tracked by hash in `.claustrum/sync-manifest.json`.
A file without the marker or a manifest entry is **never overwritten** (`--force` adopts it).

| Harness | Repo files | Global (`--global`) | In chat |
|---|---|---|---|
| `claude` | `.claude/agents/<role>.md` + `-xhigh`/`-max`, `.claude/skills/claustrum/SKILL.md`, `.mcp.json` (`mcpServers.claustrum`), `.vscode/mcp.json` (`servers.claustrum`, stdio) | `~/.claude/agents`, `~/.claude/skills`; on Windows/macOS also `claude_desktop_config.json` with the binary's absolute path | `/claustrum`, `@architect` … |
| `opencode` | `.opencode/agent/<role>.md`, `.opencode/command/claustrum.md`, `opencode.json` (`mcp.claustrum`) | `~/.config/opencode/` | `/claustrum` |
| `copilot` | `.github/agents/<role>.agent.md`, `.github/skills/claustrum/SKILL.md` | `~/.copilot/agents` | `copilot --agent architect`; the skill surfaces by relevance |
| `cursor` | `.cursor/agents/<role>.md` + variants, `.cursor/skills/claustrum/SKILL.md`, `.cursor/mcp.json` | `~/.cursor/{agents,skills,mcp.json}` | `/claustrum`, subagents by name |

The repo `.mcp.json` registers `"command": "claustrum"` — the bare name — so the binary must be on
the `PATH` of whatever spawns the host. On Linux there is no desktop-app config to write; `sync`
prints one line saying so.

The synced `/claustrum` skill does three things: first time in a repo, ask the cast questionnaire
and write the cast; when delegating, write the brief to a file and call `claustrum run … --json` (or
the `delegate` tool); when the cast says `spawned` or the user asks to coordinate issues end to
end, call `coordinate` and relay `job_status`.

## 13. Backends

| Backend | Install | Auth | Role injection | Cost in receipt |
|---|---|---|---|---|
| `claude` | `npm i -g @anthropic-ai/claude-code` | Claude Code login or `ANTHROPIC_API_KEY` | `--append-system-prompt-file <job>/system.md` | yes (`total_cost_usd`) |
| `opencode` | `npm i -g opencode-ai` | `OPENROUTER_API_KEY` / `DEEPSEEK_API_KEY` / `opencode auth` | inline agent in `OPENCODE_CONFIG_CONTENT`, process-scoped | from the JSONL usage events |
| `cursor` | Cursor's `cursor-agent` installer (not the npm package of that name) | `cursor-agent login` or `CURSOR_API_KEY` | role prefixed to the prompt, fed on **stdin**; `-f` always (an untrusted workspace is a fast exit 1) | **none**; usage tokens only |
| `copilot` | `npm i -g @github/copilot` | logged in (`~/.copilot/config.json`) or `COPILOT_GITHUB_TOKEN`/`GH_TOKEN`/`GITHUB_TOKEN` | per-job `.agent.md` via `--add-dir`, agent `claustrum-<role>` | **none** (`assistant.usage` is suppressed in JSON mode); `--reasoning-effort` is omitted for `auto` |
| `api` | `curl` | `OPENROUTER_API_KEY` / `ANTHROPIC_API_KEY` by model prefix | system message; **no tools**, `changed_files` always empty | yes |

Permission levels and what each backend does with them:

| Level | Roles | `claude` | `opencode` | `cursor` | `copilot` |
|---|---|---|---|---|---|
| `readonly` | code-reviewer | `--permission-mode plan` + read-only tool list | edit/bash deny | prompt rule (advisory) | `--mode plan --deny-tool write --deny-tool shell` |
| `shell` | ui-reviewer | plan + `--disallowedTools Edit,Write,NotebookEdit`, MCP tools reachable | edit deny, bash allow with deny patterns | prompt rule | `--mode plan --deny-tool write` + deny patterns |
| `edit` | — | `acceptEdits` + `--disallowedTools Bash` | edit allow, bash deny | prompt rule | `--allow-all-tools --allow-all-paths --deny-tool shell` |
| `edit+shell` | architect, builder, tester | `acceptEdits`, built-in tools + `Bash(*)`, `--disallowedTools "Bash(git push*)"` | edit allow, bash allow, `git push*` deny | prompt rule | `--allow-all-tools --allow-all-paths --deny-tool "shell(git push)"` |
| `full` | — | `--dangerously-skip-permissions` | `--auto` | `-f --sandbox disabled` | `--allow-all` |

Where a harness has no native deny mechanism (cursor) the deny list is a rule in the prompt, and
`doctor` calls it advisory. Claustrum launches backends on the OS it runs on and never translates
paths: a WSL binary drives Linux harnesses against Linux paths, a Windows binary drives Windows
ones; `doctor --probe` warns when binary and working directory straddle that line.

## 14. Jobs on disk

`~/.claustrum/jobs/<job id>/` (`CLAUSTRUM_HOME` overrides the root): `request.json`, `system.md`
(the exact system body the role got — the first place to look when a role misbehaves),
`stdout.log`, `stderr.log`, `result.json`. `jobs.keep_last` (200) prunes old ones when a new job is
created. Budget ledgers live beside them under `budget/<tree>/`.

## 15. Troubleshooting

| You see | It means | Fix |
|---|---|---|
| exit 2, `blind role: brief carries rationale` | the reviewer's brief has `## Context`/`## Plan`/`## Rationale` or a pasted report | remove it; add requirement or code, never reasoning |
| exit 2, `cast '<x>' not found` / `coordinate needs a cast` | no `.claustrum/casts/<x>.json` in the cwd | `cast create`/`cast new`, or `--cast` the right name |
| exit 3 | the backend's binary is not on `PATH` | install it, or `backends.<name>.path` in config |
| `Error: Model "opus" … is not available` (copilot, cursor) | the built-in `claude:*` alias reached a non-claude backend | alias the class to `copilot:auto` / `cursor:auto` |
| `api backend model must be 'openrouter:<model>' or 'anthropic:<model>'` | `--backend api` without a provider-prefixed model | `--model api:anthropic:<id>` |
| `report_status: missing` | the role never wrote its report fence | read `final_message`; the run still counts, the report does not |
| `status: failed`, "… slots … stayed unavailable" | waited past `--timeout` for a `max_parallel` slot | start fewer at once, or raise `--timeout` |
| `status: budget_exceeded` in milliseconds | admission refused it, nothing spent | read `error` — the four routes in §10 |
| `status: timeout` on `coordinate` | the architect's `--timeout` (default 1800 s) is too small for a whole pipeline | rerun with more; finished children are still in `jobs list` |
| MCP tools absent in the host | the host could not spawn `claustrum` (not on its `PATH`) | absolute `command` in `.mcp.json`, or install the binary where the GUI looks |
| copilot probe "no model alias in claustrum.json resolves to this backend" | not in a git repo, or no `copilot:*` alias | run from the repo root with the alias present |
| `dotnet test … Zero tests ran`, exit 5 | `-nologo` was passed and forwarded to the test app | drop the flag |

## 16. Not in v1, and not verified

Deliberately out: a pipeline state machine in C#, `opencode serve`/ACP/Copilot SDK transports,
YAML config, ui-reviewer on non-Claude harnesses, Windows↔WSL path translation, the `mc-*`
metaconcert roles.

Not yet measured on a real install: opencode and `api` end to end on this machine (no key);
copilot ids other than `auto`, `--resume`, the `full` rung; cursor on a paid plan; whether opencode
and cursor tolerate the unquoted `description:` their syncs emit; `coordinate` through the MCP door
from a real host; and the Windows leg of everything above.
