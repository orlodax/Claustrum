- **You delegate through Claustrum, which is blocking by nature.** Write the brief to a file first —
  the fixed H2s `## Task`, `## Scope`, `## Must still work`, `## Diff`, `## Access` (ui-reviewer
  and demo-author only) and `## Context` (non-blind roles only) — then run
  `claustrum run <role> --brief-file <path> --json`, adding `--cast <name>` when a cast is in play
  and `--tier xhigh` or `--tier max` for a heavier tier. It returns when the role is done and prints
  exactly one JSON document.
- **When the `claustrum` MCP server is connected**, the `delegate` tool is the same call
  (`role`, `brief`, `cwd`, `tier`, `cast`, …) and blocks the same way. Where a long run would exceed
  your host's own tool-call timeout, use `delegate_async` instead, then poll `job_status` and read
  `job_result` once it reports done.
- **Read the result rather than assuming it:** `status` first, then `error` whenever the status is
  not `success`, then `report` (a builder's `shaky` before anything else), then `changed_files` and
  `diff` — those two come from git snapshots taken around the run, not from the agent's own account
  of itself — and `worktree`/`branch` when the builder ran isolated.
- **Fanning out means starting several runs and then waiting for every one of them** — your shell's
  own backgrounding, or several `delegate_async` jobs polled to done — before you review anything.
  A review over a half-built batch judges a state that will never ship.
- **Running builders concurrently from a shell.** Two blocking `claustrum run` calls in a row are
  sequential — on 2026-10-09 a spawned architect with `max_parallel: 2` ran its two builders 64 ms
  apart, one after the other (#72, drive 2). To run them at once, start each in the background with
  its receipt redirected to a file, and `wait` for all of them in the same command:

  ```sh
  claustrum run builder --cast "<cast>" --brief-file .claustrum/briefs/1-builder.md --json \
    --cwd "<dir>" > .claustrum/briefs/1-builder.result.json &
  claustrum run builder --cast "<cast>" --brief-file .claustrum/briefs/2-builder.md --json \
    --cwd "<dir>" > .claustrum/briefs/2-builder.result.json &
  wait
  ```

  then read each receipt file as you would a single run's output — or use your harness's own
  background-command mode when it has one, reading every receipt before you finish. Never start
  more at once than the cast's `max_parallel`.
