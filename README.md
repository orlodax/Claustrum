# Claustrum

```
 ██████╗██╗      █████╗ ██╗   ██╗███████╗████████╗██████╗ ██╗   ██╗███╗   ███╗
██╔════╝██║     ██╔══██╗██║   ██║██╔════╝╚══██╔══╝██╔══██╗██║   ██║████╗ ████║
██║     ██║     ███████║██║   ██║███████╗   ██║   ██████╔╝██║   ██║██╔████╔██║
██║     ██║     ██╔══██║██║   ██║╚════██║   ██║   ██╔══██╗██║   ██║██║╚██╔╝██║
╚██████╗███████╗██║  ██║╚██████╔╝███████║   ██║   ██║  ██║╚██████╔╝██║ ╚═╝ ██║
 ╚═════╝╚══════╝╚═╝  ╚═╝ ╚═════╝ ╚══════╝   ╚═╝   ╚═╝  ╚═╝ ╚═════╝ ╚═╝     ╚═╝
                    one core · two doors · any harness
```

**Harness-neutral coding-agent delegation.** An "architect" in any host — Claude desktop app,
Claude Code, VS Code, Cursor, opencode, GitHub Copilot CLI — delegates builds, reviews and test
runs to agents running on **any other** harness and model (Claude, DeepSeek via opencode +
OpenRouter, Cursor, Copilot, or a bare API call), and gets back one normalized receipt.

The story behind it, with the numbers, is newsletter issue 008:
**[Claustrum — un direttore, qualunque sia il podio](https://orlodax.github.io/tks-newsletter/008-claustrum.html)**
· [English edition](https://orlodax.github.io/tks-newsletter/en/008-claustrum.html).

> **Status: alpha.** M0–M3 have shipped — the five backends, casts, the `claustrum mcp` server,
> parallel builders each on their own git worktree, a budget ledger kept across the whole job tree,
> and `backends doctor --probe`. M4 is in progress: the spawned architect (`claustrum coordinate`)
> and the release pipeline. The measured numbers behind all of it are in [NOTES.md](NOTES.md).

## Why

A team split across four harnesses had one pipeline worth keeping — architect → builders → blind
reviewer ‖ UI reviewer → tester — and it only existed inside Claude Code, because delegation was
Claude's `Agent` tool and the roles were Claude-format files. A discipline that belongs to one
person is not a discipline.

At the same time the cost sheet said to stop choosing models by vendor. Per solved task, cheap
tokens stop being cheap once the human cleanup after a failed run costs more than about eighteen
minutes; and error propagates asymmetrically by role — an architect's mistake is inherited by every
builder, a builder's mistake is caught downstream. So: Opus for the architect and the blind
reviewer, something cheap for builders and the tester, and **never a reviewer weaker than the
builder it reviews**. Routing by role needs one conductor who can talk to every model, from
whatever podium they happen to be on.

## Two doors, one core

Every host on the list gives an in-chat agent exactly two universal hooks: a shell tool and MCP.
So Claustrum has two doors and nothing else:

```
claustrum run builder --brief-file order.md --json     # door 1: a command, blocks, prints one JSON
claustrum mcp                                          # door 2: stdio MCP server — delegate, job_status, cast_questions, doctor …
```

Whichever door you use, the core does the same five things: **prepare** the delegated agent's
instructions (who it is, what it may touch, how it must report), **photograph** the repository with
git, **launch** the agent on the chosen harness and wait, **photograph again** — the difference is
what it really changed — and **read the final report**.

## The work order and the receipt

You hand in a brief with four fixed sections: `## Task` (the requester's words, verbatim),
`## Scope`, `## Must still work`, `## Diff` (how a reviewer obtains it). Non-blind roles may add
`## Context`; for a blind role the runner *refuses* a brief that carries context, plan, rationale,
or a pasted report — the blind review is enforced, not requested.

You get back one `RunResult` regardless of who did the work:

```
outcome ........... success
who ............... builder on DeepSeek V4 Flash, via opencode
files changed ..... 2         ← git says so, not the agent
report
  what I did ...... added the opencode backend
  to be tested .... reading the JSONL events
  decided alone ... nothing, the brief was enough
  shaky ........... the event names are unconfirmed
cost .............. $0.012 · 41 s
```

`files_changed` and `diff` come from the before/after git snapshots, so they are right even for a
harness whose own output does not list edits. The `report` is the fenced `claustrum-report` block
every role must end with — the four lines of the builder's honest report, and `shaky` is the one
the coordinator reads first.

## The cast

Who plays which role is decided once and stored as a file, `.claustrum/casts/<name>.json`,
committable and shared:

```json
{ "architect":     { "mode": "host" },
  "builder":       { "model": "opencode:openrouter/deepseek/deepseek-v4-pro", "max_parallel": 3 },
  "code-reviewer": { "model": "claude:opus", "tier": "xhigh" },
  "ui-reviewer":   null,
  "tester":        { "model": "opencode:openrouter/deepseek/deepseek-v4-flash" },
  "budget_usd":    10 }
```

`/claustrum` in any chat asks seven questions — architect (host or spawned), builders and their
parallel cap, reviewers and tester (or "not needed"), a budget (or "no cap") — and the *tool* owns
the questionnaire (`claustrum cast questions --json`) so the questions and their live options are
identical in every host. `budget_usd: null` disables the cap; `cast show` then says so.

## Roles: one source, every harness

Roles live once, in `roles/<role>/ROLE.md` + `role.json`, and name model **classes**
(`frontier-coding`, `cheap-coding`, `fast`) that each developer maps to real models in
`claustrum.json`. `claustrum sync` renders them into `.claude/agents/`, `.opencode/agents/`,
`.cursor/agents/`, `.github/agents/`, plus a `/claustrum` skill and the MCP registration for each
host. Generated files carry a marker; `sync --check` flags hand edits in CI; unmarked files are
never overwritten.

## Backends

| Backend    | Role injection                                    | Permissions (edit + shell, never push)                 |
|------------|---------------------------------------------------|--------------------------------------------------------|
| `claude`   | `--append-system-prompt-file`                     | `--permission-mode acceptEdits --disallowedTools "Bash(git push*)"` |
| `opencode` | inline agent in `OPENCODE_CONFIG_CONTENT`         | `permission: {"edit":"allow","bash":{"git push*":"deny"}}` |
| `cursor`   | role prefixed to the prompt, fed on stdin         | `--mode plan` for read-only rungs, else `-f`; deny by prompt rule |
| `copilot`  | per-job agent file                                | `--allow-all-tools --deny-tool "shell(git push)"`      |
| `api`      | system message; no tools                          | reasoning-only roles                                   |

Adding a backend is one class implementing `IBackend` (detect, build argv, parse output) and one
registry line. Claustrum launches backends on the OS it runs on — the Windows binary talks to
Windows harnesses, the Linux binary to Linux ones; it never translates paths. Under WSL, where both
worlds are visible at once, `backends doctor --probe` warns when the two disagree.

## Enforced, not requested

- **Blind review** — the runner rejects a brief for a `blind` role that carries rationale.
- **Parallel builders** — with `max_parallel > 1` each job gets its own `git worktree` on a
  `claustrum/<job>` branch; the cap is a semaphore shared by CLI and MCP; integration is by rebase.
- **Budget** — accounted over the whole job tree (`CLAUSTRUM_PARENT_JOB` names the tree); a child
  that would exceed what is left is refused; `claustrum jobs budget <tree>` shows the ledger.

## Two ways to conduct

`architect: host` — the agent you are chatting with adopts the role and issues delegations.
`claustrum coordinate --cast <name> --issues 12,13` — Claustrum spawns the architect too, headless,
with the cast injected and `claustrum` on its allowed tools; the pipeline is the architect's prompt,
not a state machine.

## Build

.NET 10, NativeAOT single binary (Windows, macOS, Linux; ~3 MB).

```bash
dotnet build
dotnet test
dotnet publish src/Claustrum/Claustrum.csproj -c Release -r linux-x64 -p:PublishAot=true
```

Contribution rules — style, comment ceiling, linear history, issues on the
[project board](https://github.com/users/orlodax/projects/6) — are in [AGENTS.md](AGENTS.md);
design rationale in [NOTES.md](NOTES.md).

## Install

A release binary per OS, the .NET global tool, or a build from source —
[docs/INSTALL.md](docs/INSTALL.md) has all three.

```bash
dotnet tool install -g Claustrum
```

Then [docs/TEST-DRIVE.md](docs/TEST-DRIVE.md) walks the first run on a real task, step by step,
and [docs/MANUAL.md](docs/MANUAL.md) is the reference for every verb, file, field and rule.

## Licence

[MIT](LICENSE). Named after the thin sheet of neurons under the cortex that Crick and Koch (2005)
proposed as the conductor binding the brain's specialised regions into one act.
