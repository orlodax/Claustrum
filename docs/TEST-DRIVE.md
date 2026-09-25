# Test-driving Claustrum on a real task

Claustrum has run on toy repos (`hello.txt`, the smoke script, the fixtures) and never on a real
task. This is the ordered list to change that. Every step names what to run, what to expect, and
what to write down; the whole drive is meant to fit in one afternoon and under about $20.

Read this beside [MANUAL.md](MANUAL.md) (the reference) and [INSTALL.md](INSTALL.md) (a fresh
machine). The numbers quoted below were measured on the owner's Linux box on 2026-09-21/22 and are
in [NOTES.md](../NOTES.md).

The drive is four legs, cheapest first, each one proving the next one's precondition:

| Leg | What it proves | Pays |
|---|---|---|
| 0. Preflight | the binary, every backend, `gh`, the keys | nothing (one optional probe) |
| 1. One delegation from the shell | a real brief, a real diff, the receipt, the blind gate | one builder + one reviewer run |
| 2. You are the architect (host mode) | the chat host drives the pipeline through the cast | a full pipeline |
| 3. Spawned architect (`coordinate`) | the pipeline runs unattended and leaves a branch | a full pipeline, plus the architect |

Stop at the first leg that fails and record it (step 18). A leg that passes on a toy brief and
fails on a real one is the finding this drive exists to make.

---

## Leg 0 — Preflight (free)

### 1. Have a `claustrum` on `PATH`

The `.mcp.json` that `sync` writes says `"command": "claustrum"`, and a spawned architect calls
`claustrum run …` from its own shell, so the bare name has to resolve. Publish and install it:

```bash
cd ~/repos/Claustrum
dotnet publish src/Claustrum/Claustrum.csproj -c Release -r linux-x64 -p:PublishAot=true
install -m 0755 src/Claustrum/bin/Release/net10.0/linux-x64/publish/claustrum ~/.local/bin/claustrum
claustrum --version
```

Expect `0.1.0-alpha+<commit>`. `dotnet build` leaves no executable at all (the project is
`PackAsTool`), so it has to be a publish. Windows form and other RIDs: [INSTALL.md §1c](INSTALL.md).

### 2. Ask the doctor what this machine has

```bash
claustrum backends doctor
```

On the owner's box (2026-09-22) it finds all five backends plus `gh`:

| Backend | Found as | Credential today | Usable model ids |
|---|---|---|---|
| `claude` | `~/.npm-global/bin/claude` 2.1.278 | Claude Code login (subscription) | `opus`, `sonnet`, `haiku` |
| `copilot` | `~/.local/bin/copilot` 1.0.87 | logged in (`~/.copilot/config.json`) | `auto` — the only id verified |
| `cursor` | `~/.local/bin/cursor-agent` | logged in, **Free plan** | `auto` — the only id the plan accepts |
| `opencode` | `~/.opencode/bin/opencode` 2.0.12 | **none**: no `OPENROUTER_API_KEY`, no `opencode auth` | needs a key first |
| `api` | `curl` | **none**: neither `OPENROUTER_API_KEY` nor `ANTHROPIC_API_KEY` | `openrouter:<id>` / `anthropic:<id>` |

Two consequences for the drive:

- **The first two legs run on `claude` only.** It is the one backend with a credential, a cost
  figure in its receipt, and a model that `frontier-coding`/`standard-coding` already resolve to.
- **`cheap-coding` is a trap on this machine.** `claustrum init` writes it as
  `opencode:openrouter/deepseek/deepseek-v4-flash`; with no OpenRouter key any cast that names it
  fails at launch. Either export `OPENROUTER_API_KEY` before leg 3, or point the alias elsewhere
  (step 6).

The merged-config block at the bottom of the doctor output lists the built-in aliases: everything
resolves to `claude:*` until a `claustrum.json` says otherwise.

### 3. (Optional, paid) Probe the backends you will use

```bash
claustrum backends doctor claude --probe
claustrum backends doctor copilot --probe
```

One real request each, capped at $0.50 (a cold-cache claude probe measured $0.12; copilot reports
no cost, it spends one premium request). It confirms auth end to end, which the free doctor does not.
⚠ The copilot probe needs a `claustrum.json` at the git root with an alias resolving to
`copilot:*`, otherwise it skips with "no model alias in claustrum.json resolves to this backend".
Do this step after step 6 if you want copilot probed.

### 4. Pick the target repo and the task

The requirements, in order of importance:

- a repo with an `AGENTS.md` or `CLAUDE.md` (every role reads it first and treats it as law);
- a test suite the tester can run from a documented command;
- one open issue that is **small, self-contained and verifiable**, one or two files, a clear
  "done" — a bug with a reproduction, a missing flag, a doc that lies. A feature that needs design
  decisions makes the architect ask questions nobody is there to answer.

A clean working tree in that repo, on the branch the work should land on. Builders that run in
parallel branch from `HEAD`; uncommitted changes are invisible to them and confuse the diff.

Candidates in Claustrum itself (dogfooding, `gh issue list` on 2026-09-22): **#17** (doctor:
mark cursor's prompt-only deny list as advisory) and **#29** (ClaudeSync syncs ui-reviewer with
Edit/Write and no browser tool). Both are one-subsystem fixes with an obvious test. Do the drive on
a **separate clone** or a fresh branch, never on `claude/issue-13-copilot`, which carries
uncommitted work.

```bash
git status --short          # must be empty
git switch -c drive/issue-17
```

### 5. Scaffold the repo

From the repo root (`init` has no `--cwd`):

```bash
claustrum init
git status --short
```

Expect: `claustrum.json` written, `.claustrum/casts/` created, `.claustrum/{briefs,worktrees,locks}`
appended to `.gitignore`, and a sync for the harnesses the repo already shows (`.claude/` ⇒ claude,
`.github/` ⇒ copilot). For Claude Code that means `.claude/agents/<role>.md` (fifteen files, tiers
included), `.claude/skills/claustrum/SKILL.md`, `.mcp.json` and `.vscode/mcp.json`. A second `init`
must say "already present, left unchanged" / "already up to date" and change nothing.

`claustrum.json` and `.claustrum/casts/` are committable. Commit them on the drive branch so the
builders' worktrees (branched from `HEAD`) carry the same cast and aliases:

```bash
git status --short                     # claustrum.json, .claustrum/, .claude/, .github/, .mcp.json, .vscode/
git add -A && git commit -m "Scaffold Claustrum for the test drive"
```

### 6. Fix the aliases for this machine

Edit `claustrum.json` so every class the cast will name resolves to a backend with a credential.
For an all-Claude first drive:

```json
{
  "models": {
    "frontier-reasoning": "claude:opus",
    "frontier-coding": "claude:opus",
    "standard-coding": "claude:sonnet",
    "cheap-coding": "claude:sonnet",
    "fast": "claude:haiku",
    "copilot-auto": "copilot:auto",
    "cursor-auto": "cursor:auto"
  }
}
```

`copilot-auto` and `cursor-auto` are for leg 1's cross-harness check; they are harmless if unused.
`claustrum backends doctor` reprints the merged config with `[Repo]` beside each value it took from
this file — check the five classes moved.

### 7. Write the cast (non-interactive, so it is reproducible)

```bash
mkdir -p .claustrum/briefs
cat > /tmp/drive-answers.json <<'JSON'
{
  "architect": "host",
  "builder": "standard-coding",
  "builder_max_parallel": "2",
  "code-reviewer": "frontier-coding",
  "tester": "standard-coding",
  "ui-reviewer": "not needed",
  "budget": "15"
}
JSON
claustrum cast create --answers /tmp/drive-answers.json --name default
claustrum cast show default
```

The keys are the `key` fields of `claustrum cast questions --json`; the interactive
`claustrum cast new` asks the same questions on the terminal, and `/claustrum` in a chat host asks
them with the host's picker. What lands in `.claustrum/casts/default.json`:

```json
{ "name": "default", "library": "1.0.0",
  "architect": { "mode": "host", "model": null, "tier": null },
  "roles": {
    "builder":       { "model": "standard-coding", "backend": null, "tier": null, "max_parallel": 2 },
    "code-reviewer": { "model": "frontier-coding", "backend": null, "tier": null, "max_parallel": null },
    "tester":        { "model": "standard-coding", "backend": null, "tier": null, "max_parallel": null },
    "ui-reviewer":   null },
  "budget_usd": 15 }
```

`default` is the cast `run`/`delegate` pick up when no `--cast` is given. Budget notes for the
drive: `15` caps the **tree** in leg 3; in legs 1 and 2 each shell `run` without a
`CLAUSTRUM_PARENT_JOB` is its own tree, so the cap is per run. The reviewer at `frontier-coding`
(opus) is deliberate — never a reviewer weaker than the builder it reviews (README, "Why").

---

## Leg 1 — One delegation from the shell

This leg costs one builder and one reviewer run and proves the receipt on a real diff. Everything
is a blocking command that prints one JSON document; keep the outputs.

### 8. Write the builder brief

`.claustrum/briefs/1-builder.md`, with the four fixed H2s and, because the builder is not blind,
a `## Context`:

```markdown
## Task
<the issue title and body, pasted verbatim — `gh issue view 17 --json title,body -q '.title, .body'`>

## Scope
src/Claustrum/Cli/BackendsCommands.cs and the cursor backend's doctor output only. No new files
unless the fix needs one.

## Must still work
`claustrum backends doctor cursor --probe` still runs the paid probe when a model alias resolves
to cursor; `backends doctor` with no name still prints every backend and the gh block.

## Diff
`git diff` of the working tree against HEAD.

## Context
House rules are AGENTS.md (read it first). The deny list for cursor is prompt-only by design
(NOTES.md "The cursor backend"); the fix is to say so in the doctor line, not to change the backend.
Do not write tests: the tester runs after you.
```

### 9. Run the builder

```bash
claustrum run builder --brief-file .claustrum/briefs/1-builder.md --json --stream \
  > /tmp/drive-1-builder.json
jq '{status, model, backend, cost_usd, duration_seconds, changed_files, worktree, branch,
     report_status, shaky: .report.data.shaky, open: .report.data.open_decisions, error}' \
  /tmp/drive-1-builder.json
```

`--stream` echoes the backend to stderr so you can watch; stdout stays one JSON document.
`max_parallel: 2` in the cast means this run gets **its own worktree** under
`.claustrum/worktrees/<job>` on branch `claustrum/<job>` — `worktree` and `branch` are non-null and
the repo's own working tree is untouched. Read, in this order:

1. `status` — `success`, else `error` says why (a `budget_exceeded` here means the cast budget,
   not a spent tree).
2. `report_status` — `ok`. `missing` means the role never wrote its `claustrum-report` fence: the
   model ignored the system body, which is a finding.
3. `report.data.shaky` and `open_decisions` — what the builder admits it guessed.
4. `changed_files` — from git, not from the agent. Compare with `report.data.files_changed`.
5. `cost_usd`, `duration_seconds` — write them down. The toy run was $0.18 / 9 s on sonnet; a
   real slice is the first number this drive produces.

Then look at the diff yourself: `git -C .claustrum/worktrees/<job> diff HEAD`, or
`jq -r .diff /tmp/drive-1-builder.json`.

### 10. Run the blind reviewer, and prove the gate

The reviewer's brief carries **only** `## Task`, `## Scope`, `## Must still work`, `## Diff`.
`## Diff` names the builder's branch so the reviewer can obtain it:

```markdown
## Diff
`git diff main...claustrum/<builder job id>` — the builder's branch, base main.
```

First prove the gate refuses rationale — copy the brief, add a `## Context` section, run:

```bash
claustrum run code-reviewer --brief-file /tmp/2-with-context.md --json; echo "exit=$?"
```

Expect **exit 2** and `blind role: brief carries rationale`, in under a second and with no job
spent. Then the real one:

```bash
claustrum run code-reviewer --brief-file .claustrum/briefs/2-reviewer.md --json \
  > /tmp/drive-2-reviewer.json
jq '{status, model, cost_usd, duration_seconds, changed_files, findings: .report.data.findings}' \
  /tmp/drive-2-reviewer.json
```

`changed_files` must be `[]` — the reviewer is `readonly`, and claude's plan mode enforces it.
Triage the findings yourself: real ones go back to the builder as a new brief (step 8 again, with
`## Context` naming the finding), the rest you note.

### 11. (Optional) The same builder brief on another harness

The cross-harness claim is the whole point of the tool; one run each is enough to see the receipt
shape differ:

```bash
claustrum run builder --brief-file .claustrum/briefs/1-builder.md --json --model copilot-auto \
  > /tmp/drive-1-copilot.json
claustrum run builder --brief-file .claustrum/briefs/1-builder.md --json --model cursor-auto \
  > /tmp/drive-1-cursor.json
```

Expect `cost_usd: null` and `usage: null` from both (neither CLI reports them), `changed_files`
still right (git is the source), and a `report_status: ok` if the role body was honoured. Under a
tree budget a cost-less backend is **charged its whole cap**, so in leg 3 they will look expensive
on the ledger; that is the accounting rule, not a bug (MANUAL, "Budget").

### 12. Run the tester on the reviewed branch

```bash
claustrum run tester --brief-file .claustrum/briefs/3-tester.md --json > /tmp/drive-3-tester.json
jq '{status, cost_usd, duration_seconds, r: .report.data}' /tmp/drive-3-tester.json
```

Its brief may carry `## Context` (not blind) and should name the branch, the repo's gate command
(`dotnet test`, from AGENTS.md) and the behaviour the builder listed under `behaviour_to_cover`.
The report is `commands_run`, `passed`, `failed[{test, root_cause, fault_in}]`.

### 13. Integrate by rebase, never merge

```bash
git rebase drive/issue-17 claustrum/<builder job id>    # conflicts resolve here, on the builder branch
git switch drive/issue-17 && git merge --ff-only claustrum/<builder job id>
claustrum jobs clean                                    # removes finished worktrees, keeps branches
```

Leg 1 is done when the drive branch carries a reviewed, tested fix and you have three receipts with
costs. Record them (step 18) before going on.

---

## Leg 2 — You are the architect (host mode)

The cast says `architect: host`: the agent you are chatting with adopts the role and delegates
through Claustrum. Do it in the Claude desktop app's Code tab or in Claude Code, from the drive
repo — that is the host with the most complete glue.

### 14. Connect the MCP server and load the synced role

`init` wrote `.mcp.json` (`mcpServers.claustrum` → `claustrum mcp`). Claude Code reads it at
session start, so **open a new session in the drive repo** and check the server is up: ask for the
`doctor` tool, or run `claude mcp list` in a terminal. If the tools are missing, the usual cause is
step 1: the app spawns `claustrum` from a process whose `PATH` lacks `~/.local/bin`. The fix is an
absolute `command` in `.mcp.json`, or the binary somewhere the GUI sees.

The repo-level `.claude/agents/architect.md` (synced) shadows the user-level one of the same name,
so `@architect` in this repo is the library's text, with `## Working from a cast` in it.

### 15. Drive one issue as the architect

The sentence is the test. Something like:

> Use the default cast. As the architect, take issue #29: delegate through Claustrum only — builder,
> blind code-reviewer, tester, in that order — and report with the claustrum-report block.

What must happen, and what to check while it runs:

- The architect **writes briefs to `.claustrum/briefs/`** and calls the `delegate` tool (or
  `claustrum run … --cast default --json` in Bash). It must **not** spawn native `Agent`
  subagents: a native spawn runs the role on the host's own model outside the cast, which is the
  failure the cast exists to prevent. Watch the tool calls.
- `claustrum jobs list` in a second terminal shows each delegation as it lands, with its role and
  backend. `claustrum jobs logs <id>` tails one.
- The reviewer's brief has no `## Context`. If the architect trips the gate it gets exit 2 back and
  must fix its brief — that is the gate working.
- A long build may exceed the host's MCP tool timeout. The role knows to use `delegate_async` +
  `job_status` + `job_result` then; if it stalls instead, that is a finding.
- The final message ends with one `claustrum-report` block naming every delegation with its job id.

Cost: the delegations are billed to whatever the cast names (claude here); the architect's own
turns are on the chat subscription. Sum the receipts from `claustrum jobs show <id>` for each job
the report lists.

### 16. Try `/claustrum` once

Type `/claustrum` in the same session. It must run `cast questions`, ask the seven questions with
`AskUserQuestion`, and write a cast with `cast create`. Answer them to create a second cast named,
say, `spawned` with `architect: spawned on frontier-reasoning` — that is the cast leg 3 uses.
(Or write it by hand: it is the step 7 file with `"architect": {"mode": "spawned", "model":
"frontier-reasoning", "tier": null}`.)

---

## Leg 3 — Spawned architect, unattended

### 17. `coordinate` on one issue

`gh` must be authenticated (`gh auth status`) and the repo must have a GitHub remote: the issue
body becomes the brief's `## Task` through `gh issue view`. Then:

```bash
claustrum coordinate --cast spawned --issues 17 --json --stream --timeout 5400 \
  > /tmp/drive-coordinate.json
```

- `--timeout 5400`: the default is 1800 s, and a real pipeline (opus architect, sonnet builder,
  opus review, sonnet tester, each a separate `claude -p`) will not fit in thirty minutes. The
  timeout is the architect's own; each child gets the default unless the architect passes one.
- The architect runs headless on `claude:opus` with a `## Coordination` section appended to its
  system prompt: the cast, the delegate command (`claustrum run <role> --cast "spawned"
  --brief-file <path> --json --cwd …`), the budget rules, and the order to create work branch
  `claustrum/<job id>` from `HEAD` before delegating. Its native `Agent` tool is disallowed.
- Every child it starts inherits `CLAUSTRUM_PARENT_JOB=<job id>`, so the ledger accounts them
  against the cast's `$15`. ⚠ The architect's **own** run is capped at the same `$15` but is not a
  member of the tree, so the worst case is 2× the cast budget. Budget for $30.

Watch from another terminal:

```bash
watch -n 20 'claustrum jobs list | head; echo; claustrum jobs budget <job id>'
claustrum jobs logs <child id>          # any child, live
```

When it returns, read the JSON: `status`, then `report.data` — `delegations[]` with a job id and
status each, `branch`, `closes`, `findings_open`, `shaky`. `claustrum jobs budget <job id>` is the
ledger: one line per child with what it was admitted for and what it spent.

Then inspect the branch it left:

```bash
git log --oneline drive/issue-17..claustrum/<job id>
git diff drive/issue-17...claustrum/<job id> --stat
```

Expect: linear history, a commit citing `Closes #17`, no merge commits, nothing pushed. Run the
gate yourself (`dotnet test`) before trusting the tester's report, then rebase and fast-forward the
drive branch to it and open the PR by hand.

The things most likely to go wrong, with the fix already known:

| Symptom | Meaning | Do |
|---|---|---|
| a child `budget_exceeded` in milliseconds, "$0.00 remaining", while a sibling runs | the sibling reserved the whole remainder | the architect should retry after the sibling finishes; if it reported "tree spent" instead, that is a role-text finding |
| `status: failed`, error "all N '<cast>__builder' slots … stayed unavailable" | over-fanned past `max_parallel` and waited out `--timeout` | note it; the architect was told not to |
| `status: timeout` on the coordinate run | `--timeout` too small for the pipeline | rerun with more; the children that finished are still in `jobs list` and on their branches |
| `report_status: missing` on a child | the model dropped the report fence | keep the job id; the log has the final message |
| exit 2 before any job: `gh issue view … failed` | no remote, not logged in, wrong number | fix the environment, nothing was spent |

---

## 18. Record what happened

The drive is only worth doing if the numbers survive it. In `NOTES.md`, one dated section
("First real task, 2026-09-xx"), keep:

- per leg and per run: role, backend, model, `duration_seconds`, `cost_usd`, `report_status`,
  and whether `changed_files` matched the report — the same five columns every time;
- every deviation from this document: a flag that did not exist, a default that was wrong, a role
  that ignored its text;
- the tree ledger of leg 3 next to the sum of its receipts, and the architect's own cost beside it.

Every defect becomes a GitHub issue **on the board in the same step**, with Status and dates (the
owner's rule 3). Things this drive was not able to prove and that stay open: opencode and `api`
(no key on this machine), ui-reviewer (needs the Browser MCP under claude), `coordinate` from the
MCP door (the `coordinate` tool returns `{job_id, log_path}` and is polled with `job_status`), and
the Windows leg of all of the above.
