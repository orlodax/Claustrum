# Claustrum manual

The reference for using Claustrum — every verb, file, field and rule, in one place. It restates
what the code does today (binary `0.1.0-alpha`, role library `1.0.0`; checked against the code
2026-10-08, #54); the *why* stays
in [NOTES.md](../NOTES.md) and the plan in [PLAN.md](PLAN.md). Getting the binary and the backends
onto a machine is [INSTALL.md](INSTALL.md); running it on a real task for the first time is
[TEST-DRIVE.md](TEST-DRIVE.md).

## 1. What it is, in five words each

| Word | Meaning |
|---|---|
| **harness** | a coding-agent product with a CLI: Claude Code, opencode, Cursor's `cursor-agent`, GitHub Copilot CLI |
| **backend** | Claustrum's adapter for one harness (`claude`, `opencode`, `cursor`, `copilot`) or for a bare HTTP call (`api`) |
| **role** | a job description shipped in the binary: `architect`, `builder`, `code-reviewer`, `ui-reviewer`, `tester`, `demo-author` |
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
| `coordinate` | `claustrum coordinate` | `{job_id, log_path, warnings}` — always async; poll `job_status` |
| `list_roles`, `list_backends`, `doctor` | `roles list`, `backends list`, `backends doctor` | summaries |
| `cast_questions`, `cast_create`, `cast_list` | `cast questions`, `cast create`, `cast list` | the questionnaire / the written cast / names |

The server's configuration root is the directory it was launched in (hosts launch MCP servers in
the workspace); `--cwd` on the server and `cwd` on each call override it.

## 3. CLI reference

Exit codes are fixed: `0` ok · `1` backend failure · `2` usage or config error (a bad flag, a
refused brief, a missing cast, a role sent to a harness it does not list, a `gh` failure, a
`coordinate` whose cast is not committed) · `3` backend not found · `4` timeout · `5` budget · `130`
cancelled.

### `claustrum run <role>`

```
claustrum run <role> [--brief <text> | --brief-file <path|->]
    [--cwd <dir>] [--backend <name>] [--model <alias|backend:id>] [--effort <e>] [--tier high|xhigh|max]
    [--permission readonly|shell|edit|edit+shell|full] [--deny <pattern>]…
    [--budget <usd>] [--timeout <sec>] [--resume <session>] [--file <path>]… [--env K=V]…
    [--cast <name>] [--branch <name>] [--json] [--stream] [--raw]
```

- The brief is mandatory: `--brief`, `--brief-file`, or `--brief-file -` for stdin.
- Precedence for model and tier: explicit flag > cast entry > role's tier default > built-in
  aliases. `--backend` alone changes the harness and keeps the model id, which is rarely what you
  want (`opus` is not a copilot id): prefer `--model backend:id`.
- `--cast` defaults to `.claustrum/casts/default.json` when that file exists. A cast role with a
  numeric `max_parallel` — 1 included — caps how many of its runs go at once; above 1, **every**
  builder run gets its own worktree and branch (§9).
- `--branch <name>` runs isolated in a new worktree checked out on that **existing local branch**,
  whatever the cast's `max_parallel` — e.g. `--branch claustrum/<job id>` to send a reviewed
  builder's branch back for a fix (§9). Two runs on one branch run one after the other: the second
  waits for the first's receipt, up to its `--timeout`. An unknown name, a branch checked out
  anywhere but the clean worktree of a job finished under this `CLAUSTRUM_HOME`, one git itself
  will not check out (mid-rebase in another worktree) or does not check out within 5 minutes, or a
  wait for the branch that outlasts `--timeout` comes back `status: failed` (exit 1, a receipt like
  any other) with the reason — and the holder, if any — in `error`, and no worktree left behind
  (`error` names anything the undo could not remove).
- `--stream` echoes the backend's output to **stderr** while it runs; stdout still carries only
  the final document. `--raw` adds the backend's untouched response under `raw`.
- Without `--json`, stdout gets `status:`, then `branch:`/`worktree:`/`commit:` when the run was
  isolated, the changed files and the final message; every warning goes to stderr as
  `warning: …`, and the error as `error: …`.

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

In a git repository the architect works in its own worktree, which holds committed files only, so
`coordinate` exits 2 — before any job exists, nothing spent — naming what to fix when: the cwd is
not the repository root; the cast file, `claustrum.json` or a role file under `.claustrum/roles/`
(`<role>/role.json`, `<role>/ROLE.md`, `<role>/parts/*.md` — nothing else there is read, so a
`.DS_Store` or a swap file is not checked) is modified, staged, untracked, ignored, `skip-worktree`
or `assume-unchanged` (`commit (or un-ignore) <path> (<state>) first`); the `.gitignore` holding
Claustrum's rules has any uncommitted change (`… the worktree gets HEAD's ignore rules; commit it
first`); an `.mcp.json`/`opencode.json`/`opencode.jsonc` is untracked (`commit or git-ignore … — an
ignored local copy is fine`); or `.claustrum/worktrees/`, `.claustrum/briefs/` and
`.claustrum/locks/` are not ignored by a committed rule (`claustrum init` writes them). A committed
harness config with a local edit — `skip-worktree`/`assume-unchanged` included, the usual way to keep
a token local — is not refused: the worktree gets `HEAD`'s copy, and `coordinate` says so on stderr
as `warning: …` before the run starts. The same refusals come back as the MCP tool's own error, the
warnings in its `warnings` array. Without `--json`, stdout adds `branch:`, `worktree:` and `commit:`
after `logs:`, and every warning on the receipt goes to stderr as `warning: …`.

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
- `clean`: removes the **worktrees** of finished isolated jobs; their branches stay, with the work
  committed on them. It never forces: a worktree with anything uncommitted — new files included,
  whatever `status.showUntrackedFiles` says (a commit the runner could not make, a hard-killed run)
  — or one git will not remove plainly (a submodule the role populated) is printed as `could not
  remove <job>: <path> left in place: <reason>` and exits 1. Commit or discard what is in it, then
  rerun. Only `<cwd>/.claustrum/worktrees` is swept: a `coordinate` architect's worktree holds its
  builders' under its own, and is left in place (`it holds job worktrees of its own …`) until
  `claustrum jobs clean --cwd <that worktree>` has removed them — git would delete them with it,
  uncommitted work included. An empty `probe-<hex>` directory there, which a `coordinate` killed
  mid-check leaves behind, is deleted (`removed stray probe directory <name>`); a non-empty one is
  never touched.
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
`.claustrum/casts/` and git-ignores `.claustrum/{briefs,worktrees,locks}/`, runs `sync` for `claude`
**always** — its sync is the one that registers the MCP server in `.mcp.json`/`.vscode/mcp.json` —
plus each harness the repo already shows signs of (`.opencode/` or `opencode.json[c]`, `.github/`,
`.cursor/`; `--all` for every one), and appends a "Claustrum delegation" pointer to an existing
`AGENTS.md`. It never creates `AGENTS.md`/`CLAUDE.md` and never overwrites a file it did not
generate; a rerun reports "already present, left unchanged".

### `claustrum mcp [--cwd <dir>]`

The stdio server. Logging goes to stderr; stdout is the protocol.

## 4. Roles

| Role | Blind | Permission | Deny | May delegate to | Harnesses | Tier `high` → `xhigh`/`max` |
|---|---|---|---|---|---|---|
| `architect` | no | edit+shell | `git push` | builder, code-reviewer, ui-reviewer, tester, demo-author | claude, opencode, cursor, copilot | frontier-reasoning at every tier |
| `builder` | no | edit+shell | `git push` | architect (to ask, not to delegate work) | claude, opencode, cursor, copilot | frontier-coding at every tier |
| `code-reviewer` | **yes** | readonly | — | — | claude, opencode, cursor, copilot, **api** | standard-coding → frontier-coding |
| `ui-reviewer` | **yes** | shell (run, never edit) | `git push` | — | claude only (its browser part) | frontier-coding at every tier |
| `tester` | no | edit+shell | `git push` | — | claude, opencode, cursor, copilot | standard-coding at every tier |
| `demo-author` | no — briefed sighted | edit+shell (it writes its deck) | 24 git verbs: `push`, the common ones that stage or commit (`add`, `commit`, `apply`, `update-index`, `read-tree`, …) and that move `HEAD`, refs or the tree (`checkout`, `reset`, `stash`, `merge`, `pull`, `update-ref`, …) — not every one (`branch -f`/`-D`, `worktree remove`, `tag`, `notes` stay open); read-only git stays open, `merge-base` included | — | claude only (its browser part) | standard-coding, one tier |

A role runs only on the harnesses it lists: `run`, `delegate` and `coordinate` refuse a registered
backend outside that list with exit 2 before any job exists (a demo-author mapped to opencode, say),
and `cast questions` offers each role only the aliases that land on one of its harnesses;
`cast create`/`cast new`/`cast_create` refuse an answer that lands elsewhere. An unregistered backend
name is not checked: a role that renders on any harness (builder, tester, …) comes back
`backend_missing`, while the demo-author and ui-reviewer fail to render first (`no part 'browser'
for harness '<name>'`, exit 2). A repo that ships a role's parts for another harness may list it in
`.claustrum/roles/<role>/role.json` `harnesses`.

The pipeline order is fixed and the architect owns every step of it: **builder(s) → code-reviewer
(‖ ui-reviewer when something renders in a browser) → tester → demo-author** (every browser-facing
feature, once the gate is green, unless the caller says no demo). Builders do not test, never
create, modify or delete a test file, and do not call reviewers; the tester runs the repo's full
gate, pre-existing tests included; the reviewers, the tester and the demo-author never fix code. The
demo-author's deck and video stay on disk, gitignored, in the main checkout's
`docs/demos/<feature>/` and are never committed — at a path git tracks or ever tracked it records
into a `<directory>-<short commit>/` sibling instead. The only line it ever adds to a `.gitignore`
is `docs/demos/`, and that is all the architect commits of its output; any other deck directory is
ignored through the local `info/exclude`, which it always writes and which needs no commit. It
records a commit that contains the feature, never uncommitted changes. Every role stops what it
started before it reports. "The architect never picks a model, it picks the tier": the model comes
from the library class and the cast; a heavier tier also buys a stronger class for the three
reviewing/testing roles.

A role's source is `roles/<role>/ROLE.md` + `role.json` + `parts/<part>.<harness>.md`, embedded in
the binary. A repo may override one under `.claustrum/roles/<role>/` in the **working directory**
(`--cwd`, else the process's own) — not the git root `claustrum.json` is read from. `ROLE.md` and
each `parts/` file replace the library's; `role.json` is deep-merged (objects merge, arrays
replace); `_shared/` (house rules, report formats, tier stub) is not overridable. A file synced
from an overridden role carries `source=local` in its marker, `roles show` prints `source: local`,
and a malformed local `role.json` is exit 2 naming the file.

Each role's system body is: the opening paragraph of the rendered `ROLE.md`, then `## Report format`,
then the rest of `ROLE.md`, then `## House rules` ("read `AGENTS.md` and `CLAUDE.md` first; they are
law"). The report format sits second because a model skipped it on short tasks when it came last
(`RoleRenderer.ComposeSystemBody`). The brief always goes in the **user** prompt.

## 5. The brief

Markdown with fixed H2 sections; the runner reads them, the roles are told to expect them.

| Section | Who | What |
|---|---|---|
| `## Task` | all | the requester's words, verbatim — the requirement, not your reading of it |
| `## Scope` | all | the paths in play |
| `## Must still work` | all | behaviour that has to survive the change, stated behaviourally |
| `## Diff` | reviewers | how to obtain it: branch and base, commit range, PR number, "the working diff" |
| `## Access` | ui-reviewer, demo-author | how to start and reach the app logged in, as a role, credential-free |
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
| `changed_files[{path, kind}]` | **git**, before/after snapshots | `A`/`M`/`D`; right even when the harness does not list edits. An isolated run whose branch moved reports the branch's delta from the commit it started on instead — what the runner or the role committed, plus anything left uncommitted; empty when the run's worktree was no longer its own (`warnings[]` says so) |
| `diff`, `diff_truncated` | git | `git diff HEAD` on those paths plus `--no-index` for new files (an isolated run whose branch moved: `git diff <starting commit>`); 200 KB cap (64 KB via MCP) |
| `worktree`, `branch` | runner | for any isolated run (`max_parallel > 1` or `--branch`): where it ran, and the branch — `claustrum/<job_id>`, or the `--branch` name |
| `commit` | runner, git | isolated runs: the branch tip after the run when the run moved it — the runner's commit of what was left uncommitted, or the role's own; `null` when nothing changed, or when the commit failed (then `warnings[]` says the work was left uncommitted, and on which branch), or was skipped because the worktree's path was no longer the job's worktree on its branch |
| `cost_usd`, `usage` | backend | `null` for copilot and cursor, which report neither |
| `session_id` | backend | for `--resume` |
| `exit_code`, `duration_seconds`, `log_path` | runner | `log_path` is the job's `stdout.log` |
| `warnings[]` | runner | e.g. several report fences, last one won; work left uncommitted on an isolated run's branch |
| `raw` | backend | only with `--raw` / `include_raw` |

Renames arrive as an `A` plus a `D`; a non-git working directory falls back to an mtime/size scan
with `diff: null`.

### Report schemas per role

Every role must end its final message with exactly one fenced block tagged `claustrum-report`:

- **builder**: `{status: done|partial|blocked, summary, files_changed[{path, why}], behaviour_to_cover[], open_decisions[], shaky[], departed_from_brief[]}` — `shaky` is what the coordinator reads first.
- **code-reviewer / ui-reviewer**: `{status, findings[{severity, verdict: CONFIRMED|PLAUSIBLE, file, line, scenario, steps, evidence}], would_change_if_broader[]}`, most severe first; an empty list is a legitimate clean result.
- **tester**: `{status, commands_run[], passed, failed[{test, root_cause, fault_in: test|code}], skipped[]}`.
- **architect**: `{status, summary, delegations[{role, tier, job_id, status}], branch, closes[], findings_open[], open_decisions[], shaky[]}` — for a spawned architect this block is the entire result the caller sees.
- **demo-author**: `{status, deck, commit, steps, dataset{rung, kind, mechanism}, shareable, video, gitignore, left_behind[], defects_seen[], gaps[]}` — `deck` is the path actually used in the main checkout (a `-<short commit>` sibling, explained in `gaps`, when the planned directory is or was tracked), `video` is recorded by default (a `null` needs its reason in `gaps`), `gitignore` names the line and where it went — `docs/demos/` in a root `.gitignore`, left uncommitted for the orchestrator, plus `info/exclude`; any other directory in `info/exclude` only, nothing to commit; `already ignored`; or `outside every checkout` — and a deck directory the main checkout still does not ignore is `blocked` with no deck file written (`gitignore` still lists any ignore line that was); `shareable: false` makes the deck and the video internal.

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
  "backends": { "copilot": { "path": "/opt/copilot/bin/copilot" } },
  "defaults": { "timeout_seconds": 1800, "budget_usd": 5, "env_passthrough": "allowlist" },
  "jobs":     { "keep_last": 200 }
}
```

`backends.<name>` takes `path` and `injection`; `doctor` shows `injection`, but no backend reads it
— `claude` always injects with `--append-system-prompt-file`. There is no JSON schema file, and a key
Claustrum does not know (a `$schema`, say) is ignored rather than refused.

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
`CLAUSTRUM_PARENT_JOB` (membership in a tree, §10), `CLAUSTRUM_SKIP_PROBE=1`, `CLAUSTRUM_DEBUG=1`
(print the stack trace behind an unexpected `error:`), `CLAUSTRUM_NO_SPLASH` (any value: no logo on a
bare `claustrum`, as with `NO_COLOR` or a redirected stdout), and the three config overrides above.
`CLAUSTRUM_SMOKE_MODEL_<BACKEND>` and `CLAUSTRUM_SMOKE_COORDINATE_MODEL` choose the smoke scripts'
paid rows; only the scripts read them, never the binary.

Keys never go in `claustrum.json` or a cast: both are committable. `doctor` prints only whether a
variable is present.

## 8. Casts

`.claustrum/casts/<name>.json` in the working directory (like role overrides, not the git root),
committable, shared with the team:

```json
{ "name": "default", "library": "1.0.0",
  "architect": { "mode": "host" | "spawned", "model": "<alias|backend:id>" | null, "tier": "high|xhigh|max" | null },
  "roles": {
    "builder":       { "model": "standard-coding", "backend": null, "tier": null, "max_parallel": 2 },
    "code-reviewer": { "model": "frontier-coding", "backend": null, "tier": "xhigh", "max_parallel": null },
    "demo-author":   null,
    "tester":        { "model": "standard-coding", "backend": null, "tier": null, "max_parallel": null },
    "ui-reviewer":   null },
  "budget_usd": 15 }
```

- `architect.mode`: `host` — the agent you chat with adopts the role; `spawned` — `coordinate` runs
  it headlessly on `architect.model`.
- A role set to `null` is "not needed": an architect under that cast neither delegates to it nor
  does its work.
- `max_parallel` on the builder caps how many builders run at once, at any number; above 1 it also
  turns on worktree isolation for every builder run (§9).
- `budget_usd: null` disables the cap (`cast show` prints it as `"budget_usd": null`); a per-call
  `--budget` still applies.
- Per-call `--model`/`--backend`/`--tier` flags override the cast for that call.

**The questionnaire** is owned by the tool so it is identical in every host: `claustrum cast
questions --json` returns one question per library role, then the builders' parallel cap and the
budget, with their live options (the aliases in `claustrum.json` that land on a harness the role
lists; `not needed` where allowed; free-form `backend:id`; `no cap` for the budget):

| key | prompt | allows |
|---|---|---|
| `architect` | `host` or `spawned on <alias>` | free form |
| `builder` | model for the builder | free form |
| `code-reviewer`, `tester`, `ui-reviewer`, `demo-author` | model for the role — claude aliases only for `ui-reviewer` and `demo-author` | free form, `not needed` (for `demo-author`, that is the "no demo" switch) |
| `builder_max_parallel` | `1`, `2`, `3`, … | free form |
| `budget` | USD, or `no cap` | free form |

Answer them with `claustrum cast new` on a terminal, `claustrum cast create --answers file.json`
from a script, or `/claustrum` in a chat host (the synced skill asks with the host's own picker,
then calls `cast_create`). An empty option list means no `claustrum.json` alias lands on an installed
backend the role runs on — on a machine without `claude`, the demo-author and ui-reviewer have none;
`cast questions` and `cast new` say so and still offer `not needed`. All three refuse an answer that
puts a role on a harness it does not list, naming the role and its harnesses (`cast new` asks again
on a terminal, and exits 2 when its input is piped, rather than shifting later answers). Because
they load `claustrum.json` to resolve aliases, a malformed one makes `cast create`/`cast_create`
fail even when no answer uses an alias.

## 9. Parallel builders

**The cap.** A numeric `max_parallel` is a cap at every value, 1 included: a per-cast semaphore
shared by the CLI and the MCP door. A job past the cap **waits** for a slot, up to its `--timeout`
(default 1800 s), and then comes back `status: failed` with `all N '<cast>__<role>' slots … stayed
unavailable`, having done nothing. At 1 the runs queue and each works in the working directory
itself.

**Isolation** starts at 2, or with `--branch`. Each such job runs in `git worktree add
.claustrum/worktrees/<job> -b claustrum/<job>` from the repo's `HEAD` — with `--branch <name>`,
`git worktree add .claustrum/worktrees/<job> <name>` on that existing branch instead.
`changed_files`/`diff` are computed inside the worktree and the receipt carries `worktree`,
`branch` and `commit`. The checkout runs the repo's smudge filters and post-checkout hook, so it
gets 5 minutes; one that fails, outlasts them or is cancelled is undone — a `-b` branch it cut
included — before the run reports or exits, so no half-made worktree stays registered. Isolation
needs a git repository: outside one, a run its `max_parallel` would isolate runs in the working
directory instead, queued one at a time as at 1, and its receipt warns `no git repository at <cwd>:
ran in place, no worktree, no commit`; `--branch` there is refused.

**An isolated run stays in its worktree.** Its prompt ends with a trailer naming the worktree, its
branch and the main checkout it must never touch, and its deny list gains `git checkout` and
`git switch` — enforced natively on claude, opencode and copilot at every level that applies a deny
list (not `full`), a prompt rule on cursor. The deny is a guard on how a command is written (§13):
`git -C <main checkout> checkout …` is not matched, which is why the trailer is there too.

**The runner commits the work.** When an isolated run ends — whatever its status, as long as the
backend ran — the runner commits everything left uncommitted in the worktree on its branch:
subject `claustrum <role> <job_id>`, the report's `summary` as the body, the repo's own git
identity and hooks. Claustrum's own `.claustrum/worktrees/`, `.claustrum/briefs/` and
`.claustrum/locks/` are never part of it, ignored or not. What `git add` leaves in the worktree
is left out of the commit, the rest is committed, and each path left out gets a warning of its own
— read back from git's index, never from its messages, so a translated git words nothing differently:

- a directory with its own `.git` — a clone, a `git init` with no commit, a hand-made worktree —
  would be committed as a gitlink nobody can check out: `embedded repository at <path> left out of
  the commit on <branch> — move it out or add it as a submodule`. A gitlink whose path the staged
  `.gitmodules` lists is a deliberate submodule and is committed;
- changes inside an initialised submodule, which only a commit in the submodule can take: `changes
  inside submodule <path> left out of the commit on <branch> — commit them in the submodule, then
  stage its pointer`;
- anything else git would not stage — a path outside a sparse-checkout, an unreadable file, one a
  clean filter refuses: `<path> left out of the commit on <branch>: git did not stage it
  (sparse-checkout, permissions or a filter — see the job's stderr.log)`, where git's own words are
  appended (`claustrum jobs logs <job_id> --stderr`).

All of it holds whatever `diff.ignoreSubmodules` says. When nothing at all could be staged and a
path of the last kind was left out — a stale `index.lock` stops `git add` outright — the receipt
also says `work left uncommitted on <branch>: git add staged nothing: <git's line>`.
`commit` on the receipt is the branch tip whenever the run moved it, so a role
that committed by itself is recorded too, and `changed_files`/`diff` are then the branch's delta
from the commit the run started on: what was committed — by the runner or by the role — and
whatever is still uncommitted, left-out paths included. A commit git refuses (no identity, a failing
hook) leaves the work in the worktree, staged, and a `warnings[]` entry `work left uncommitted on
<branch>: …`; the run's status is untouched. The runner commits only in a directory that is still
the job's worktree on its branch:
one whose worktree was removed under it (a `jobs clean` from another `CLAUSTRUM_HOME`) is, to git,
part of the main checkout, so the runner commits nothing, reports no changes and no `commit`, and
warns `work left uncommitted: <path> is not the job worktree on <branch> (…)`. A worktree that is
still the job's but no longer on its branch — the role stopped mid-rebase (a detached `HEAD`) or
switched it — gets no commit either; its receipt keeps the changes read inside it, `commit` is
null, and it warns `work left uncommitted: <path> is on <a detached HEAD | refs/heads/…>, not
<branch>`. Nor does one still on its branch with a merge, a rebase or a `git am` in progress — a
commit would conclude it, and the runner never makes a merge commit, conflicted or not — or with
unresolved conflicts, whatever left them (a cherry-pick, a revert, `stash pop`, `merge --squash`,
`apply --3way`), whose markers `git add -A` would commit as the resolution: same receipt, and `work
left uncommitted: <path> has a <merge | rebase | git am> in progress, not a clean <branch>` or `work
left uncommitted: <path> has unresolved conflicts on <branch> — resolve or abort the operation
inside the worktree, then commit there yourself`. A clean `git revert -n` or `cherry-pick -n` is
neither: the runner commits it as an ordinary single-parent commit.

**Integration** is the architect's and it is by **rebase onto the work branch, then fast-forward**
— never a merge commit, never `git push` (every role's deny list). A builder's branch stays checked
out in its worktree until `claustrum jobs clean`, and git will not rebase a branch checked out
elsewhere (`fatal: '<branch>' is already used by worktree at '…'`): rebase it inside that worktree
(`git -C <worktree> rebase <work branch>`), then fast-forward the work branch to it from your own
working tree (`git merge --ff-only <builder branch>`). Never rebase in your own working tree — that
leaves it on the builder's branch, and an isolated architect may not `git switch` back — and run
`claustrum jobs clean` only once the builders are integrated. A builder whose receipt warns `work
left uncommitted on <branch>` has its work only in its worktree: commit it there first; the other `work left uncommitted:` warnings,
and the `left out of the commit` ones, are routed in §15. To send a reviewed builder back for a fix, run it
with `--branch <the branch on its receipt>` — `claustrum/<that job id>`, or the name a `--branch`
builder was given: it continues on the same branch in a fresh worktree, and the fix lands there as
another commit. A branch can be checked out in one worktree at a time, so the finished job's
worktree is removed first — only if it is clean (new files count, whatever
`status.showUntrackedFiles` says), and only when it is a `.claustrum/worktrees/<job id>` whose job
wrote its `result.json` under this `CLAUSTRUM_HOME`. A branch held by the main checkout, a running
job, a job this `CLAUSTRUM_HOME` has no directory for (one run under another home, or pruned by
`jobs.keep_last`: `claustrum jobs clean` under the home that ran it frees it) or anything else is
refused with the holder named. Runs on one branch never overlap: a second `--branch` run on it waits
for the first one's receipt (up to its `--timeout`), then takes the branch over from the finished
worktree. `claustrum jobs clean` removes the worktrees of finished jobs, never with `--force`, and
keeps the branches.

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
the same request `run` builds, for the `architect` role, and adds four things — the fourth, in a git
repository only:

1. **The cast appended to the system body** as a `## Coordination` section: the cast line by
   line ("builder: model …, tier …, max_parallel …", "ui-reviewer: not needed — do not delegate
   to it"), the budget across the tree, the delegate command (`claustrum run <role> --cast "<name>"
   --brief-file <path> --json --cwd "<dir>"`), the four `budget_exceeded` routes, "write each brief
   to `.claustrum/briefs/<n>-<role>.md`", and — in a git repository — the work branch
   **`claustrum/<job id>`** (below), into
   which each parallel builder's branch (which carries its work as a commit, unless its receipt warns
   the work was left uncommitted) is rebased — inside the builder's worktree — and fast-forwarded;
   never a merge, never a push. An isolated builder sent back for a fix runs with `--branch <the
   branch on its receipt>` (an in-place builder has no branch: its work is already in the working
   tree), and `max_parallel` is a cap at every value.
2. **`CLAUSTRUM_PARENT_JOB=<job id>`** in the architect's environment, so every `claustrum` it runs
   joins the tree.
3. **`gh issue view <n> --json title,body,labels`** folded into the brief's `## Task` (`--issues`),
   with the instruction that the resolving commit cites `Closes #<n>`.
4. **Its own worktree**, below.

**The architect works in its own worktree.** In a git repository `coordinate` runs it isolated,
like a parallel builder (§9): `git worktree add .claustrum/worktrees/<job id> -b claustrum/<job id>`
from `HEAD`, the isolation trailer and the `git checkout`/`git switch` deny, and when it finishes the
runner commits whatever it left uncommitted onto the work branch (`commit` on the receipt). **Your
checkout is never touched**: its branch and `HEAD` stay where they were, and uncommitted edits in it
stay there instead of riding along. The one thing that lands in it is a demo-author's deck, by that
role's own rule: gitignored, in `docs/demos/<feature>/`, where `jobs clean` cannot delete it. The
`## Coordination` section says the architect is already on
the work branch, that the main checkout is yours, and gives every delegation `--cwd "<its
worktree>"` — so an isolated builder's worktree nests under the architect's
(`.claustrum/worktrees/<job id>/.claustrum/worktrees/<builder id>`), cut from the work branch's tip,
and the slot locks live in the architect's worktree too. It integrates by rebasing inside each
builder's worktree and fast-forwarding its own: a rebase in its own worktree would leave it on the
builder's branch, with `git switch` denied. Outside a git repository there is no `HEAD` to branch
from: the architect works in the cwd, and one line of its `## Coordination` replaces every git
instruction — no work branch and no worktree isolation, builders run in place one at a time, their
changes land directly in the cwd, and there is nothing to rebase or clean. A builder whose cast says
`max_parallel` 2 or more runs in place there too, queued, with the `no git repository at …` warning (§9).

A worktree holds committed files only, and the architect's children read their cast, `claustrum.json`
and the role files of `.claustrum/roles/` from it — so `coordinate` refuses with exit 2, before any
job exists, a cwd that is not the repository root, any of those files that differs from `HEAD` (a
local edit hidden by `git update-index --skip-worktree` or `--assume-unchanged` included), a
`.gitignore` holding Claustrum's rules with any uncommitted change (the worktree gets `HEAD`'s rules,
whatever the edit is), an untracked `.mcp.json`/`opencode.json`/`opencode.jsonc` (what `claustrum
init` writes: commit it, or git-ignore it as a local copy), and a `.claustrum/{worktrees,briefs,locks}/`
that no committed rule ignores (`claustrum jobs clean` could not remove the architect's worktree, and
the architect's own `git add` would take in its builders' worktrees, briefs and locks). Commit them,
then rerun. A committed harness config with a local edit only warns: the worktree gets `HEAD`'s copy.
The runner's own commit leaves `.claustrum/{worktrees,briefs,locks}/` out whatever the rules say, and
leaves out — with a warning, committing the rest — any other directory that is a git repository of
its own and is not a submodule the staged `.gitmodules` lists (§9).

**Nothing uncommitted reaches the worktree**, and that includes what a gate may need: gitignored
test prerequisites (`.env`, `*.local.json`, `node_modules/`, build caches), submodules — the
worktree's are not initialised — and harness settings that are not committed (`.claude/settings*.json`,
an ignored `.mcp.json`, the local edit of a committed one). Every child of the architect runs there or
in a worktree nested in it, so a tester whose gate needs one of them fails for an environmental reason,
and only the harness configs' local edits are warned about before the run. Commit
what can be committed, make the gate set up the rest (restore, `submodule update --init`), or say so
in the brief.

**Cleaning up** is two sweeps, innermost first: `claustrum jobs clean --cwd .claustrum/worktrees/<job
id>` removes the builders' worktrees nested in the architect's, then `claustrum jobs clean` removes
the architect's own. In the other order the architect's worktree is left in place and the sweep says
which `--cwd` to run first. Branches stay, as always.

On `claude` the architect's native `Agent` tool is disallowed, so it cannot fan out outside the
cast. Everything that can be refused (flags, cast, uncommitted files, `gh`, the brief) is refused
**before** a job directory exists. The result is the architect's receipt: its
`report.data.delegations[]` lists every child with a job id, `branch` and `worktree` name the work
branch and where it is checked out, and `claustrum jobs budget <job id>` is the tree's ledger. It
leaves the work on the branch; pushing and the PR are yours.

From MCP, `coordinate` returns `{job_id, log_path, warnings}` immediately — `warnings` is the CLI's
pre-run `warning: …` lines, often empty; poll `job_status`, then
`job_result`.

## 12. Sync: one role library, every harness

`claustrum sync` renders the library into each harness's own format and registers the MCP server.
Generated markdown carries `<!-- claustrum:generated role=… harness=… library=… sha256=… -->`
as its first body line, with `source=local` before `sha256` when the role was overridden (§4);
generated JSON keys are tracked by hash in `.claustrum/sync-manifest.json`. A file without the
marker or a manifest entry is **never overwritten** (`--force` adopts it).

**A synced Claude agent carries no deny list today.** In agent frontmatter, `disallowedTools` cannot
hold a per-command deny: an entry with a specifier such as `Bash(git push *)` removes the whole
Bash tool ([code.claude.com/docs/en/sub-agents](https://code.claude.com/docs/en/sub-agents), read
2026-10-02). A frontmatter `PreToolUse` hook on `Bash` that exits 2 could carry it, scoped to that
agent; that is not implemented yet (a follow-up issue). Until then `sync` renders role.json `deny`
nowhere, a native agent — the builder's `git push`, the demo-author's git verbs — keeps it as prose
only, and the deny list is enforced only when a role runs through `claustrum run`/`delegate`.

role.json `withoutTools` names tools a role's level grants but it never uses (the demo-author's
`NotebookEdit`); a synced Claude agent leaves them out of `tools` and lists them under
`disallowedTools`. It reaches **synced Claude agent files only**: under `claustrum run` the claude
backend's `edit+shell` flags do not name it, and `acceptEdits` approves `NotebookEdit` like any
edit. A local `.claustrum/roles/<role>/role.json` override replaces the whole array, as it does
`tools` and `deny`.

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
| `claude` | `npm i -g @anthropic-ai/claude-code` | Claude Code login or `ANTHROPIC_API_KEY`; `CLAUDE_CONFIG_DIR` is passed through, and `doctor` prints it as `config dir:` | `--append-system-prompt-file <job>/system.md`; the brief fed on **stdin**, never argv | yes (`total_cost_usd`) |
| `opencode` | `npm i -g opencode-ai` | `OPENROUTER_API_KEY` / `DEEPSEEK_API_KEY` / `opencode auth` | inline agent in `OPENCODE_CONFIG_CONTENT`, process-scoped | from the JSONL usage events |
| `cursor` | Cursor's `cursor-agent` installer (not the npm package of that name) | `cursor-agent login` or `CURSOR_API_KEY` | role prefixed to the prompt, fed on **stdin**; `-f` always (an untrusted workspace is a fast exit 1) | **none**; usage tokens only |
| `copilot` | `npm i -g @github/copilot` | logged in (`~/.copilot/config.json`) or `COPILOT_GITHUB_TOKEN`/`GH_TOKEN`/`GITHUB_TOKEN` | per-job `.agent.md` via `--add-dir`, agent `claustrum-<role>` | **none** (`assistant.usage` is suppressed in JSON mode); `--reasoning-effort` is omitted for `auto` |
| `api` | `curl` | `OPENROUTER_API_KEY` / `ANTHROPIC_API_KEY` by model prefix | system message; **no tools**, `changed_files` always empty | yes |

Permission levels and what each backend does with them:

| Level | Roles | `claude` | `opencode` | `cursor` | `copilot` |
|---|---|---|---|---|---|
| `readonly` | code-reviewer | `--permission-mode plan` + read-only tool list, plus the same deny rules as `edit+shell` (its `gh pr *` would otherwise allow `gh pr merge`) | edit/bash deny | prompt rule (advisory) | `--mode plan --deny-tool write --deny-tool shell` |
| `shell` | ui-reviewer | plan + `--disallowedTools Edit,Write,NotebookEdit`, MCP tools reachable | edit deny, bash allow with deny patterns | prompt rule | `--mode plan --deny-tool write` + deny patterns |
| `edit` | — | `acceptEdits` + `--disallowedTools Bash` | edit allow, bash deny | prompt rule | `--allow-all-tools --allow-all-paths --deny-tool shell` |
| `edit+shell` | architect, builder, tester, demo-author | `acceptEdits`, built-in tools + `Bash(*)`, `--disallowedTools "Bash(<deny>),Bash(<deny> *)"` per deny entry; MCP tools are outside the allow-list | edit allow, bash allow, `<deny>` and `<deny> *` deny | prompt rule | `--allow-all-tools --allow-all-paths --deny-tool "shell(<deny>)"` per entry |
| `full` | — | `--dangerously-skip-permissions` | `--auto` | `-f --sandbox disabled` | `--allow-all` |

Where a harness has no native deny mechanism (cursor) the deny list is a rule in the prompt, and
`doctor` calls it advisory. Everywhere else a deny entry is a guard on how a command is written,
not a sandbox — Claude Code's own docs say a Bash rule "isn't a security boundary around the
program" ([permissions](https://code.claude.com/docs/en/permissions#bash-rule-limits)). It does not
see `git -C <dir> commit`, `git -c k=v commit`, a quoted verb, a user alias (`git ci`), an absolute
path to git, `sh -c "…"`, or a script that shells out to git; a verb the list does not name is
simply allowed. Claude Code does split compound commands, so `cd x && git commit` is caught. Each
entry matches its verb as a word, bare or with arguments: claude's `Bash(git merge)` +
`Bash(git merge *)` and opencode's `"git merge"` + `"git merge *"` stop `git merge` and
`git merge main` but not `git merge-base`; copilot's `shell(git merge)` matches the first-level
subcommand exactly. This applies to every deny list, `--deny` and `claustrum.json`
`roles.<role>.deny` included, and it narrows entries written for the old prefix meaning: `rm -rf`
no longer stops `rm -rfv build`, and `git push --force` no longer stops `--force-with-lease`. Add
the longer form as its own entry, or end the entry with `*` (`git push --force*`) to keep a prefix
match on claude and opencode; copilot's `shell(…)` matches by subcommand, not by prefix, either way.
Claustrum launches backends on the OS it runs on and never translates paths: a WSL binary
drives Linux harnesses against Linux paths, a Windows binary drives Windows ones; `doctor --probe`
warns when binary and working directory straddle that line.

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
| exit 2, `coordinate runs the architect in a worktree, which sees only committed files — commit (or un-ignore) <path> (<state>) first` | the cast, `claustrum.json` or a role file under `.claustrum/roles/` (`<role>/role.json`, `<role>/ROLE.md`, `<role>/parts/*.md`) differs from `HEAD` — modified, staged, untracked or ignored — or hides a local edit (`skip-worktree`, `assume-unchanged`), and the architect's worktree would not see it | commit it (un-ignore a cast the repo ignores; `git update-index --no-skip-worktree`/`--no-assume-unchanged <path>` first for a hidden edit), then rerun; nothing was spent |
| exit 2, `… — .gitignore (<state> — the worktree gets HEAD's ignore rules; commit it first)` | the file holding Claustrum's `.claustrum/` ignore rules has an uncommitted change, related to them or not: the worktree gets `HEAD`'s copy, so what was checked is not what it will see | commit it (or revert the edit), then rerun |
| exit 2, `… — commit or git-ignore .mcp.json (untracked) — an ignored local copy is fine` | a harness config `claustrum init` writes (`.mcp.json`, `opencode.json`, `opencode.jsonc`) is in no commit, so the architect's harness would start without it | commit it, or git-ignore it if it is meant to stay local (the architect then runs without it) |
| `warning: .mcp.json (<state>): the architect's worktree gets HEAD's copy — your local edit stays out of it` (stderr before the run; MCP `warnings`) | a committed harness config has a local edit — plain, or hidden by `skip-worktree`/`assume-unchanged` — and the architect and its children read the committed one | nothing, if the committed copy works; otherwise commit the edit, or stop the run and move the setting somewhere committed |
| exit 2, `… git-ignore .claustrum/worktrees/, … in a committed .gitignore first` | no committed rule ignores Claustrum's working directories (as written: a whitelist `.gitignore` with `*` and `!*/` un-ignores a worktree directory), so `jobs clean` could not remove the architect's worktree and its own `git add` would take in its builders' worktrees, briefs and locks | `claustrum init` appends the rules; commit `.gitignore`, then rerun |
| exit 2, `coordinate runs the architect in a worktree of the whole repository …: run it from <root>` | `coordinate` ran in a subdirectory of the repository | run it from the repository root, with the cast in `.claustrum/casts/` there |
| exit 2, ``role '<r>' runs on … only (its role.json `harnesses`)`` | the cast, `claustrum.json` or `--backend` sent the role to a harness it is not written for (the demo-author and ui-reviewer are claude-only) | point the role at an alias on one of its harnesses, mark it `not needed` in the cast, or — if the repo ships its parts for that harness — list it under `harnesses` in `.claustrum/roles/<role>/role.json` |
| exit 3 | the backend's binary is not on `PATH` | install it, or `backends.<name>.path` in config |
| `Error: Model "opus" … is not available` (copilot, cursor) | the built-in `claude:*` alias reached a non-claude backend | alias the class to `copilot:auto` / `cursor:auto` |
| `api backend model must be 'openrouter:<model>' or 'anthropic:<model>'` | `--backend api` without a provider-prefixed model | `--model api:anthropic:<id>` |
| `report_status: missing` | the role never wrote its report fence | read `final_message`; the run still counts, the report does not |
| `status: failed`, "… slots … stayed unavailable" | waited past `--timeout` for a `max_parallel` slot | start fewer at once, or raise `--timeout` |
| `status: failed`, "`--branch <name>`: …" | the branch does not exist locally, another checkout holds it (the main checkout, a running job, a dirty finished worktree, a job unknown under this `CLAUSTRUM_HOME`), git would not check it out or did not within 5 minutes (a slow smudge filter or post-checkout hook), or another run on the branch outlasted `--timeout` | read `error`: it names the holder, or quotes git; switch that checkout off the branch, let the job finish, commit/discard its changes, or run `claustrum jobs clean` under the home that ran the job; after a slow checkout, `left behind: …` names anything the undo could not remove (a cancelled add prints it on stderr as `warning: git worktree add was cancelled; left behind: …`) — `git worktree remove -f -f <path>` removes a worktree git left locked |
| `status: failed`, "`--branch <name>`: its previous worktree <path> holds a populated submodule, which git will not remove — …" | the finished job's worktree on that branch is clean but has a submodule checked out, and git refuses to remove any worktree that does | a commit made inside that submodule and pushed nowhere lives only in that worktree's git directory, so push or copy it first; then `git worktree remove --force "<path>"` and rerun |
| `warnings[]`: "work left uncommitted on <branch>: …" | git refused the runner's commit of an isolated run (no identity, a hook), or — `… git add staged nothing: <git's line>` — `git add` staged nothing at all (a stale `index.lock`, or every path left out) | the work is still in the job's worktree: fix what git names, then commit it there; `jobs clean` leaves that worktree in place until you do |
| `warnings[]`: "embedded repository at <path> left out of the commit on <branch> — move it out or add it as a submodule" | the role left a directory with its own `.git` (a clone, a `git init` with no commit, a hand-made worktree) that `.gitmodules` does not list; the rest of its work is committed on the branch | the repository is still in the job's worktree: move it out, or `git submodule add` it there and commit; `jobs clean` leaves that worktree in place until you do |
| `warnings[]`: "changes inside submodule <path> left out of the commit on <branch> — commit them in the submodule, then stage its pointer" | the role changed files inside an initialised submodule and committed nothing there (or moved a pointer its `.gitmodules` hides with `ignore = all`); the rest of its work is committed on the branch | in the job's worktree: commit inside `<path>`, then `git add <path>` and commit; `jobs clean` leaves that worktree in place until you do |
| `warnings[]`: "<path> left out of the commit on <branch>: git did not stage it (sparse-checkout, permissions or a filter — see the job's stderr.log)" | `git add` would not stage that path — outside the worktree's sparse-checkout cone, unreadable, or refused by a clean filter (git-lfs); the rest of the work is committed on the branch | read git's words: `claustrum jobs logs <job_id> --stderr`; in the job's worktree fix the cause (`git add --sparse <path>`, the file's permissions, the filter), then commit it there; `jobs clean` leaves that worktree in place until you do |
| `warnings[]`: "work left uncommitted: <path> is not the job worktree on <branch> (…)" | the job's worktree was removed while it ran — typically a `jobs clean` under a different `CLAUSTRUM_HOME` — and the role kept writing into a plain directory | nothing was committed, and the receipt lists no changes and no `commit`; what the role wrote after that is in `<path>`, outside every branch: move it by hand, or rerun the builder |
| `warnings[]`: "work left uncommitted: <path> is on …, not <branch>" | the role left its own worktree off its branch — stopped mid-rebase (a detached `HEAD`) or switched branch | nothing was committed; `changed_files`/`diff` are what changed in that worktree. Finish or abort the rebase there (`git -C <path> rebase --continue`/`--abort`), or switch back to `<branch>`, then commit inside it |
| `warnings[]`: "work left uncommitted: <path> has a … in progress, not a clean <branch>" | the role left a merge (conflicted or not), a rebase or a `git am` in progress on its own branch, and the runner's commit would have concluded it — a merge commit, or a commit in the middle of a rewrite | nothing was committed; `changed_files`/`diff` are what changed in that worktree. In `<path>`, abort a merge (`git merge --abort`: integration never makes a merge commit); finish or abort a rebase or `git am` (`--continue`/`--abort`); then commit what is left inside it |
| `warnings[]`: "work left uncommitted: <path> has unresolved conflicts on <branch> — resolve or abort the operation inside the worktree, then commit there yourself" | the role left conflicted paths on its own branch — a cherry-pick, revert, `stash pop`/`apply`, `merge --squash` or `apply --3way` stopped on a conflict — and `git add -A` would have committed their conflict markers as the resolution | nothing was committed; `changed_files`/`diff` are what changed in that worktree. In `<path>`, `git status` names the paths: fix them and `git add` them, or abort what made them (`git cherry-pick --abort`, `git revert --abort`, `git reset --merge` for the others — a conflicted `stash pop` keeps its stash); then commit inside it |
| `warnings[]`: "receipt delta unavailable, snapshot kept: …" | after the runner's commit, git could not read the branch's tip or diff it against the run's starting commit (a git past its 30 s bound) | `commit`, when set, is right and nothing is left to commit; `changed_files`/`diff` are the snapshot from before the commit, which can miss what the role committed itself — read the branch (`git log -p <branch>`) |
| `jobs clean`: "could not remove <job>: <path> left in place: …" | that worktree has uncommitted changes (new files included), or a populated submodule | commit or discard them (`git -C <path> status --untracked-files=all`), then rerun; `git worktree remove --force <path>` only once you know what it drops |
| `jobs clean`: "… left in place: it holds job worktrees of its own under …" | a `coordinate` architect's worktree still holds its builders' worktrees | `claustrum jobs clean --cwd <that worktree>` first (the message names it), then rerun |
| `fatal: '<branch>' is already used by worktree at '…'` on `git rebase` | a builder's branch is still checked out in its worktree | `git -C <worktree> rebase <work branch>`, then `git merge --ff-only <branch>` on the work branch (§9) |
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
