# Claustrum manual

The reference for using Claustrum — every verb, file, field and rule, in one place. It restates
what the code does today (binary `0.2.0`, role library `1.1.0`; checked against the code
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
| **cast** | a per-developer file (`.claustrum/casts/`, git-ignored) saying who plays which role, how many builders run at once, and the budget; model aliases live in the user config |
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
| `job_status` | — | `{state: running\|waiting_for_input\|abandoned\|done\|failed, elapsed_seconds, last_line, question: {seq, text} \| null}` |
| `job_say` | `claustrum jobs say` | `{answers_question, state}` — answers the open question, or messages the running role (§3, *Talking to a running job*) |
| `job_result` | `claustrum jobs show` | the receipt, once done (throws before) |
| `coordinate` | `claustrum coordinate` | `{job_id, log_path, warnings}` — always async; poll `job_status` (the architect runs live: `waiting_for_input` is its question to you, answered with `job_say`) |
| `list_roles`, `list_backends`, `doctor` | `roles list`, `backends list`, `backends doctor` | summaries |
| `cast_questions`, `cast_create`, `cast_list` | `cast questions`, `cast create`, `cast list` | the questionnaire / the written cast / names |

The server's configuration root is the directory it was launched in (hosts launch MCP servers in
the workspace); `--cwd` on the server and `cwd` on each call override it.

## 3. CLI reference

Exit codes are fixed: `0` ok · `1` backend failure · `2` usage or config error (a bad flag, a
malformed job id (every `jobs` verb takes only `yyyyMMdd-HHmmss-xxxxxxxx`), a refused brief, a missing cast, a role sent to a harness it does not list, a `gh` failure, a
`coordinate` whose untracked cast no committed rule ignores, a `.claustrum` link that leaves the repo, an
uninitialized repo) · `3` backend not found · `4` timeout · `5` budget · `6` abandoned (`jobs wait`, `jobs reply`, `jobs cancel`,
and a tree's `run`: the process running a job is gone, with no receipt; under `--json` the one document is then a RunResult
with `status: failed` and an `error` saying so) · `7` needs input (a tree's `run` or `jobs reply`: a child
asked its architect a question and still waits, *Questions inside a tree*; and any finished run whose report said `needs_input`, *When nobody can answer live*) · `130` cancelled. Once the job directory exists (queued for a `max_parallel` slot, waiting for a `--branch`
lock, adding its worktree, or running), the first Ctrl-C (SIGINT), SIGQUIT, SIGTERM or SIGHUP (the terminal closing) ends the run
with a `cancelled` receipt: exit 130, the document on stdout under `--json`, `result.json` on disk
(#94). A cancel while queued or adding leaves no worktree, directory or branch (#108: the job directory is made
by Claustrum itself before git runs, and deleted again if git never registered it; the only gap is git killed in the
microseconds between writing its `gitdir` and `.git` files, which can leave a stray locked entry, see NOTES); one just after the add, before
the backend starts, leaves the registered worktree and its branch at the starting commit (`jobs
clean` removes the worktree later, never the branch); one while running keeps both with a commit of
any work so far (§9). Before the job directory exists nothing is minted: a signal while
`run`/`coordinate` is still reading its brief from stdin gets the runtime's default exit (130 for
Ctrl-C, 143 for SIGTERM, 129 for SIGHUP, no handler yet); one during `coordinate`'s planning (while `gh` runs) is
exit 130 with no receipt. A second Ctrl-C, SIGQUIT or SIGTERM ends the
process at once with the runtime's own code (130, 131, 143), no receipt, and the backend may keep running (it received no signal of
its own when the sender was `kill`). **SIGHUP never forces**: closing a terminal delivers two within a fraction of a
millisecond (the shell's hangup, then the kernel's to the foreground group), so a later SIGHUP is absorbed and the first one's
cancel runs to its receipt. The backend shares Claustrum's process group, so a terminal's SIGHUP or Ctrl-C (or a
`kill -- -<pgid>`) can kill it before the cancel arrives: a backend that dies by SIGHUP, SIGINT, SIGQUIT or SIGTERM
(exit 129, 130, 131, 143) and a cancel that arrives within one second still read as `cancelled`; without one it is a failed
backend. So does any `git` call Claustrum makes for the run (the snapshot, the worktree add, the commit). Once the terminal is gone, `--stream` echo and the final output are best-effort: a failed write is dropped, the
receipt is on disk first and the exit code is unchanged. On Windows Ctrl-C and Ctrl-Break take the cancel path (both reach
`Console.CancelKeyPress`; not measured on Windows), SIGTERM registers but cannot be cancelled there, and
SIGHUP is the console-close event (the window closing): it starts the same cancel (or, after an earlier Ctrl-C or SIGTERM, joins it) and holds the handler for up to 4 s, until the
receipt is written (`run`, `coordinate`) or the sweep has stopped (`jobs clean`), because Windows ends the
process once the handler returns (not measured); the forced-exit codes above are the Unix ones.

### `claustrum run <role>`

```
claustrum run <role> [--brief <text> | --brief-file <path|->]
    [--cwd <dir>] [--backend <name>] [--model <alias|backend:id>] [--effort <e>] [--tier high|xhigh|max]
    [--permission readonly|shell|edit|edit+shell|full] [--deny <pattern>]…
    [--budget <usd>] [--timeout <sec>] [--resume <session>] [--file <path>]… [--env K=V]…
    [--cast <name>] [--branch <name>] [--json] [--stream | --stream-raw] [--raw]
    [--reply-timeout <sec>] [--no-live] [--detach]
```

- The brief is mandatory: `--brief`, `--brief-file`, or `--brief-file -` for stdin.
- Precedence for model and tier: explicit flag > cast entry > role's tier default > built-in
  aliases. `--backend` alone changes the harness and keeps the model id, which is rarely what you
  want (`opus` is not a copilot id): prefer `--model backend:id`.
- `--cast` defaults to `.claustrum/casts/default.json` when that file exists. A cast role with a
  numeric `max_parallel` — 1 included — caps how many of its runs go at once; above 1, **every**
  builder run gets its own worktree and branch (§9).
  Two kinds of warning reach `warnings[]` in the result and stderr. The **cast** warnings fire only
  when the model came from the cast (no `--model` or `--backend` flag): the alias silently fell
  through to a built-in default (`no claustrum.json or user config defines it`), or its stored
  `resolved` value no longer matches what it resolves to now — so an alias that once meant one model
  and now means another is never invisible. An explicit `--model`/`--backend` flag suppresses
  those two. The **role-level** warning (`role '<r>': model '<m>' resolved to the built-in default …;
  <user config> does not define it`) fires whenever the role's final model — flags, cast, `roles`
  config and the tier's class — reached a built-in default through an alias, flag or no flag, unless a
  cast warning already said so for that role; a `--model backend:id` names no alias and never triggers it.
- A cast role's `fallback` (§8) answers a provider that refuses the role's model before it starts: the run is retried
  once, as a new job, on the fallback, and the receipt returned is the retry's, with `warnings[]` saying why. An
  explicit `--model`/`--backend` is never retried — you chose that model.
- `--branch <name>` runs isolated in a new worktree checked out on that **existing local branch**,
  whatever the cast's `max_parallel` — e.g. `--branch claustrum/<job id>` to send a reviewed
  builder's branch back for a fix (§9). Two runs on one branch run one after the other: the second
  waits for the first's receipt, up to its `--timeout`. An unknown name, a branch checked out
  anywhere but the clean worktree of a job finished under this `CLAUSTRUM_HOME`, one git itself
  will not check out (mid-rebase in another worktree) or does not check out within 5 minutes, or a
  wait for the branch that outlasts `--timeout` comes back `status: failed` (exit 1, a receipt like
  any other) with the reason — and the holder, if any — in `error`, and no worktree left behind
  (`error` names anything the undo could not remove). The free of the previous job's worktree never
  uses `git worktree remove`: once Claustrum's own checks pass (clean, new files included; no submodule
  populated; not `git worktree lock`-ed; on an existing branch (`refs/heads/…`, not a detached HEAD) with no rebase, merge,
  cherry-pick, revert or bisect under way; no commit only its own HEAD reflog or its own `refs/worktree`, `refs/bisect` or
  `refs/rewritten` refs hold (the branch's reflog counts as kept); no `skip-worktree`/`assume-unchanged`
  file present on disk; and really this repository's worktree, not a directory the role re-initialised with its own
  `.git`), the directory is renamed to
  `.claustrum/worktrees/.trash-<job>-<random>` and its own registration (`.git/worktrees/<id>`, never a repo-wide
  `git worktree prune`: a worktree of yours on an unmounted drive keeps its registration) is deleted; the trash is then
  deleted. It needs git 2.31 or later, and reads `worktree.useRelativePaths` registrations. A branch held by a registration whose `.git` file is gone is released only if the
  directory is gone too; a directory that still exists is left, and the branch refused. A Ctrl-C, SIGTERM or SIGHUP before the rename leaves the worktree
  intact and the run ends `cancelled`; after it, only trash and perhaps a registration the next `--branch` run or
  `jobs clean` drops. A SIGKILL is the same. A worktree that fails a check is a refusal naming it, with `--branch`
  failing as the holder's `left in place (…)`; resolve what it names (commit, switch back, finish or abort the
  operation, clear the mark) and retry. A trash directory that would not delete, or a registration git still lists
  after the rename (`<path> freed, but … (trash at …; `claustrum jobs clean` removes it)`), is a `warnings` entry on
  the receipt, never a failure: the free completed. Nothing is ever forced.
- `--detach` validates and prepares exactly as `run` does (everything that can be refused is refused before a job exists), mints the
  job, starts a detached `claustrum jobs host <id>` process that runs it to its receipt, and exits 0 at once: the id on stdout, or
  under `--json` `{"status":"started","job_id":"…"}`. The host has the environment, working directory and `CLAUSTRUM_PARENT_JOB`
  `run` had, so the env allow-list, the budget tree, the cast, isolation and the `max_parallel` slot (which the *host* acquires) behave
  as for an in-process run; it outlives `run`, its terminal and its session (a session of its own on Unix, a console that is never
  shown on Windows) and writes what it prints to `host.log` in the job directory. Watch it with `jobs follow <id>`, talk to it with
  `jobs say <id>`, wait with `jobs wait <id>`, stop it with `jobs cancel <id>`. `--detach` with `--stream` is a usage error. A
  `--file` that is not a regular file (`<(cmd)`) is read by the host after `run` has exited: give a detached job a real file.
  Its reply timeout follows `run`'s rule (1800 s when `run` was started from a terminal or in a tree, else 0); `--reply-timeout` sets it.
- `--stream` echoes formatted progress lines to **stderr** as the job runs
  (`HH:mm:ss LABEL  detail…`; whitespace folded to one space, control characters shown as `\x1b`-style
  escapes, so model text cannot move the cursor or retitle the terminal; claude's final `result` adds a
  `total $…` line); `--stream-raw` (implies streaming) echoes the raw backend JSON unchanged. Both are
  plain flags and parse in any position. stdout always carries only the final document. `--raw` adds the backend's untouched
  response under `raw`.
- Without `--json`, stdout gets `status:`, then `branch:`/`worktree:`/`commit:` when the run was
  isolated, the changed files and the final message; every warning goes to stderr as
  `warning: …`, and the error as `error: …`.

### `claustrum coordinate`

```
claustrum coordinate [--cast <name>] (--issues 12,13 | --brief <text> | --brief-file <path|->)
    [--cwd <dir>] [--tier …] [--model <architect model>] [--budget <usd>] [--timeout <sec>]
    [--json] [--stream | --stream-raw] [--raw] [--reply-timeout <sec>] [--no-live]
claustrum coordinate --resume <job> (--answers <file> | --brief <text> | --brief-file <path|->) [--tier …] [--model …] [--budget <usd>] [--timeout <sec>]
```

Runs the cast's **architect** headlessly (§11). `--resume` continues one that stopped with `needs_input` (*When nobody can answer live*). `--issues` needs `gh` authenticated and a GitHub
remote; a failure there is exit 2 and nothing is spent. It never reads a bare stdin: `--brief-file -`
is the only way to pipe. Its `--budget` caps the architect's own run; the cast's `budget_usd` caps
the children.

In a git repository the architect works in its own worktree, which holds committed files only, so
`coordinate` exits 2 — before any job exists, nothing spent — naming what to fix when: the cwd is
not the repository root; the cast file is untracked or ignored and no committed `.gitignore` rule
ignores `.claustrum/casts/` (`run 'claustrum init' and commit .gitignore first`); `claustrum.json` or a role file under `.claustrum/roles/`
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
warnings in its `warnings` array. `--stream` formats progress to stderr;
`--stream-raw` echoes the raw JSON. Without `--json`, stdout adds `branch:`, `worktree:` and `commit:`
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
part into a named skip. A repo MCP config that is not a regular file is reported `present, not a regular
file` and never read. The probe needs a `claustrum.json` at the git root whose aliases reach the
backend, or it skips with "no model alias … resolves to this backend".

### `claustrum jobs list | show <id> | logs <id> [--stderr] | follow [<id>] [--raw] | say <id> <text> | wait <id> [--timeout <s>] [--json] | reply <id> <text> [--json] | answer <id> (--answers <file> | <text>) [--json] | cancel <id> | clean [--cwd] | budget <tree> [--reset]`

- `list`: recent jobs, newest first — id, status, `role/backend`.
- `show`: the receipt (`result.json`), or the request if it has not finished.
- `logs`: the captured stdout (readable while the job runs); `--stderr` for the other stream.
- `follow [<id>]`: follow a job's output as it runs. With an id, prints a header line (job id,
  role, `backend:model`), then streams the job's `stdout.log` from the start, formatted per its
  backend's stream formatter (`HH:mm:ss LABEL  detail…`, sanitized as above). A line that has no time of
  its own and was already in the log when the follow began (claude's stream has none) shows `--:--:--`;
  lines that arrive afterwards show their arrival time. Polls every ~500 ms. When the job finishes
  and `result.json` appears, prints a summary line (`status`, cost, duration, `backend:model`,
  warnings count) and exits 0. Without an id, follows every job that starts after the command, each
  line prefixed with the short job id, until Ctrl-C (exit 130). `--raw` shows unformatted lines.
  Works while the job's terminal is gone: open a second terminal and run
  `claustrum jobs follow <job id>` to watch progress from there. A job whose claustrum process died
  before writing `result.json` is followed until Ctrl-C: there is nothing to tell follow it ended.
  A live job's conversation is printed beside the stream (`? `, `> `, `! `, below), and on a terminal
  an open question is asked on the spot.
- `answer <id>`: continues a finished job that stopped with `needs_input` (*When nobody can answer live*, below).
- `say <id> <text>` and `wait <id>`: the way into a live job, below. `reply <id> <text>` is `say` and then the wait a tree's `run`
  makes (*Questions inside a tree*). `cancel <id>` ends a detached job: it leaves a marker in the job directory, which the host
  polls (every 250 ms) and answers by ending the job `cancelled`, with its receipt, as Ctrl-C ends an in-process run; `cancel` waits
  for that receipt (up to two minutes) and prints its summary line. Exit 2 when the job is unknown, has no host (an in-process job:
  Ctrl-C the process that runs it), is done or is abandoned; 4 when the host has not finished in time; 6 when the host died
  without a receipt. `host <id>` is the detached process itself — hidden, started by `run --detach` and a tree's `run`, never typed.
- `clean`: removes the **worktrees** of finished isolated jobs; their branches stay, with the work
  committed on them. It never forces: a clean worktree is renamed to a `.trash-*` sibling, its own registration
  deleted and the trash deleted, and the sweep first deletes any `.trash-*` an earlier interrupted free left,
  with the one registration each may still have (`swept N leftover trash directories`; a trash that will not delete,
  that git still lists as a worktree, or whose registration was not released — locked, another repository's, its
  worktree directory still there, a NUL in the file — is a `warning:` line, not a failure, and is kept). If `.claustrum`
  is a link that leaves the working directory, `jobs clean` refuses before touching anything (exit 2). A finished job's worktree is also
  left while a continuation of it (`jobs answer`, below) is running: that job works in it.
  The first Ctrl-C, SIGTERM or SIGHUP stops the sweep (exit 130); a worktree not yet renamed stays intact, and
  the next `jobs clean` finishes any trash. A worktree with anything uncommitted — new files included,
  whatever `status.showUntrackedFiles` says (a commit the runner could not make, a hard-killed run)
  — or one Claustrum will not remove (a populated submodule, a `git worktree lock`-ed one, one on a detached HEAD, one
  mid-rebase/merge/cherry-pick, one whose HEAD reflog holds commits no ref and not the branch's reflog contains (commits made on a detached HEAD; a rebase whose
  commits the branch carries again passes, and so does an amend or reset made while on the branch), one with a `skip-worktree`/`assume-unchanged` file on disk, or a directory
  that is no longer this repository's worktree) — is printed as `could not
  remove <job>: <path> left in place: <reason>` and exits 1. Commit or discard what is in it, then
  rerun. Only `<cwd>/.claustrum/worktrees` is swept: a `coordinate` architect's worktree holds its
  builders' under its own, and is left in place (`it holds job worktrees of its own …`) until
  `claustrum jobs clean --cwd <that worktree>` has removed them — git would delete them with it,
  uncommitted work included. An empty `probe-<hex>` directory there, which a `coordinate` killed
  mid-check leaves behind, is deleted (`removed stray probe directory <name>`); a non-empty one is
  never touched.
- `budget <tree>`: the ledger of one tree (§10); `--reset` deletes it, refused while a member runs.

### Talking to a running job

A claude or opencode role need not finish in one breath. When its turn ends without a `claustrum-report` block, that turn
is a message to the human: the session stays open, the human's reply goes in as the next turn, and the job's clock
stands still while it waits (it keeps its concurrency slot). Everything said lands in the job directory's
`conversation.jsonl` and reads as one line per entry: `? ` a role's question, `> ` the human's reply or message,
`! ` a Claustrum notice (a timeout warning, "no answer"). Text from the role is sanitized like the stream's. Three
front doors, one channel:

- **The terminal.** `run` and `coordinate` with stdin and stderr both terminals are live with a reply timeout of
  1800 s: the question is printed on stderr as `? …` and a `reply> ` prompt reads one line from the terminal itself
  (`/dev/tty`, `CONIN$`; never stdin, which may have carried the brief). What was typed before the question existed is
  discarded when the prompt appears: a reply answers only what was on screen. An empty line means "not now" — it prints
  `not answered; reply later with: claustrum jobs say <id> "<answer>"` and the question stays open for `jobs say` and
  `jobs follow`. Ctrl-C still ends the run `cancelled`, with its receipt, prompt or not. `jobs follow <id>` on a
  terminal prints the same conversation and asks the same way. A blind role (`blind: true`) is never prompted for.
- **Another shell.** `claustrum jobs say <id> "…"` queues one message: the answer while a question is open, a message
  to the running role otherwise (exit 2 when the job has no `live.json`, i.e. is not live, or is done or abandoned, or runs
  a blind role, which receives no human input). `jobs wait <id>`
  blocks until the job is waiting or finished: it prints the question as `? text` then `(question N)`, or the summary
  line `jobs follow` ends on (exit 0 for either, 4 on `--timeout`, 130 on Ctrl-C). A job whose claustrum process is gone with no `result.json`
  is *abandoned*: `jobs wait` prints `abandoned: the claustrum process running this job is gone` and exits 6, and
  `jobs follow` says so once and stops prompting. `--json` prints
  `{"state", "question": {"seq", "text"} | null, "result_status"}` instead, for a script or an architect to branch on.
- **MCP.** `job_status` reports `state: waiting_for_input` and the `question`; the host asks its user with its own
  question tool and answers with `job_say`. `delegate_async` and `coordinate` run live with a 1800 s reply timeout; `delegate`,
  which blocks its host, runs with 0. A `coordinate` architect answers its children's questions itself, so what reaches
  `job_status` is the architect's own (*Questions inside a tree*).

When nobody answers — `--reply-timeout 0` (the default with no terminal), or the timeout passing without a reply — the role is
told "No human is available to answer now" (after a timeout, "No answer arrived within <n> minutes"), then the one rule for both: *If the decision is
one you can make, make it, record it in open_decisions and continue; if it is the human's, stop with status needs_input and put your
questions in the report.* A role that stops that way is answered with `jobs answer` (below). `--reply-timeout <s>` sets the wait and
`--no-live` turns the channel off: the run is exactly what it was before it existed. Blind roles (`code-reviewer`,
`ui-reviewer`) can only tell, not ask: a human answer could carry the rationale the blind gate withholds, so their doubts
go into their findings. Live needs a backend that holds a conversation: claude and opencode. On claude a message sent while the role works
reaches it at its next tool-result boundary, or starts its next turn, and a question that ends a turn the human's message has not yet reached is not left open; opencode runs one process per turn and resumes its session, so a message sent mid-turn waits for
the turn's end and goes out as the next turn, and the 85% timeout warning reaches the role only between turns (a role that works one long
turn never gets it: tell it its deadline in the brief). For any other backend (copilot, cursor, api) the flags are accepted and change nothing;
those roles use the fallback of *When nobody can answer live*.

#### When nobody can answer live

A live conversation needs a backend that can hold one (claude and opencode) and someone to answer. Where either is
missing — **copilot, cursor and api**, which have no live protocol; a run with reply timeout 0 (no terminal, MCP `delegate`); a
child in a tree whose architect is not live — the role cannot ask by ending a turn, and the fallback applies: the role stops with
`"status": "needs_input"` and `"questions": [{"key", "prompt", "options", "allow_free_form"}]` in its `claustrum-report` instead of
guessing at a decision that is the human's. Blind roles never do this; their doubts go in `findings`.

- **The receipt says so.** `status: needs_input`, `error: null`, the questions in `result.json` (`questions[]`), and exit code
  **7** — the code a tree child's early return uses too, so read the document, not the number: an early return is
  `{"status":"needs_input","job_id","question":{seq,text}}` and the job is still waiting (`jobs reply`); a finished run is a
  whole RunResult with `questions[]` and nothing waiting (`jobs answer`). Human mode prints `needs input: <id>`, one `? [key] prompt`
  per question with its options, and the command to answer with. A report that says `needs_input` with no readable question
  still ends `needs_input` (answer in free text); the receipt's `warnings[]` says so. Over MCP, `job_status` shows `status` and
  `questions` for a done job, and `job_result` the whole receipt.
- **`claustrum jobs answer <id> (--answers <file> | <text>) [--json]`.** Continues a finished `needs_input` job: its backend session
  is resumed (`run --resume <session_id>`) as a **new job** with the same role, cast and cwd, linked by `parent_job_id`, and **pinned to what
  the job ran as**: its backend, model and permission level (`backend.json`), whatever today's configuration resolves; when it would
  resolve otherwise the receipt's `warnings[]` names both. An isolated job continues **in the same worktree** (`.claustrum/worktrees/<the job>`, on its
  branch; the new receipt's `worktree` says so): reused when it still holds the branch and is clean, recreated at that path when
  `jobs clean` removed it, refused with the reason when something else is there or it holds uncommitted work (a `failed` receipt; fix it
  and answer again). The session records its working directory and its tool calls name paths under it. A job is **answered once**: the
  start of its continuation writes `answered.json` beside its receipt, exclusively; a second `jobs answer` (or a retried `job_answer`)
  is refused, exit 2, naming the continuation, and when that one stopped with `needs_input` too, it is the one to answer. A continuation that
  never reached its backend (a refusal, a budget, a missing binary) gives the answer back. The message is the
  answers: `--answers` is a JSON object, question `key` → string answer (an unknown key, or a value outside the options of a
  question that does not allow free form, is refused; a question left out is handed back to the role to decide and record in
  `open_decisions`), or the text is taken as one free-form reply. It prints the new job's result as `run` does and exits with its
  status's code, so a role that asks again gives 7 again. It runs under the live rules of any `run`: on a terminal the new job is live. Exit 2, saying which rule, for a job that is unknown, not finished, not
  `needs_input`, a blind role, a coordinate job (use `coordinate --resume`), a backend that keeps no session (api: start a new run with
  the answers in its brief) or a receipt with no session id. MCP: `job_answer {job_id, answers | text}`, which returns the new job's
  id at once (poll it like `delegate_async`).
- **`claustrum coordinate --resume <job> (--answers <file> | --brief <text> | --brief-file <path>)`.** The same for a coordinate job:
  its architect session is resumed on the job's branch (`claustrum/<job>`) in the job's own worktree, children's worktrees
  included, pinned and answered once as above, under the same cast and the same **budget tree**: the children are accounted in the first job's ledger (`jobs budget <first job>` lists both architect runs),
  and the job left a `successor` marker so the tree's detached hosts watch the running architect, not the finished one. `--cast`,
  `--issues` and `--cwd` are refused (the job's own are used); `--tier`, `--budget` and `--timeout` override the original's, and `--model` is
  accepted but the architect stays on the backend and model its session began on (a warning says so).
  MCP `job_answer` does this for a coordinate job.
- **Progress notes.** A role on any backend may print a line `claustrum-note: <text>`. Claustrum appends it to the job's
  `conversation.jsonl` as a `message` from the role (the file is created if the run is not live; a live session numbers and writes
  them itself) and `jobs follow` shows it as `· <text>`. One-way: nothing answers it. The job's `conversation` field then points at the file. A repeat of a note is recorded once, at
  most 200 per job. Only a JSON event's assistant text is read (not a tool's output), so on a backend whose output arrives as one document at
  the end (cursor, api), notes appear when the run ends.

#### Questions inside a tree

In a `coordinate` tree (`CLAUSTRUM_PARENT_JOB` set) whose architect is live (`CLAUSTRUM_ARCHITECT_LIVE=1`, which `coordinate` sets next to
the tree id; a tree id exported by hand, `coordinate --no-live` and a backend with no live protocol leave it unset, and there a child runs
in-process as before with reply timeout 0, told at once that no human is available) the architect answers its children first. A live session's stdin pipe belongs to
the process that started it, so a child that may outlive its `run` call is hosted from the start in a detached `claustrum jobs host`
(`--detach`), and a plain `claustrum run` there is `--detach` followed by a wait, with a reply timeout of 1800 s (the TTY rule's 0
would tell the child at once that nobody can answer). If the job finishes, `run` prints and exits exactly as it does outside a tree.
If the job asks, `run` returns early with exit code **7**: human mode prints `needs input: <job id>` then `? <text>`; `--json`
prints `{"status":"needs_input","job_id":"…","question":{"seq":…,"text":"…"}}` as its one document. The child keeps its session, its
slot and its budget reservation while it waits, and its clock stands still. The architect's loop is `run` → 7 →
`claustrum jobs reply <id> "<answer>"` → 7 … → 0: `reply` queues the answer (refusing a blind, done or abandoned job as `jobs say`
does), then waits like `run` and prints the next question (7) or the result exactly as `run` would (`--json`: the RunResult; the
exit code is its status's). A child whose backend has no live protocol cannot wait: it ends `needs_input` and `run` exits 7 with its
whole RunResult (`questions[]`), nothing alive to reply to. `jobs reply` refuses such a job, saying to use `jobs answer`, which resumes it.
What the architect cannot answer it escalates by ending its own turn with the question: it is live under
`coordinate`, so its human gets it (the terminal, or `job_status`/`job_say` over MCP, where `coordinate` now runs its architect with
a 1800 s reply timeout), and the architect replies to the child with what they said. A child nobody answers within the window is
told no human is available, and decides what it can. Ctrl-C (or SIGTERM, SIGHUP) on a waiting `run` or `reply` cancels the job through its host and prints the
`cancelled` receipt, exit 130, as it would in-process; a machinery refusal in the host (a `.claustrum` link that leaves the repo) is exit 2
here too. Under `--json` a tree's `run` and `jobs reply` always print exactly one document: the `needs_input` one, the job's RunResult,
or, when the job's own never comes (the host died: exit 6; `result.json` unreadable: exit 1; a Ctrl-C that could not reach the host:
exit 130), a RunResult synthesized with the job id and an `error` saying which. No paid process is left with nobody to stop it: a detached
host checks every few seconds that the `coordinate` job it hangs from is still alive (no `result.json`, its process running) and cancels
its job, with a Notice in the conversation, once it is not; and a `coordinate` that finishes, is cancelled or times out cancels every
detached job of its tree still running, listing them in its receipt's `warnings`. Not detached, so exactly as before: a blind role (it never
asks), `--stream` (this process must stay in the room, so the child cannot be answered), `--no-live`, a backend that is not live, and any
`run` outside a tree.

### `claustrum cast questions [--json] | create --answers <file> [--name n] | new [--name n] | list | show <name> | use <name>`

§8. `new` asks on the terminal; `create` takes a JSON file `{question key: answer}`; `use` copies a
cast over `default.json`.

### `claustrum sync`

```
claustrum sync [--only claude,opencode,copilot,cursor] [--roles a,b] [--global] [--force] [--check] [--dry-run]
```

§12. `--check` exits 2 on a hand-edited, stale or missing generated file (for CI); `--dry-run`
prints the unified diff and, after it, the model each agent file names and why; `--force` adopts a file
Claustrum did not generate. `sync` reads `claustrum.json` (§7, §12).

### `claustrum init [--all] [--answers <file>]`

Scaffolds the current directory: writes model aliases to the **user config**
(`~/.config/claustrum/config.json`, or `%APPDATA%\claustrum\config.json` on Windows) when
`--answers <file>` is given — a JSON object mapping alias (`frontier-reasoning`,
`frontier-coding`, `standard-coding`, `cheap-coding`, `fast`) to `backend:model-id`. Only
missing aliases are written; existing ones are never overwritten, and every other key in the file
(including ones Claustrum does not know) is kept. The file is replaced atomically (a temp file beside it, then a rename). Without `--answers`, missing
aliases are asked on the terminal; with redirected stdin it prints a message and exits 2.

`init questions --json` prints one question per missing built-in alias with options
(including the built-in default), the same snake_case shape as `CastQuestion`, plus `user_config_path`. "Missing" means defined neither in
the user config nor in the repo `claustrum.json`. `init` writes no repo `claustrum.json` (it reports `claustrum.json: not written …`
when absent, `already present, left unchanged` when there): aliases are per-developer in the user config.

Also creates `.claustrum/casts/` and git-ignores `.claustrum/{casts,briefs,worktrees,locks}/`,
runs `sync` for `claude` **always** — its sync is the one that registers the MCP server in
`.mcp.json`/`.vscode/mcp.json` — plus each harness the repo already shows signs of
(`.opencode/` or `opencode.json[c]`, `.github/`, `.cursor/`; `--all` for every one), and
appends a "Claustrum delegation" pointer to an existing `AGENTS.md` saying the cast is
per-developer and the aliases live in the user config.
It never creates `AGENTS.md`/`CLAUDE.md` and never overwrites a file it did not
generate; a rerun reports "already present, left unchanged".

Every write is fenced to the current directory, as in `sync` (§12): a `.claustrum`, `claustrum.json`,
`.gitignore` or `AGENTS.md` that resolves outside it through a committed link is reported
`refused (resolves outside the repo → <real path>)`, nothing is read or written there, and `init` exits 2
at the end. The harness syncs still run. A file there that is a pipe, a socket or a device (a FIFO from
`mkfifo`, a committed link to `/dev/zero`) is never opened for reading or writing: it is reported
`refused (<reason>)` and `init` exits 2. The same holds for every repo file Claustrum reads or writes in
place (§12 sync targets, casts, `claustrum.json`): the type is checked on the opened file, and a read stops
at 8 MiB (`not a regular file or too large`). Paths you type on the command line (`--file`, `--brief-file`,
`--answers`) stay readable as pipes; the MCP `files` argument does not.

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
and a malformed local `role.json` is exit 2 naming the file. An override file is read only when its real
path stays inside the working directory, a link at the file itself included: one that leaves it is exit 2
naming the file and its real path, in `sync`, `run` and `roles show` alike. `sync --global` never applies a
repo's overrides (§12).

Each role's system body is: the opening paragraph of the rendered `ROLE.md`, then `## Report format`,
then the rest of `ROLE.md`, then `## House rules` ("read `AGENTS.md` and `CLAUDE.md` first; they are
law"). The report format sits second because a model skipped it on short tasks when it came last
(`RoleRenderer.ComposeSystemBody`). The brief always goes in the **user** prompt.

The `api` backend cannot read files, so for it alone `## House rules` carries the working directory's
`AGENTS.md` and `CLAUDE.md` themselves, each under its own heading and capped at 32 KiB (cut at a
character boundary and marked). A file that is absent is said so in one line. A file whose real path
leaves the working directory (a committed `AGENTS.md -> ~/.ssh/id_rsa`) is never read, and neither is a
linked one that does not land on a regular `*.md` file outside any `.git` directory (`AGENTS.md -> .git/config`
holds the CI token). On Linux, macOS and FreeBSD the file is opened by walking down from `/` one name at a time without
following any link, so a link swapped in anywhere on the way, above the working directory included, is not read; on
Windows the opened file's own final path is judged. A file with other hard links is not inlined either, nor is one this
system cannot check; where that cannot be shown, nothing is inlined. The run's `warnings` name the file
and why, and so does a truncation (#55). An isolated run (`--branch`, or a cast with `max_parallel` of 2 or more) inlines the
files of its own worktree, that is of the branch it runs on, and not those of the main checkout's working copy; if that
worktree is not exactly `<repository>/.claustrum/worktrees/<job>` (a link inside the repository, such as
`.claustrum -> .cache/claustrum`, is followed; one leaving it, or a link planted at or above the worktree, is not), nothing is
inlined and the warning says whether the directory resolves outside the repository or is not the job's worktree.

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
| `status` | runner | `success`, `failed`, `timeout`, `cancelled`, `backend_missing`, `budget_exceeded`, `needs_input` (the report asked the human something; exit 7) |
| `error` | runner | **read it whenever `status` is not `success`** — it says which refusal or failure |
| `error_kind`, `error_detail` | backend | `failed` runs only: why, when the backend's output says — `provider_quota` (out of credits, a rate limit: 402/429, `provider.quota`), `provider_auth` (401/403), `model_not_found` (404, `provider.no-route`), `provider_transport` (408/5xx, no answer at all), or `other`. `error_detail` is the provider's own words, without the text the role wrote before it failed. Read for **opencode** and **claude**; the other backends report `other` on a failure and `null` while it succeeds. `null` on every run that did not fail |
| `before_any_work` | runner | `failed` runs only: `true` when the run changed no file, was a single turn, completed no tool call (a call that errored did nothing) and made at most two steps (model requests: the first, and the one that hands an errored call back); `false` when it did work; `null` when the backend cannot count its tool calls. With a provider-side `error_kind` it is what lets a cast's fallback take over |
| `fallback_of`, `retried_as` | runner | a cast's fallback (§8): the retry's receipt names the job it replaced in `fallback_of`; the replaced job's receipt stays as it was (`failed`, its own `error_kind`) plus `retried_as: <the retry's job id>`. `jobs show <id>` of a retried job tells you where to look; `jobs say|reply|wait|follow|cancel` and MCP `job_say`/`job_status` given its id go on to the retry and say so (stderr; `retried_as` in the MCP result) |
| `backend`, `model`, `role` | resolved request | what actually ran |
| `final_message` | backend | the agent's last message, report fence included |
| `report` | extracted | `{data: <parsed JSON>, raw_text}` from the `claustrum-report` fence |
| `report_status` | extracted | `ok`, `missing` (no fence), `unparsed` (fence, invalid JSON — `raw_text` kept) |
| `questions[{key, prompt, options[], allow_free_form}]` | report | only for `needs_input`; `null` otherwise |
| `conversation` | runner | `{path, entries}` of the job's `conversation.jsonl`: a live job's, or a run's `claustrum-note:` lines; `null` when there is none |
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

Every role must end its final message with exactly one fenced block tagged `claustrum-report`. A non-blind role (builder, tester,
architect, demo-author) may also give `status: needs_input` with `questions[{key, prompt, options[], allow_free_form}]`, the
questions-as-data fallback of *When nobody can answer live*; the two reviewers, which are blind, never do (their doubts are findings):

- **builder**: `{status: done|partial|blocked|needs_input, summary, files_changed[{path, why}], behaviour_to_cover[], open_decisions[], shaky[], departed_from_brief[]}` — `shaky` is what the coordinator reads first.
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

`sync` reads the same layers (§12): `models` aliases, and `roles.<name>.model|effort|deny`, decide the synced
agent files. `sync --global` reads only the user file, never the repo's `claustrum.json`.

`roles.<name>.user_mcp` (`true`/`false`) keeps a claude role's user-level MCP servers (§13). It is read from the user config only: in a repo's `claustrum.json` it is ignored, with a warning on the receipt that names the file.

`backends.<name>` takes `path` only. There was an `injection` key once; nothing ever read it and it was
removed (#56), so a config that still carries it loads, the key ignored like any other unknown one — `claude`
always injects with `--append-system-prompt-file`. There is no JSON schema file, and a key Claustrum does
not know (a `$schema`, say) is ignored rather than refused.

**Model grammar**: `[backend:]<model-id>`; a bare name is looked up as an alias (recursively,
depth 3), then handed to the backend as an id. `claustrum init` asks what each alias that neither your
user config nor the repo's `claustrum.json` defines should map to, and writes the answers to the user
config; an alias nobody defines falls back to a built-in `claude:*` default, and every run that lands on
one says so in `warnings[]`. What each backend accepts as an id:

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
per-developer, git-ignored:

```json
{ "name": "default", "library": "1.1.0",
  "architect": { "mode": "host" | "spawned", "model": "<alias|backend:id>" | null, "tier": "high|xhigh|max" | null },
  "roles": {
    "builder":       { "model": "standard-coding", "backend": null, "tier": null, "max_parallel": 2 },
    "code-reviewer": { "model": "cheap-coding", "fallback": "frontier-coding", "backend": null, "tier": "xhigh", "max_parallel": null },
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
- Each role entry and a spawned `architect` may carry an optional `fallback`: an alias or a
  `backend:model-id`, the same grammar as `model`, with `resolved_fallback` recorded at creation as `resolved` is.
  When the role's model is refused **by its provider before the role did anything** — out of credits (402,
  `provider.quota`), a key the provider rejects, a model it cannot route, or a connection that never answered
  (`error_kind`, §6) — `run`, `delegate`, `delegate_async` and `coordinate`'s architect retry **once**, as a new
  job, on the fallback, with the same brief (the blind gate is checked again), role, tier, permission, cast and tree. The returned receipt is the
  retry's, with `fallback_of` and a warning: `code-reviewer: opencode:openrouter/deepseek/deepseek-v4-pro failed
  before starting (provider_quota: <the provider's words>); ran on claude:opus instead (job <the first job>)`.
  The first job's receipt keeps `status: failed` and gains `retried_as`. Not retried: a run an explicit
  `--model`/`--backend` chose, a run that changed a file, completed a tool call or went past its second step, a live session past its first
  turn, a continuation (`jobs answer`, `--resume`, `coordinate --resume`) whose session only its own backend can
  read, a job that is itself a retry (never a second one), a failure of another kind (a timeout, the role's own
  error, `other`). The first job's cost is charged as usual; the retry gets what is left of the run's `--budget`
  and is admitted by the tree's ledger like any sibling. A fallback that cannot be used (it does not resolve, the
  budget is spent) leaves the first receipt as it was plus a warning saying so. A retry run by a detached host (`run --detach`, a tree's
  child) is that host's job too: `jobs cancel`, a tree's end and a waiter's Ctrl-C reach it like the first. Name either job: `jobs say`,
  `reply`, `wait`, `follow`, `cancel` and MCP `job_say`/`job_status` given the first id act on the retry (one hop) and say so. A `coordinate`
  architect's retry is a whole new coordinate job: its own tree, branch and ledger entry, rooted at its own id.
- Each role entry and `architect` may carry an optional `resolved` field,
  `"backend:model-id"` — what the alias resolved to when the cast was created or last
  edited. Filled automatically by `cast create`/`cast new`/`cast_create`; saved before
  this change do not have it and still load. If the config changes so the same alias
  resolves differently later, `run` warns: `cast '<name>' role '<role>': alias '<alias>'
  resolved to <stored> when the cast was created, now to <current> (<source>)`.
- A cast is never read, written or listed through a link that leaves the working directory (the fence
  of `sync`, §12): `cast new`/`create`/`use`/`show`/`list`, `cast_create` and `run`/`delegate` refuse with
  exit 2, naming the file and where it really leads (the fence decides before any existence check; a link out
  to nothing reads as "no such cast"). A missing `name` or `library` loads as the file's
  name and the shipped library version.

**The questionnaire** is owned by the tool so it is identical in every host: `claustrum cast
questions --json` returns one question per library role, then the builders' parallel cap and the
budget, then each role's optional fallback, with their live options (the aliases in `claustrum.json` that land on a harness the role
lists; `not needed` where allowed; free-form `backend:id`; `no cap` for the budget). The returned
JSON also includes a `models` array: one entry per alias in the merged config with its current
`target` (`backend:model-id`), the `layer` it came from (`builtin|user|repo|env`) and a
human-readable `source`. Text mode prints this table before the questions, one line per alias, so
the alias → model mapping (and a silent built-in fallback) is visible up front. A built-in alias
shows `(built-in default — no claustrum.json or user config defines it)` as its source.

| key | prompt | allows |
|---|---|---|
| `architect` | `host` or `spawned on <alias>` | free form |
| `builder` | model for the builder | free form |
| `code-reviewer`, `tester`, `ui-reviewer`, `demo-author` | model for the role — claude aliases only for `ui-reviewer` and `demo-author` | free form, `not needed` (for `demo-author`, that is the "no demo" switch) |
| `builder_max_parallel` | `1`, `2`, `3`, … | free form |
| `budget` | USD, or `no cap` | free form |
| `architect_fallback`, `builder_fallback`, `code-reviewer_fallback`, … | asked last, one per role: the same aliases as the role's own question, or `none` (the default, and what `cast new` takes when its input ends first, so a script written before fallbacks still works). Dropped when the role is `not needed`, or the architect is `host` | free form |

Answer them with `claustrum cast new` on a terminal, `claustrum cast create --answers file.json`
from a script, or `/claustrum` in a chat host (the synced skill asks with the host's own picker,
then calls `cast_create`). An empty option list means no `claustrum.json` alias lands on an installed
backend the role runs on — on a machine without `claude`, the demo-author and ui-reviewer have none;
`cast questions` and `cast new` say so and still offer `not needed`. All three refuse an answer that
puts a role — or its fallback — on a harness it does not list, naming the role and its harnesses (`cast new` asks again
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

**The job machinery is fenced.** A `.claustrum` that resolves outside the working directory through a
committed link (or a `worktrees/` or `locks/` inside it that does) gets nothing created through it: a run
that needs a worktree or a lock file is refused with exit 2 and a `failed` receipt whose `error` names the path
and its real path, and `coordinate` refuses the same way before any job exists. A link that stays inside the
repo is fine. The MCP tools return the `failed` document.

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
worktree. `claustrum jobs clean` removes the worktrees of finished jobs, never with `--force` (it renames a
clean one aside and deletes its registration, as `--branch` does), and keeps the branches. Its first Ctrl-C, SIGTERM or SIGHUP
stops the sweep (exit 130); a second one exits at once.

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
the architect's own `git add` would take in its builders' worktrees, briefs and locks), and a symlink
committed anywhere under `.claustrum` (the architect's harness would read or write through it: `git rm` it). Commit them,
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
`sync --global` renders the library only: a repo's `.claustrum/roles/` applies to that repo, and its files
must not end up in your user-global agents. Run inside a repo that overrides a role being synced, every mode
(write, `--dry-run`, `--check`) lists the role under `ignored local overrides (--global renders the library
only; a repo's .claustrum/roles/ applies to that repo):`; the exit code is unchanged.
A symlinked agent file is foreign whatever it points at; `--force` replaces the link with a regular
file and never writes through it, so the link's target is left byte-identical. `--dry-run` and
`--check` print `would replace symlink` with `--force`; without it the link is listed as foreign with
its target. Hard links are not detected: a hard-linked marked file is written in place.

In repo mode a target whose directory resolves outside the directory sync runs in (the current directory;
a committed `.claude -> ../../.claude`, say, or a monorepo subdirectory whose `.claude` links upward) is **refused**: it is listed under `refused
(sync never writes through a link that leaves the repo, nor into a pipe, socket or device):` with its real path, nothing
is written or created there, and the command exits 2 in write, `--dry-run` and `--check`; `--force` does
not override. The `.claustrum` manifest and the JSON MCP targets follow the same rule, and a JSON target
that is a link resolving inside that directory is merged through. The fence covers reads too: a local role
override whose real path leaves the directory is never read (exit 2, with the real path), so its content
cannot reach an agent file or a `--dry-run` diff; a link to nothing outside is just no override. A local override
linked to a file inside the directory is read only if that file has the same extension (`.json` or `.md`) and no
`.git` path component, so `ROLE.md -> .git/config` is refused too. On Windows a repo under a folder-mounted volume or a
container bind mount is expected to resolve through the OS placement (`GetFinalPathNameByHandle`) of a link
the string walk cannot place; if the OS can spell neither a DOS nor a GUID name for it, the repo stays refused.
On Windows a link or target spelled as a network path (`\\server\share`, `\\?\UNC\…`, `\\?\GLOBALROOT\Device\Mup\…`, any letter case) is refused **without being opened** (a link's own target is read before the link is statted), so a committed link cannot make `sync` authenticate to a foreign host; a repo whose own directory is on a share keeps working for links that stay on that share (#105). None of this is measured on Windows beyond the CI leg's link tests. Under `--global` a linked root or
agents/skills directory is your own setup and is written into; the `global root` line and the
`(resolved → <real path>)` annotations say where the files really went. In either mode, whenever a replaced link's
directory resolves elsewhere (a linked root, a linked agents directory, a `.claude` linked inside the repo), the
`replaced symlink` / `would replace symlink` line adds `(lands at <real path>)`: the file is written beside the link's
name in that real directory, never at the link's target. For agent and skill files (the targets `--force` replaces rather than merges),
`--dry-run` never shows a linked file's target as removed lines; a linked JSON target is merged through, so it is read and diffed.

**A synced Claude agent carries no deny list today.** In agent frontmatter, `disallowedTools` cannot
hold a per-command deny: an entry with a specifier such as `Bash(git push *)` removes the whole
Bash tool ([code.claude.com/docs/en/sub-agents](https://code.claude.com/docs/en/sub-agents), read
2026-10-02). A frontmatter `PreToolUse` hook on `Bash` that exits 2 could carry it, scoped to that
agent; that is not implemented yet (a follow-up issue). Until then `sync` renders role.json `deny`
nowhere in Claude files, a native agent — the builder's `git push`, the demo-author's git verbs — keeps it as prose
only, and the deny list is enforced only when a role runs through `claustrum run`/`delegate`.

**`claustrum.json` decides the model.** `sync` loads the layers `run` uses and chases each tier's model
class (or `roles.<role>.model`) through `models`. The model is written when the alias lands on the harness
being synced: `claude:opus` in a Claude file, `opencode:openrouter/…` in an opencode file, `copilot:<id>`,
`cursor:<id>`. An alias on another backend (an `api:` model in a Claude agent file, or the built-in
`claude:*` aliases in an opencode file) cannot be expressed there, and the default stands: `opus`/`sonnet`/`haiku`
for Claude, the two OpenRouter ids for opencode, `auto` for Copilot, `inherit` for Cursor. `roles.<role>.model`
applies to every tier of the role; `roles.<role>.effort` re-points the base agent only, because a `-xhigh`/`-max`
stub is named for its effort. `roles.<role>.permission` is not applied to synced files. `--dry-run` lists, after
the diffs, one line per agent file: the model and the reason (`alias 'x' → backend:id (layer)`, or `default for
class '…'` with why the alias was not used; and, for a role with `roles.<role>.permission`, `claustrum.json permission
(…) not rendered`). A malformed `claustrum.json`, or a model alias cycle among the roles being synced, fails `sync`
with exit 2 before anything is written. `claustrum init` loads the same configuration and fails the same way.

**Deny.** Only opencode has a per-command deny syntax in an agent file: `claustrum.json`'s `roles.<role>.deny`
and role.json's own `deny` (the builder's `git push`) become `permission.bash` entries (`"<deny>": deny` and
`"<deny> *": deny`, no allow rule), so a synced opencode agent enforces what `claustrum run` does. **That changes
default opencode output**: every role with a deny list now has a `permission:` block, where there was none, so
a re-sync reports those agent files as stale. Claude, Copilot and Cursor files cannot carry a per-command deny, so
`--dry-run` says `claustrum.json deny (…) not rendered` for the role, and only `claustrum run` enforces it.

**opencode `mode` and `variant`.** A synced opencode agent is `mode: subagent`, except a role whose `role.json`
says `"primary": true` (the library's architect, and its tier stubs): that one is `mode: all`, so it can be picked
as the main agent and still be spawned. Claude Code, Copilot and Cursor need nothing for a primary role: Claude
Code runs any agent file as the main thread with `claude --agent <name>`, and Copilot's `--agent` selects any
agent. A local `.claustrum/roles/<role>/role.json` sets `primary` like any scalar. `variant:` is written only
when the resolved model carries `#<variant>` (an alias such as `opencode:openrouter/deepseek/deepseek-v4-pro#xhigh`);
a tier's effort never becomes one, since a model may not publish it. `claustrum run` on opencode follows the
same rule: `--model` is the resolved model as is, so a `-max` tier on the default `cheap-coding` alias sends no `#max`.

role.json `withoutTools` names tools a role's level grants but it never uses (the demo-author's
`NotebookEdit`); a synced Claude agent leaves them out of `tools` and lists them under
`disallowedTools`. It reaches **synced Claude agent files only**: under `claustrum run` the claude
backend's `edit+shell` flags do not name it, and `acceptEdits` approves `NotebookEdit` like any
edit. It is honoured at every level (`readonly` and `shell` too), and a dropped tool is never also in `tools`. A local `.claustrum/roles/<role>/role.json` override replaces the whole array, as it does
`tools` and `deny`.

| Harness | Repo files | Global (`--global`) | In chat |
|---|---|---|---|
| `claude` | `.claude/agents/<role>.md` + `-xhigh`/`-max`, `.claude/skills/claustrum/SKILL.md`, `.mcp.json` (`mcpServers.claustrum`), `.vscode/mcp.json` (`servers.claustrum`, stdio) | `agents` and `skills` under `$CLAUDE_CONFIG_DIR`, else `~/.claude`; on Windows/macOS also `claude_desktop_config.json` with the binary's absolute path | `/claustrum`, `@architect` … |
| `opencode` | `.opencode/agent/<role>.md`, `.opencode/command/claustrum.md`, `opencode.json` (`mcp.claustrum`) | `$OPENCODE_CONFIG_DIR`, else `$XDG_CONFIG_HOME/opencode`, else `~/.config/opencode` | `/claustrum` |
| `copilot` | `.github/agents/<role>.agent.md`, `.github/skills/claustrum/SKILL.md` | `agents` and `skills` under `$COPILOT_HOME`, else `~/.copilot` | `copilot --agent architect`; the skill surfaces by relevance |
| `cursor` | `.cursor/agents/<role>.md` + variants, `.cursor/skills/claustrum/SKILL.md`, `.cursor/mcp.json` | `~/.cursor/{agents,skills,mcp.json}` always: cursor-agent hard-codes that directory for all three, and `CURSOR_CONFIG_DIR` moves only its `cli-config.json` | `/claustrum`, subagents by name |

`sync --global` prints one line per harness, `global root (<harness>): <path> (<reason>)`, in every
mode (write, `--dry-run`, `--check`), naming the root it chose and the variable that decided it. When
the root is itself a link the line reads `<path> → <real path> (<reason>)`.

A tier stub (`<role>-xhigh`, `<role>-max`) is the full role rendered at that tier plus a
`## Non-negotiable` block, so a natively spawned stub receives the role's instructions as its system prompt.

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
| `api` | `curl` | `OPENROUTER_API_KEY` / `ANTHROPIC_API_KEY` by model prefix | system message (with `AGENTS.md`/`CLAUDE.md` inlined, §4); **no tools**, `changed_files` always empty | yes |

**MCP servers on `claude`.** A claude role runs with `--strict-mcp-config`: it gets the servers of the working
directory's own `.mcp.json` (what `sync` writes there is the `claustrum` server only; any other server is yours) and
nothing from your user config, so your personal connectors are not attached to headless runs and do not cost tokens on
every turn. A `.mcp.json` that is a link leaving the directory, a pipe or device, or not valid JSON is refused with a
warning on the receipt, and the run then has no MCP servers. To keep your user-level servers for a role, set
`"roles": { "<role>": { "user_mcp": true } }` in your **user** config; a repo's `claustrum.json` (and a repo's
`.claustrum/roles/<role>/role.json` `userMcp`) cannot attach your personal connectors and is ignored with a warning.
The `ui-reviewer` and `demo-author` ship with `user_mcp` on, because their browser servers (`mcp__Claude_Browser`,
`mcp__claude-in-chrome`) come from your user or built-in config, not from `.mcp.json`; set it to `false` for a role
in the user config to take that back (the role then has no browser). The servers a run was given are
in the job's `backend.json` (`mcp_servers`; `["user"]` when opted in). `opencode` has no way to leave its global
config out of a run, so its roles still see those servers.

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
`backend.json` (backend, model, and for claude the `mcp_servers` the run had, with `mcp.json`, the copy it loaded),
`stdout.log`, `stderr.log`, `result.json`, and for a live job `conversation.jsonl`, `live.json` (the session's
state and the open question's `seq`) and `inbox/` (messages waiting to be consumed). A detached job adds `host-request.json`
(what the host runs), `host.json` (the host's pid and start time, and the tree it was started in), `host.log` (its console output) and, once `jobs cancel` asks, `cancel`; a `coordinate` job adds `owner.json` (its process, which its detached children watch). A job that ended `needs_input` adds `resume.json` (the request `jobs answer` and `coordinate --resume` rebuild the run from) and, once `coordinate --resume` continues a coordinate job, `successor` (the job that took over its tree). A run that printed `claustrum-note:` lines has a `conversation.jsonl` without being live. `jobs.keep_last` (200) prunes old ones when a new job is
created. Budget ledgers live beside them under `budget/<tree>/`.

## 15. Troubleshooting

| You see | It means | Fix |
|---|---|---|
| exit 2, `blind role: brief carries rationale` | the reviewer's brief has `## Context`/`## Plan`/`## Rationale` or a pasted report | remove it; add requirement or code, never reasoning |
| exit 2, `this repo isn't initialized: no user config at <path> and no claustrum.json at <path>; run \`claustrum init\`` | neither the user config file (`~/.config/claustrum/config.json`) nor the repo's `claustrum.json` exists | run `claustrum init` to set up model aliases and scaffold |
| exit 2, `cast '<x>' not found` / `coordinate needs a cast` | no `.claustrum/casts/<x>.json` in the cwd | `cast create`/`cast new`, or `--cast` the right name |
| exit 2, `coordinate runs the architect in a worktree, which sees only committed files — commit (or un-ignore) <path> (<state>) first` | the cast, `claustrum.json` or a role file under `.claustrum/roles/` (`<role>/role.json`, `<role>/ROLE.md`, `<role>/parts/*.md`) differs from `HEAD` — modified, staged, untracked or ignored — or hides a local edit (`skip-worktree`, `assume-unchanged`), and the architect's worktree would not see it | commit it (a cast that is untracked or ignored is fine once a committed `.gitignore` rule ignores `.claustrum/casts/` — `claustrum init` writes it; `git update-index --no-skip-worktree`/`--no-assume-unchanged <path>` first for a hidden edit), then rerun; nothing was spent |
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
| `status: failed`, `error_kind: provider_quota` (or `provider_auth`, `model_not_found`, `provider_transport`) | the model's provider refused the request — no credits (OpenRouter's `402 provider.quota`), a key it rejects, a model it cannot route, no answer — and `error_detail` is its message | fix it at the provider, or give the role a `fallback` in the cast (§8): a refusal before any work is then retried once on it. `retried_as` on the receipt means that already happened: read the retry's |
| `status: failed`, "… slots … stayed unavailable" | waited past `--timeout` for a `max_parallel` slot | start fewer at once, or raise `--timeout` |
| `status: failed`, "`--branch <name>`: …" | the branch does not exist locally, another checkout holds it (the main checkout, a running job, a dirty finished worktree, a job unknown under this `CLAUSTRUM_HOME`), git would not check it out or did not within 5 minutes (a slow smudge filter or post-checkout hook), or another run on the branch outlasted `--timeout` | read `error`: it names the holder, or quotes git; switch that checkout off the branch, let the job finish, commit/discard its changes, or run `claustrum jobs clean` under the home that ran the job; after a slow checkout, `left behind: …` names anything the undo could not remove (a cancelled add records `git worktree add was cancelled; left behind: …` as the receipt's `error`: `error: …` on stderr in human mode, inside the document under `--json`) — `git worktree remove -f -f <path>` removes a worktree git left locked |
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
| `jobs clean`: "… left in place: it is on a detached HEAD / its HEAD names … which does not exist / has a rebase in progress / its HEAD reflog holds N commits that no branch, tag or other ref contains / is marked skip-worktree or assume-unchanged / is not this repository's worktree" (and `--branch`'s "which was left in place (…)" for the same) | the finished worktree holds something a delete would lose: commits on a detached HEAD or a branch that is not there, an operation in progress, commits only its reflog remembers (the pre-amend or pre-reset ones), edits git is told to hide, or a `.git` the role replaced with its own repository | open the worktree and resolve it (put the commits on a branch, switch to one, finish or abort the operation; for reflog-only commits `git -C <path> branch <name> <sha>` keeps one, `git -C <path> reflog expire --expire-unreachable=now HEAD` drops them; `git update-index --no-skip-worktree`/`--no-assume-unchanged` then look at the file; for a re-initialised directory move its repository out), then rerun; nothing was moved or deleted |
| `jobs clean` / `--branch`: "… left in place: it needs git 2.31 or later" | the installed git predates `git rev-parse --path-format=absolute`, which the ownership check of a free reads | upgrade git to 2.31 or later; nothing was moved or deleted |
| `jobs clean`: "`.claustrum/worktrees` resolves outside the working directory …" (exit 2) | `.claustrum` is a symlink leaving the repo | replace the link with a real directory; `jobs clean` touches nothing through one |
| `jobs clean`: "`<path>` freed, but … (trash at …; `claustrum jobs clean` removes it)" / `--branch` warning | the worktree directory was moved aside and is safe, but deleting its registration or the trash failed | rerun `claustrum jobs clean`; it finishes the trash and the registration |
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
from a real host; the Windows leg of everything above, the OS-placed links of §12 (folder-mounted volumes,
container bind mounts, cloud placeholders) among it; and a plain failed `git worktree add` (not a cancel),
which by reading still leaves its job `pending` with no receipt.
