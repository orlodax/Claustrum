# Claustrum — design notes

Long-form rationale that does not fit the comment cap in `AGENTS.md`. Code points here by section
title. Keep sections dated; when the code changes, update or strike the note rather than leaving it
to rot.

## Why two front doors (2026-09-12)

Every host we care about — Claude desktop/Code, VS Code, Cursor, opencode, Copilot CLI — exposes
exactly two universal hooks to an in-chat agent: a shell tool and MCP. So Claustrum is one core with
a CLI (`claustrum run …`) and a stdio MCP server (`claustrum mcp`), nothing else. No IDE plugin, no
per-host SDK.

## Spawn on the OS you run on (2026-09-12)

Windows and WSL see the same files under different names (`D:\…` vs `/mnt/d/…`). Claustrum never
translates paths: the Windows binary spawns Windows harnesses, the WSL binary spawns Linux ones.
`doctor` warns when a backend resolved from WSL is a `/mnt/c/…` Windows executable, because that
harness would receive Linux paths it cannot open.

## npm shims on Windows (2026-09-12)

`claude` and `copilot` install as `.cmd` shims. `CreateProcess` ignores `PATHEXT`, and `cmd.exe`
mangles `%` and long argument lines. `BinaryLocator` reads the shim; when it matches the standard
`"%dp0%\node_modules\<pkg>\bin\<exe>" %*` shape it runs that executable directly. Otherwise it runs
the `.cmd` and escapes `%` as `%%`.

## Role injection per backend (2026-09-12)

- claude: `--append-system-prompt-file` — file-based to avoid inline JSON through the shim; the
  `--agents` inline form is kept as an opt-in mode.
- opencode: an inline agent in `OPENCODE_CONFIG_CONTENT` with the prompt string embedded, so nothing
  is written into the repo or `~/.config`.
- cursor: no system-prompt hook exists; the role body is prefixed to the prompt.
- copilot: a per-job agent file; `COPILOT_HOME` relocation is verified at M3, with the fallback of a
  job-suffixed file in `~/.copilot/agents/` removed after the run.
- api: the role body is the system message.

## Blind review is enforced, not requested (2026-09-12)

The reviewer must see only the task and the diff. The runner refuses a brief for a `blind` role
that carries `## Context`, `## Plan`, `## Rationale`, or a pasted `claustrum-report` block. This
replaces the prose rule in the original `architect.md`, which depended on the architect's
discipline.

## Parallel builders (2026-09-12)

N builders in one checkout clobber each other. With `max_parallel > 1` each builder job gets its own
`git worktree` on a `claustrum/<job>` branch; the architect integrates by rebase. The cap is a
semaphore in `JobManager`, shared by the CLI and MCP paths, so a spawned architect cannot over-fan.
