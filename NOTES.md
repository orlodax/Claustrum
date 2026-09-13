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

## npm shims on Windows (2026-09-12, confirmed 2026-09-13, cmd.exe fallback removed 2026-09-13)

`claude` and `copilot` install as `.cmd` shims. `CreateProcess` ignores `PATHEXT`, so a bare `claude`
`FileName` never resolves. `BinaryLocator`/`NpmShimParser` read the shim body and recognize two real
shapes, confirmed against actual installs on this machine: a compiled-binary shim (`claude.cmd` ->
`"%dp0%\node_modules\@anthropic-ai\claude-code\bin\claude.exe" %*`, run directly) and the classic
pure-JS shim (`yo.cmd` -> `"%_prog%" "%dp0%\node_modules\yo\lib\cli.js" %*` where `_prog` is
`%dp0%\node.exe` if bundled else bare `node`, run as `node <script> args`).

Anything matching neither shape used to fall back to `cmd.exe /d /s /c` with `%` doubled to `%%`
(`CmdEscaping`). Measured 2026-09-13: that fallback was actively wrong, not just untested. A quoted
argument came out **split into 3** on the far side of `cmd`'s own re-tokenizing; a literal `%VAR%`
in an argument **still expanded** despite the `%%` doubling (cmd expands during its own parse pass,
before the doubled `%` collapses back to one); and a 9000-char argument through a real shim was
**silently truncated** at cmd's ~8191-char command-line limit — no error, just a corrupted argv on
the far side. Fix: spawn the `.cmd` file directly as `FileName` with the args on `ArgumentList`,
exactly like any other executable, instead of composing the command line ourselves. `CmdEscaping` is
deleted; `BinaryLocator` no longer builds a `cmd.exe` command line anywhere.

Re-measured against a fake `.cmd` shim: a space-containing argument and one with an embedded `"`
both survive as exactly one argument each (no split) — the specific defect this fix targets. The
underlying ~8191-char ceiling is still real, though, because a `.cmd` is always executed by
`cmd.exe` no matter how it's launched; a 9000-char argument through the same shim now fails loudly
(`Win32Exception`/child stderr "The command line is too long.", nonzero exit) instead of silently
truncating. That is strictly better — a visible failure beats corrupted data — but it is a change in
*how* an oversized arg fails, not a removal of the limit; a role/brief that can genuinely exceed it
for an unrecognized `.cmd` shim still needs a guard — the same open item NOTES.md "Role injection
per backend" already flagged and never closed.

## Role injection per backend (2026-09-12, corrected 2026-09-13, correction reverted 2026-09-13)

- claude: `--append-system-prompt-file <job>/system.md`, per PLAN.md A3, with `system.md` as the
  source of truth. The M1 pass concluded this flag "does not exist" from `claude --help` (v2.1.269)
  not listing it, and switched to inline `--append-system-prompt <text>` instead — **that conclusion
  was wrong**: the flag is registered but hidden with `.hideHelp()`, and `claude` silently ignores
  unrecognized flags rather than erroring, so `--help` not listing it and the inline flag "appearing
  to work" both told nothing about whether the file-based flag actually exists. Re-verified
  2026-09-13 against the CLI's own option table, not just `--help`'s rendered output. Now that
  argv goes back to file-based injection, the role body itself no longer rides in an argv string at
  all, so it can't be the thing that overflows a `cmd.exe`-composed command line. That ceiling still
  exists in general for other long arguments through an *unrecognized* `.cmd` shim, now as a loud
  failure instead of silent truncation (NOTES.md "npm shims on Windows"). The `--agents` inline form
  is kept as a possible opt-in mode later.
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

## Two JsonSerializerContexts (2026-09-13)

`Claustrum.Core` cannot see `Claustrum.Roles` types (Core has no project reference the other way,
and must not gain one just to serialize role.json/library.json), so `Claustrum.Roles.Json.RolesJsonContext`
is a second source-generated `JsonSerializerContext`, independent of Core's `ClaustrumJsonContext`.
It only covers the role library's own on-disk DTOs (`RoleLibraryManifest`, `RoleDefinition`,
`SyncManifest`); anything crossing the Roles → Core seam is the plain `RenderedRole` record Core
already declares, not JSON. Architect-approved split, not a builder improvisation.

## Embedded resource naming mangles `-` to `_`, but only in directory segments (2026-09-13)

`Claustrum.Roles.csproj` embeds `roles/**/*` with `LinkBase="roles"`. MSBuild's default manifest
resource name generation replaces `-` with `_` in every path segment **except the final file name**:
`roles/code-reviewer/role.json` embeds as `...roles.code_reviewer.role.json`, but
`roles/_shared/report/code-reviewer.md` keeps its hyphen (`...roles._shared.report.code-reviewer.md`)
because `code-reviewer.md` there is the file name, not a folder. `RoleLibrary.FindResourceName`
mangles `-`→`_` per path segment except the last before doing a suffix search over
`Assembly.GetManifestResourceNames()`, rather than trying to reconstruct the exact prefixed name.
`RoleLibrary.ListRoles()` reads each embedded `role.json`'s own `name` field instead of deriving the
role from the (possibly mangled) resource path, so `code-reviewer` is never seen as `code_reviewer`
by a caller. Do not "simplify" this back to a direct name-to-resource-name mapping — that's exactly
what broke on the first pass (`claustrum run code-reviewer` would 404 against a resource that only
exists as `code_reviewer`).

## Role templating recurses one level into `{{part:name}}` (2026-09-13)

`roles/builder/parts/delegation.claude.md` itself contains `{{delegate.architect}}`. The first build
of `RoleRenderer` did a single `Regex.Replace` pass over `ROLE.md`, so the part's raw text — tokens
and all — was pasted in unresolved and `{{delegate.architect}}` shipped literally into the rendered
agent file. `RoleRenderer.ResolveToken`'s `part:` branch now re-renders the part's text through the
same resolver before returning it. None of the shipped parts reference `{{part:...}}` themselves, but
a `.claustrum/roles/<role>/` local override could, and a `{{part:a}}`↔`{{part:b}}` cycle there would
hit a `StackOverflowException` — uncatchable, kills the process past the new top-level exception
boundary (review finding #3, 2026-09-13). `ResolveToken` now threads a `partDepth` counter and throws
`RoleRenderException` past `MaxPartDepth` (8, comfortably above the one level shipped parts use).

## Tier stubs use their own token set, not `RoleRenderer.Render`'s (2026-09-13)

`_shared/tier-stub.md` (the `-xhigh`/`-max` body template) is rendered by a separate method,
`RoleRenderer.RenderTierStub`, with tokens `{{role}} {{tier}} {{effort}} {{tier_guidance}}
{{non_negotiable}}` — distinct from `ROLE.md`'s `{{harness}} {{role}} {{tier}} {{effort}}
{{house_rules}} {{report_format}} {{part:<name>}} {{delegate.<role>}}` set from docs/PLAN.md §B2.
Tier stubs are deliberately thin (§B1: "already thin → generated from a template, never
hand-written") and never carry ground rules, house rules or report format sections, so they have no
use for `{{part:...}}` or `{{delegate...}}`. `tier_guidance` text (one sentence per tier) lives as a
small switch in `RoleRenderer`, not in the template, since it is fixed vocabulary shared by every
role rather than per-role data.

## ClaudeSync's Claude model mapping is separate from Core's model-class resolution (2026-09-13)

`role.json` tiers name a model **class** (`frontier-coding`, `standard-coding`, ...), resolved to a
concrete backend+model by Core's `claustrum.json` config (docs/PLAN.md §A7) — out of scope for this
slice. A `.claude/agents/<role>.md` frontmatter `model:` field, by contrast, is Claude Code's own
concept and needs a literal `opus`/`sonnet`/`haiku` right now, independent of any Claustrum config
file. `ClaudeSync.ClaudeModelFor` is therefore a small static class→literal-model map local to
`ClaudeSync`, not a call into Core: `frontier-reasoning`/`frontier-coding` → `opus`,
`standard-coding` → `sonnet`, `cheap-coding`/`fast` → `haiku`. This reproduces the owner's existing
roster (`builder: opus`, `code-reviewer: sonnet` at `high` / `opus` at `xhigh`+`max`) without
depending on A7 landing first.

## Generated-file marker covers the body, not the frontmatter (2026-09-13)

The `<!-- claustrum:generated ... sha256=... -->` marker's hash is computed over the rendered body
only (post-`Trim()`), not the YAML frontmatter above it. A frontmatter-only change (e.g. `role.json`
description edited but body untouched) is still allowed to overwrite freely, same as any other
marked file — the marker's purpose is only to distinguish "claustrum wrote this" from "someone
hand-authored this", not to detect staleness (that's `--check`, M2). Skip-vs-write is instead decided
by comparing the **whole candidate file** (frontmatter + marker + body) against what's on disk.

## Claude agent frontmatter keeps `effort` beyond the four fields the brief named (2026-09-13)

`docs/PLAN.md`'s task brief lists `name, description, model, tools, color` for the generated
`.claude/agents/<role>.md` frontmatter. `ClaudeSync.BuildFrontmatter` also writes `effort:` (as the
owner's seed `builder.md`/`code-reviewer.md` do), because it is the only frontmatter field that
differs between a role's base file and its `-xhigh`/`-max` stubs when the model class does not also
change (builder's tiers all resolve to `frontier-coding` → `opus`). Dropping it would make
`builder.md` and `builder-xhigh.md` byte-identical except for `name`/`description`. Additive,
harmless if Claude Code ignores the field; flagged here as a deliberate departure from the literal
field list, not an oversight.
## Model seam records were created by the Core builder (2026-09-13)

`PermissionLevel`, `PermissionPolicy`, `ClaustrumReport`, `RenderedRole`, `ResolvedRole` were briefed
as already existing in `Model/` for the Roles builder to code against, but neither parallel worktree
had them at task start (checked both `git log` and the sibling worktree's file tree). The M1 Core
builder created them from the docs/PLAN.md A2/B2/B3 shapes, with two additions PLAN.md's sketch
doesn't spell out: `RenderedRole.Deny` (the role's own baked-in deny list, concatenated with config
and flag denies in `Config.Resolve`) and `ResolvedRole.Blind` (so `Runner.RunAsync`, which only ever
sees `ResolvedRole`, can run the blind gate without a back-reference to `RenderedRole` or role.json).
If the Roles builder's branch defines these differently, reconcile at rebase — the field list above
is what `Claustrum.Core` actually compiles against.

## Report extraction shape (2026-09-13)

`ClaustrumReport(JsonElement? Data, string RawText)`: `Data` is null only when a fence was found but
was not valid JSON (`ReportStatus.Unparsed`); `RawText` is always kept either way so "unparsed" never
loses the evidence (docs/PLAN.md B3). `RunResult` carries `ReportStatus` and `Warnings` (e.g.
multiple-fences-found) as fields additive to the original A2 sketch, not nested inside `report`.

## Worktree snapshot: renames split into Added + Deleted, content hashed for dirty files (2026-09-13, corrected 2026-09-13)

`git status` reports a rename as one `R` entry — in the index column normally, or in the worktree
column when only the working tree side renamed it — carrying an `OldPath`. The original
`WorktreeSnapshot` recorded only the new path as `ChangeKind.Added` and silently dropped the old
path, so a backend that renamed a file was reported as a plain addition: the same file count as
before the rename, with no record that the old name is gone. `ToChangedFiles` now emits two entries
for a rename — the new path as `Added`, the old path as `Deleted` — and parses the `-z` old-path
field for a worktree-column `R` too, not just the index column. A copy (`C`, index column only)
still folds into a single `Added` at the new path with the source left untouched, since a copy
doesn't remove anything; `git status` here has no `--find-copies`/`-C` though, so `C` is not
actually reachable through this exact invocation.

Comparing status codes alone across before/after also missed edits to a file that was already dirty
going in: same `M` (or `??`) code before and after, content changed in between — the exact
"already-dirty, edited again" case, and the same one that made an untracked file's *further* edit
invisible even though `??` never changes. Each `GitStatusEntry` now also carries a SHA-256 of the
worktree file's current bytes at capture time (null when the file doesn't exist on disk, e.g.
deleted); `DiffAsync` reports a path when its status code *or* its hash changed relative to the
prior snapshot. This is what makes the untracked-but-existing case come out right: an untracked file
edited during the run keeps its git-perspective kind (`A`, not `M`) but is now included because its
hash moved, not because its status letters did.

## Config layer origins for concatenated deny lists (2026-09-13)

`Config`'s per-key `Origins` map (for the future `doctor` command, docs/PLAN.md A7) records a single
winning `ConfigLayer` per key. For `deny`, which concatenates across layers instead of overwriting,
the recorded origin is the *last* layer that added anything to that role's deny list, not the full
set of contributing layers. Good enough to answer "did my config file touch this," not "which layers
built this list" — revisit if `doctor` needs the latter.

## Env allow-list is dead without clearing ProcessStartInfo.Environment (2026-09-13)

`ProcessStartInfo.Environment` is pre-populated by .NET with a copy of the *current* process's own
environment as soon as `UseShellExecute` is `false` — this is documented .NET behaviour, not a
Claustrum default. `ProcessRunner.RunAsync` was only ever adding the allow-listed entries on top of
that full inherited set with `startInfo.Environment[key] = value`, never removing anything, so every
variable Claustrum itself was running with (secrets, unrelated tool config, all of it) leaked into
every spawned backend regardless of `EnvAllowList`. Fix is `startInfo.Environment.Clear()` before
populating from `EnvAllowList.Build` — confirmed with a fake backend exe that dumps its own
environment: a non-allow-listed variable set in the test harness's own process no longer appears in
the child, and a `--env` entry does. `EnvAllowList.Build` itself was already correct; the bug was
entirely at this one call site.

## Backend config and env passthrough are call-site data, not RunRequest fields (2026-09-13)

`backends.<name>.path` and `defaults.env_passthrough` both come out of `Config`, which `Runner` does
not read (`Config.Resolve` already ran by the time `RunAsync` is called, per docs/PLAN.md A3's own
"resolve -> Build -> snapshot" pipeline). Rather than have `Runner` reach into `Config` — a
dependency it deliberately doesn't have — `RunOptions` grew `BackendConfig?` and `EnvPassthroughAll`
the same way it already carries `DiffByteCapBytes`: resolved by the caller, handed to `Runner` as a
per-call knob. `ProcessRunner.RunAsync` gained a matching `envPassthroughAll` parameter and now
passes `options.BackendConfig` through to `BinaryLocator.Locate` instead of the hardcoded `null` that
made `backends.<name>.path` silently unreachable from `claustrum run`. Wiring `ConfigOverrides`/
`config.Merged` into these two `RunOptions` fields is CLI-side (`RunCommand.cs`) and out of the Core
builder's slice — flagged for the CLI builder rather than guessed at here.

## Runner always yields a result after the process ran (2026-09-13)

Two related gaps, one fix: Ctrl+C or a timeout left `Runner.RunAsync` with no `RunResult` at all,
because the after-snapshot and its diff were still run with the same (already-cancelled) token as
the backend process, so `WorktreeSnapshot.CaptureAsync`/`DiffAsync` threw `OperationCanceledException`
before a result could ever be built — the CLI's generic `OperationCanceledException` catch then
exited 130 with no `result.json` on disk to show for the run that did happen. Both calls now always
use `CancellationToken.None`: by the time `ProcessOutcome` exists, cancellation/timeout has already
done its job (`ProcessTermination.Cancelled`/`TimedOut`), and `DetermineStatus` reports it correctly
without needing the token to still be live.

More generally, everything from `ProcessOutcome outcome = await processRunner.RunAsync(...)` through
building and writing `RunResult` is now in a `try`/`catch` that turns any exception into a `Failed`
`RunResult` with `Error` set, still written to `result.json` — a git binary vanishing mid-diff, a
`Parse` bug, anything. Scope is deliberate: it starts *after* `ProcessOutcome` is obtained, i.e. the
backend process has definitely run to completion or been killed. A throw from `processRunner.RunAsync`
itself — most notably `BackendNotFoundException`, when the resolved binary isn't actually on PATH —
is pre-spawn by definition (`process.Start()` was never reached) and still propagates uncaught, same
as before this fix: the CLI already has a dedicated catch mapping it to exit code 3 without a
`result.json`, and changing that contract wasn't asked for.
