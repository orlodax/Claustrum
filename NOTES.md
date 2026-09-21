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

## Worktree snapshot: an unreadable file is unhashable, not fatal (2026-09-14)

`HashWorktreeFile` used to do a bare `File.Exists` + `File.OpenRead` on every path `git status`
listed. A file locked `FileShare.None` by an editor, an antivirus scan, or OneDrive/Dropbox sync —
or one that simply disappears between the two calls — made `File.OpenRead` throw, and that exception
came out of `WorktreeSnapshot.CaptureAsync` with nothing to catch it. `Runner`'s *before* snapshot
called this outside its own guarded section (see the next note), so one locked file destroyed the
whole run: no `result.json`, and over MCP a `delegate_async` job that faulted instead of reporting
`backend_missing`/`failed` with a real message.

Fix: `HashWorktreeFile` now catches `IOException`/`UnauthorizedAccessException` around the read and
returns `null`, exactly like the existing `!File.Exists` branch. Traded-off consequence, accepted
deliberately: a file that is locked during *both* the before and after snapshot now hashes `null`
both times, so `DiffAsync` falls back to comparing only the git status letters for it — a
content-only edit to an already-dirty, already-locked file can be missed in `changed_files`. Strictly
better than destroying the run, but not free; revisit if a real workflow depends on catching that
exact case (e.g. by falling back to size+mtime like the non-git `ScanFiles` path does).

## Runner's before-snapshot is now inside a guarded section too (2026-09-14)

Docs/PLAN.md's "Runner always yields a result after the process ran" only ever guarded the *after*
snapshot onward (the `try` starting where `ProcessOutcome outcome` is already in hand) — the *before*
snapshot and `backend.Build` ran as plain statements ahead of it, so any exception there (a bad `cwd`
that fails `git`'s `CreateProcess`, or any future `IBackend.Build` failure) escaped `RunCoreAsync`
entirely and faulted the whole job/process instead of producing a `Failed` `RunResult`. Now the
before-snapshot + `backend.Build` run in their own `try`/`catch (Exception)`, returning
`WriteResult(job, FailureResult(job, role, outcome: null, ex))` — `FailureResult` takes a nullable
`ProcessOutcome` so it can report `ExitCode: -1`/`DurationSeconds: 0` when the backend process never
even started. No `finally`/`DeleteTempFiles` is needed for this section: `spec` does not exist yet if
`backend.Build` is what threw, so nothing was created to clean up.

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
backend process has definitely run to completion or been killed.

2026-09-14: `processRunner.RunAsync` itself throwing `BackendNotFoundException` — the resolved binary
isn't actually on PATH — used to still propagate uncaught past this point, on the reasoning that
`process.Start()` was never reached so it wasn't yet "a process that ran". That silently skipped the
CLI's own exit-3 mapping's `result.json`, and left the MCP door with nothing to map at all (the MCP
SDK's own generic "An error occurred invoking '...'" swallowed the real message). `RunAsync` now
wraps just that call in its own `try`/`catch (BackendNotFoundException)` and returns the same
`BackendMissing` `RunResult` the unregistered-backend-name branch above already produced, just with
the binary-not-on-PATH message instead — one `MissingBackendResult(job, role, errorMessage)` builder,
two callers. `ExitCodeFor(RunStatus.BackendMissing)` still maps to exit 3 on the CLI, unchanged.

## A cast is call-site data, not a Core concept (2026-09-14)

Same category as `RunOptions.BackendConfig`/`EnvPassthroughAll` (NOTES.md "Backend config and env
passthrough are call-site data, not RunRequest fields"): `Cast`/`CastStore`/`CastResolution`/
`CastApplication` all live in `src/Claustrum/Casts/`, not `Claustrum.Core`. `Runner`/`Config` never
see a cast — `CastApplication.Resolve` folds a cast's role entry into `ConfigOverrides` and the
render tier *before* `Config.Resolve` ever runs, at the one call site (`RunCommand`, and now the MCP
`delegate`/`delegate_async` tools) that already builds those overrides from its own flags/parameters.
This keeps M2's cast feature from touching M1's Core/Roles projects at all — the only Core change
this milestone needed was `Config.ResolveModelBackend` (the cast questionnaire's live-options filter)
and the `Runner.RunAsync(..., JobPaths, ...)` overload (`delegate_async` needs the job id before the
run finishes).

## CastBudget: a plain `decimal?` can't tell "no cast" from "cast says unlimited" (2026-09-14)

docs/PLAN.md §D1: a cast's `budget_usd: null` means the cap is disabled for that cast — authoritative
over `claustrum.json`'s `defaults.budget_usd`, not a "no opinion, fall through" value. But when no
cast is active at all, the *existing* `defaults.budget_usd` fallback must still apply. Both cases
present as C# `null` if the cast's budget were folded into `ConfigOverrides.BudgetUsd` directly (the
same trick used for `Model`/`Backend`), so `CastBudget` is a one-field wrapper: `CastBudget? castBudget
= null` means no cast is active (fall back to config); `new CastBudget(null)` means an active cast
says unlimited (stop there, do not fall back). `CastResolution.ApplyBudget` is the three-way
precedence (flag > active cast > config default), kept pure/AppServices-free specifically so this
tri-state logic has direct unit coverage without spinning up a real cast file on disk.

## MCP tool registration, confirmed for real (2026-09-14)

docs/PLAN.md §A1 flagged `ModelContextProtocol` 2.2.0's `WithTools<T>(JsonSerializerOptions)` overload
as UNCONFIRMED, "verify at M2 by AOT-publishing with zero IL2026/IL3050 warnings." Confirmed, by
reflecting over the installed package (not by reading docs) and then proving it three ways: (1)
`dotnet publish -r linux-x64 -p:PublishAot=true` on this machine (has clang + zlib1g-dev) produces
zero trim warnings with all 10 tools registered; (2) the published native binary answers a real stdio
JSON-RPC session correctly — `initialize`, `tools/list`, and every `tools/call` route to the right
method and return well-formed results; (3) a `delegate` call against the real `claude` CLI installed
on this machine actually created a file, returned `status: "success"`, and the diff/report round-
tripped. Shape: `Host.CreateEmptyApplicationBuilder` → `builder.Logging.AddConsole(o =>
o.LogToStandardErrorThreshold = LogLevel.Trace)` (stdout is JSON-RPC only) → `AddMcpServer()
.WithStdioServerTransport().WithTools<ClaustrumTools>(jsonOptions)`, where `jsonOptions.TypeInfoResolver
= JsonTypeInfoResolver.Combine(ClaustrumJsonContext.Default, CastJsonContext.Default,
McpJsonContext.Default)` — one combined resolver over every source-generated context a tool's
parameters or return type can reach. `ClaustrumTools` must be a non-static class (`static class` can't
be a generic type argument for `WithTools<TToolType>`), with the tool methods themselves `static`.

## JSON sync targets are tracked by hash in sync-manifest.json, not an inline marker (2026-09-14)

`.mcp.json`/`.vscode/mcp.json` (docs/PLAN.md §B4/§D5) have no room for the `<!-- claustrum:generated
... -->` comment `ClaudeSync.WriteGenerated`'s Markdown targets carry (`SyncManifestFile`'s own doc
comment already flagged this: "so a future non-marker target (e.g. JSON) is still idempotent").
`McpConfigSync` instead: computes `JsonNode.DeepEquals` between the existing `claustrum` key and what
it would write (skip if equal, formatting/property-order-independent); when they differ, treats the
existing key as "ours to overwrite" only if `sync-manifest.json` has a recorded hash for that path
that matches the *current* on-disk entry's hash (i.e., claustrum wrote it last time and nothing else
has touched it since), otherwise foreign (needs `--force`). Everything else in the file — sibling
`mcpServers`/`servers` entries, unrelated top-level keys — is preserved because only `root[sectionKey]
["claustrum"]` is ever assigned. Skipped entirely under `--global`: these are repo-root files, not one
of §B4's `--global` targets (agent directories, the desktop app's own config file).

## The synced skill is named `claustrum`, not `delegate` (2026-09-14)

docs/PLAN.md §D2: "the synced per-harness skill is named `claustrum`... it replaces the `delegate`
skill" once the cast questionnaire exists — Claustrum owns the questions, the skill is only the UI
that asks them with the host's native mechanism. `ClaudeSync.WriteSkill` now writes
`.claude/skills/claustrum/SKILL.md`; the body leads with "run `cast_questions`, ask, then
`cast_create`" before the unchanged delegation instructions. A repo that already has the old
`.claude/skills/delegate/SKILL.md` from an M1 sync keeps it untouched (different path, sync never
looks there) — not cleaned up automatically, since deleting a file a human might have since edited is
not something `sync` does anywhere else either.

## MCP child stdin inheritance hung git under a real host (2026-09-14)

`delegate` hung forever (measured >45s, waited 90s; CLI `run` finishes the same request in ~0.25s),
but only against a real MCP host — one that writes `initialize`/`tools/call` and keeps stdin open, as
every real host does. A probe whose stdin hits EOF (e.g. `echo '...' | claustrum mcp`) shuts the
server down as soon as input ends, tearing the process down mid-`delegate` before it can hang — which
is how the PR that shipped this bug's own manual test passed. Measured live: `Get-CimInstance
Win32_Process` during the hang showed a child `git status --porcelain=v1 ...` still alive 20s in,
where the same command run by hand finishes in 0.037s; the job directory held only `request.json` and
`system.md`, i.e. it never got past `WorktreeSnapshot.CaptureAsync`, well before `ProcessRunner` ever
spawns the backend.

Root cause: none of the three spawn sites (`WorktreeSnapshot.RunGitAsync`, `ProcessRunner.RunAsync`,
`ClaudeBackend.DetectAsync`) redirected `StandardInput`, so every child inherited the MCP server's own
stdin — the live JSON-RPC pipe the host is writing into and the stdio transport is concurrently
reading. A child inheriting that pipe can block on it (or steal protocol bytes from the host stream).
Fix: `RedirectStandardInput = true` at all three sites, `Close()`d immediately after `Start()`. Safe to
close unconditionally — no backend or `git` invocation here is ever fed anything over stdin; `claude`
takes its brief as a positional argv element (`ClaudeBackend.Build`), and `git`'s own stdin is never
read by the flags this codebase passes it. `RunGitAsync` additionally got a 30s defensive timeout (see
its own `gitTimeout` field) so a git that hangs for some *other* reason can no longer wedge a tool
call forever either — belt-and-suspenders, not the fix itself.

## The stdin hang reproduces on Windows only, and its regression test had to be calibrated (2026-09-14)

Measured while auditing M2's test coverage, by reverting all three `RedirectStandardInput`/`Close()`
pairs and re-running `Claustrum.Tests`' `McpStdioServerTests`:

- **Windows:** `delegate` over real stdio answers after **30.16s** with `isError` and the SDK's generic
  "An error occurred invoking 'delegate'" — 30s being `RunGitAsync`'s own defensive timeout, not a
  recovery. Fixed, the same call answers in **0.20s** with `status: backend_missing`.
- **Linux (WSL, same source):** the reverted code **passes** in 2.6s. `git` does not block on the
  inherited pipe there, so on Linux alone no test can distinguish the defect from the fix.

Two consequences. The assertion has to be on the *payload*, not on "a response arrived" — a test that
only waited for a reply would pass against the bug. And CI must run the Windows leg for that test to
be load-bearing at all; `ProcessRunnerTests.AChildThatReadsStdinSeesEofInsteadOfBlockingAsync` is the
platform-neutral half, catching a `RedirectStandardInput` left without its `Close()` on either OS.

## An unreadable worktree file kills a whole run before any result exists (2026-09-14)

`WorktreeSnapshot.CaptureAsync` runs `HashWorktreeFile` on every path `git status` lists, and that is
`File.Exists` followed by `File.OpenRead` — a file that is locked, or that vanishes between the two,
throws out of `CaptureAsync`. Runner calls it *before* the guarded section ("Runner always yields a
result after the process ran" starts after `ProcessRunner` returns), so the exception escapes
`RunCoreAsync` entirely: CLI `run` prints "error: The process cannot access the file …" and exits 1,
and MCP `delegate_async` leaves the job `failed` with no `result.json`.

Reproduced deliberately: one file in the repo held open with `FileShare.None` is enough (measured
2026-09-14). This is the explanation for the one-off `job_status → "failed"` seen during M2 review
verification on a job that should have ended `backend_missing`: that probe put `CLAUSTRUM_HOME`
*inside* the scanned repo, so concurrent job files were being created and written while the snapshot
hashed them. `JobManager.GetStatus`'s own mapping is not racy — a `Task` never goes `Faulted` →
`RanToCompletion`. Not fixed here (it is M1 Core code, outside the M2 review's eleven findings); the
fault shape is pinned by `JobManagerTests.AJobWhoseWorktreeSnapshotCannotRunReportsFailedAndRethrows…`.

## Worktree isolation for max_parallel builders (2026-09-18, issue #4/M3)

`docs/PLAN.md` §D4 needs two things a job with `max_parallel > 1` doesn't otherwise get: its own git
working directory, and a cap on how many run at once. Both are cross-process concerns — a spawned
architect fans builders out as separate `claustrum run` CLI processes (docs/PLAN.md §D3), not as
calls inside one long-lived server — so neither could be an in-memory data structure scoped to a
single `JobManager` instance the way a naive "per-cast semaphore" reading might suggest.

- **Isolation**: `JobWorktree.AddAsync` (`Core/Git/JobWorktree.cs`) runs `git worktree add
  .claustrum/worktrees/<job> -b claustrum/<job>` against the *request's* cwd, built on a new
  `GitProcess` helper extracted from `WorktreeSnapshot`'s private git runner so the stdin-close and
  30s-timeout fixes above are not re-risked at this second call site. `RemoveAsync` deletes the
  working directory only (`git worktree remove --force`); the branch survives for the architect to
  rebase from, per §D4's own wording — nothing yet calls `RemoveAsync` (that is `claustrum jobs
  clean`, not built in this slice).
- **Concurrency cap**: `RoleConcurrencyGate` (`Core/Jobs/RoleConcurrencyGate.cs`) is `maxParallel`
  numbered lock files under `.claustrum/locks/<role>.<n>.lock`, each held open with `FileShare.None`
  for the lifetime of one run; a caller that finds every slot taken polls every 50ms. This works
  identically whether every caller is one process's `JobManager` or N separate CLI invocations,
  because the OS — not the caller's process — is what's actually serializing the opens. The lock
  files are deliberately never deleted: unlinking one while a second process races to reopen the same
  path would let that process's lock land on an orphaned inode while a third process opens a *new*
  file at the same path and takes the "same" slot, double-booking it. Left in place, the files are a
  few bytes each and the exclusivity check keeps meaning what it says for the life of the repo.
- **Wiring**: `DelegateEngine.RunAsync` only takes this path when `DelegateRequest.MaxParallel > 1`
  (threaded from `CastRoleEntry.MaxParallel`, i.e. a cast's `"max_parallel"` — docs/PLAN.md §D1's
  example). Config, tier, and harness resolution still read the request's original cwd; only the
  `RunRequest` that actually reaches `Runner`/`ProcessRunner`/`WorktreeSnapshot` gets the worktree
  path, so `changed_files`/`diff` are computed inside it as §D4 specifies. The gate is keyed on role
  name alone, not role+cast: nothing in the acceptance criteria (3 parallel builders, no clobbering)
  needed per-cast pools, and adding that axis now would be untested surface. `RunResult.Worktree`/
  `Branch` are additive nullable fields (`schema_version` stays `"1"`), populated only on this path.

Not done in this slice: the questionnaire (`cast_questions`/`cast new`) does not yet ask for
`max_parallel` — a cast file has to set it by hand today. `budget_usd` enforcement across a job tree
(§D4's second sentence) also waits for `coordinate` to exist (M4/issue #5); only the per-job cast
budget already wired in M2 is in scope here.

Verified end-to-end (2026-09-18): three `claustrum run builder --cast default` CLI processes started
concurrently against a real `claude` backend and a toy repo with `"max_parallel": 3` landed on three
distinct branches/worktrees, each `status: success` with its own single-file `changed_files` entry,
and the main checkout was untouched throughout — the exact §D4/M3 "done when" scenario.

## The api backend (2026-09-18, issue #4/M3)

`curl` is the process this backend actually spawns — no HTTP client SDK, no extra dependency, and it
fits `IBackend.Build -> ProcessSpec` (Core/Backends/Api/ApiBackend.cs) without any change to Core's
architecture. Two things drove the shape:

- **No tools at all** (docs/PLAN.md §A3): unlike every other backend, `api` cannot edit files or run
  shell commands — it is a single chat completion. That is why only text-report roles list it in
  `harnesses` (code-reviewer already did; builder/tester/ui-reviewer never will), and why `Build`
  ignores `Permission`/`Deny` entirely — there is nothing to gate.
- **Model spec is `api:<provider>:<model-id>`**, e.g. `api:openrouter:deepseek/deepseek-v4-pro` or
  `api:anthropic:claude-opus-4-5`. `Config.SplitBackendModel` only strips the *first* colon, so the
  provider segment survives inside `ResolvedRole.Model` for `Build` to split again. Passing
  `--backend api` and `--model openrouter:...` as two *separate* flags does **not** work the same
  way: `Config.Resolve` always runs the alias/backend split on the raw `--model` value regardless of
  `--backend`, so `openrouter:` would be consumed as if it were a (wrong) backend name and lost.
  Verified this end to end with the real CLI (2026-09-18): the two-flag form silently drops the
  provider prefix and `Build` throws "got 'deepseek/deepseek-v4-pro'"; the single combined
  `--model api:openrouter:deepseek/deepseek-v4-pro` form resolves and reaches `Build` correctly.

The request body and the `Authorization`/`x-api-key` header go into two temp files under
`run.JobDirectory` (`-d @file`, curl's `-K`/`--config` for headers) instead of argv, so neither the
API key nor a possibly-large brief shows up in `ps`; both are in `ProcessSpec.TempFiles`, so
`Runner`'s existing `finally` deletes them regardless of outcome. `--fail-with-body` makes curl exit
nonzero on an HTTP error while still returning the body on stdout, so `Parse` can extract the
provider's own error message either way.

Fixtures (`tests/fixtures/api/`) are fabricated from OpenRouter's and Anthropic's published response
shapes, not recorded from a live call — this environment has no `OPENROUTER_API_KEY`/
`ANTHROPIC_API_KEY` to test against, so, like opencode/cursor/copilot, this backend is best-effort
until validated against a real account (docs/PLAN.md's own M3 UNCONFIRMED list).

## The opencode backend (2026-09-18, issue #4/M3)

Verified against a real `opencode-ai` 1.18.31 install (`npm install -g opencode-ai`, available in this
sandbox — unlike Cursor/Copilot's CLIs it installs cleanly with no account). No working provider
credential was available (no `ANTHROPIC_API_KEY`/`OPENROUTER_API_KEY` reachable from here), so a
genuine successful run could not be recorded, but everything short of that was confirmed live:

- **`OPENCODE_CONFIG_CONTENT`** (JSON, `{"agent":{"<name>":{"mode":"primary","prompt":"{file:<path>}"}}}`)
  really exists and is read by the binary, `{file:...}` substitution included — grepped straight out
  of the installed executable's own source strings, not just inferred from docs.
- **`OPENCODE_PERMISSION`** is a *separate*, simpler env var for the permission JSON — confirmed live
  the same way. The original plan guessed permission had to live nested inside
  `OPENCODE_CONFIG_CONTENT`; the real binary reads it standalone, which is what `OpencodeBackend.Build`
  now does (`OpencodeBackend.cs`).
- **`--variant`** (not guessed anywhere in the original plan) is opencode's reasoning-effort flag —
  found via `opencode run --help`, now carrying `ResolvedRole.Effort` the way `--effort`/`--variant`
  do for claude/copilot.
- **`run --format json`** emits one JSON object per line, each with `type` and `sessionID`. A real
  `type:"error"` event was captured (`tests/fixtures/opencode/error.jsonl` — genuine, not fabricated)
  by pointing `run` at a real agent/config with no reachable model; the process exited 1, consistent
  with claude/api's own exit-code-driven `IsError`, so `Parse` did not need special-casing there.
  End-to-end verified too: a real `claustrum run builder --backend opencode` against this same
  failure mode produced a correct `status:"failed"` RunResult with `error`/`session_id` pulled straight
  out of that JSON event.
- **Not independently confirmed**: the shape of a *successful* run's events. `message.part.updated`
  and `step-finish` are real event/part-type strings found in the binary (opencode's public SDK
  documents a part union including `text`/`step-finish`, and `step-finish` carries `cost`/`tokens`),
  but no live success was captured to pin the exact field layout — `success.jsonl` is built from that
  published shape, flagged the same way `tests/fixtures/api/`'s fixtures are. `Parse` treats a
  repeated `message.part.updated` for the same part id as a full-state replacement, not an append,
  because a *separate* `message.part.delta` event name also exists in the binary for incremental
  chunks — if that assumption is wrong, this is the first place to look.

Cursor's own CLI could not be probed the same way: the `cursor-agent` npm package is an unrelated
third-party tool ("Task sequence creator for Cursor AI agents"), not Cursor's real CLI, which ships as
a standalone installer script rather than an npm package — installing and then discarding it here so
it doesn't get mistaken for the real thing later. Cursor stays fixture-only per the original plan
("cursor validated by a teammate who has it").

## The copilot backend (2026-09-18, issue #4/M3)

Verified against a real `@github/copilot` 1.0.86 install (`npm install -g @github/copilot`). No
GitHub Copilot subscription token was reachable from here — the sandbox's own repo-scoped
`GITHUB_TOKEN` is a narrower credential meant for something else and was deliberately not pressed
into this instead — so, unlike opencode, not even an authenticated *error* shape could be recorded,
only the pre-auth failure path. Still a large upgrade over the original plan's guesses:

- **`-C`, `--agent`, `--output-format json` (JSONL), `--mode` (`interactive|plan|autopilot`),
  `--reasoning-effort`** (not `--effort`; `high`/`xhigh`/`max` are valid values there, a lucky exact
  match with Claustrum's own tier names) **all confirmed live** via `copilot --help`.
- **`--add-dir <dir>` "loads that directory's `.github/skills` and `.github/agents` as trusted
  configuration"** — confirmed live, and a real, cwd-relative mechanism rather than the `COPILOT_HOME`
  relocation the original plan guessed (`copilot help environment` confirms `COPILOT_HOME` only
  relocates config/state, nothing about `agents/`). `CopilotBackend.Build` writes
  `<job>/copilot-agents/.github/agents/claustrum-<role>.agent.md` and passes `--add-dir
  <job>/copilot-agents`, so the target repo itself never needs a Claustrum file committed into it.
- **`--allow-tool`/`--deny-tool` with `shell(...)`/`write` tool names** confirmed live from `copilot
  --help`'s own examples (`--allow-tool='shell(git:*)' --deny-tool='shell(git push)'`,
  `--allow-tool='write'`). **`--allow-all`** (equivalent to `--allow-all-tools --allow-all-paths
  --allow-all-urls`) is a real single flag, used for Full instead of the two-flag combination the
  original plan guessed.
- **Deliberate deviation from docs/PLAN.md §A3's ReadOnly/Edit rows**: `--help` states
  `--allow-all-tools` is "required for non-interactive mode", so a mapping that omits it (as those two
  rows originally did) risks `-p` hanging on a confirmation prompt nothing can ever answer headlessly
  — a real, well-documented risk, not a hypothetical one. `PermissionArgs` instead grants broadly with
  `--allow-all-tools`/`--allow-all-paths` at every level and narrows with `--deny-tool`, the same
  allow-broad-deny-narrow shape `ClaudeBackend`'s own EditShell mapping already uses, and relies on
  `--mode plan` (not tool denial) to keep ReadOnly's *effect* read-only.
- **Confirmed live, and load-bearing for `Parse`**: an unauthenticated/fatal-startup failure prints a
  human-readable message to stderr with **empty stdout**, exit code 1 — not a JSON error object the
  way opencode's own startup failures are (`tests/fixtures/copilot/auth-failure-stderr.txt`, a genuine
  capture). End-to-end verified too: a real `claustrum run builder --backend copilot` against this
  same failure reached a correct `status:"failed"` RunResult with that exact message as `error`.
- **Not confirmed at all**: the JSONL shape of a *successful* run, or the `.agent.md` frontmatter
  schema — no authenticated session was reachable, and unlike opencode's binary, `@github/copilot`'s
  is stripped (no useful event-name strings to recover). `Parse` therefore tries several plausible key
  names (`content`/`text`, nested under `message`; `input_tokens`/`prompt_tokens` and their `output`
  counterparts for usage) rather than committing to one guessed shape, and always keeps the raw JSON
  in `Raw` so a real failure here is diagnosable rather than silently wrong. `success.jsonl` and the
  `.agent.md` frontmatter are both flagged best-effort, same as the other M3 backends.

## The cursor backend (2026-09-18, issue #4/M3): fixture-only, as the plan already expected

*Superseded 2026-09-21 by "The cursor backend, validated against a real install" below — kept as the
record of what was guessed and why.*

Unlike opencode/copilot, Cursor's real CLI could not be installed here at all — it ships as a
standalone installer script (`curl https://cursor.com/install -fsS | bash`), not an npm package, and
this environment's network policy plus the lack of a Cursor account make that install unverifiable
either way. `CursorBackend.cs` implements docs/PLAN.md §A3's cursor row exactly as written — binary
name `cursor-agent`, prompt-prefix injection (no system-prompt hook, so the rendered role body is
prefixed onto the brief with `# Task`), the deny list turned into a `## Hard rules` prompt section
(cursor has no native per-command deny flag), and the `-p --output-format json ... --workspace <cwd>`
argv/permission table — with zero live confirmation. `tests/fixtures/cursor/*.json` are fabricated
from the plan's own guessed field names (`result`/`session_id`/`usage`/`is_error`). This is the one
M3 backend that stays exactly as unconfirmed as the original plan already flagged it
("cursor validated by a teammate who has it") — nothing here upgrades that status.

One deliberate deviation from the plan's table landed in review: ReadOnly passes `-f` rather than
`--mode ask`, and states the no-edit rule in the prompt instead. See "M3 review fixes" below for
why, and treat it as the first thing the teammate validation should check.

## OpencodeSync: agent + command files, verified against a real install (2026-09-18, issue #4/M3)

opencode ships its own first-party "Customizing opencode" reference doc *inside the binary itself*
(readable with `strings` on the unstripped executable — see NOTES.md "The opencode backend" for how
that binary was obtained) — an authoritative source better than public docs for exactly this kind of
detail, and it settled several things the original plan only guessed at:

- **Commands, not skills, are the slash-command mechanism.** opencode has both `.opencode/skill(s)/
  <name>/SKILL.md` (auto-surfaced reference material the model may or may not read, matching Claude
  Code's own skill semantics) and `.opencode/command/<name>.md` (a literal `/name` slash command,
  frontmatter `description`/`agent`/`model`/`variant` + a body template with `$ARGUMENTS`). Only the
  second one makes `/claustrum` an actual typeable command, so `OpencodeSync.WriteCommand` writes
  `.opencode/command/claustrum.md`, reusing the exact same shared body
  (`roles/_shared/claustrum-skill.md`) ClaudeSync's own `SKILL.md` uses — the interview and
  delegation steps are harness-neutral by construction.
- **Project agents**: `.opencode/agent/<name>.md` (or `.opencode/agents/`), global:
  `~/.config/opencode/agent(s)/<name>.md` (NOT `~/.opencode/`). Allowed frontmatter fields:
  `name, model, variant, description, mode, hidden, color, steps, options, permission, disable,
  temperature, top_p`; the file body becomes the agent's prompt. `model` always carries a provider
  prefix (`"provider/model-id"`), confirmed by the same doc's own shape notes.
- **Verified live, not just read**: synced `.opencode/agent/builder.md` (+ `-xhigh`/`-max` stubs) and
  `.opencode/command/claustrum.md` were written into a real temp repo, then `opencode agent list`
  (against the real opencode-ai 1.18.31 install) printed `builder (subagent)`, `builder-xhigh
  (subagent)`, `builder-max (subagent)` — proof the frontmatter shape is genuinely accepted by
  opencode's own strict config validation ("opencode hard-fails on invalid config"), not just
  plausible-looking. The command file could not be verified the same way (no `commands list`
  equivalent was found), so it rests on the same authoritative source, one notch less confirmed than
  the agent files.
- **One inference, not directly confirmed**: the doc's condensed examples never show `---` YAML
  frontmatter delimiters (just `key: value` lines running straight into the body), which is almost
  certainly the doc's own formatting shorthand rather than the real file syntax — `---`-delimited
  frontmatter is what every other tool here uses (Claude Code's own agent files included) and is what
  `WriteAgent`/`WriteCommand` emit. If a real sync round-trip ever shows opencode misparsing the
  frontmatter, this is the first place to check.
- **Model class -> concrete id mapping** (`OpencodeModelFor`) only uses the two model ids
  docs/PLAN.md itself ever actually names (`openrouter/deepseek/deepseek-v4-pro` and `-flash`) rather
  than inventing a third, unconfirmed id for `standard-coding`.
- **Deliberately out of scope**: registering the claustrum MCP server in `opencode.json`'s own `mcp`
  key (opencode has one, confirmed live in the same reference doc) — that merge needs the same
  idempotency/foreign-key care `McpConfigSync` gave `.mcp.json`, and deserves its own dedicated pass
  rather than being bolted onto this one.
- **Duplication accepted for now**: `OpencodeSync`'s marker/idempotency machinery
  (`WriteGenerated`/`ComputeSha256`/`HasMarker`/manifest-free by design) is a near-duplicate of
  `ClaudeSync`'s own. With only two concrete Sync classes so far the right shared shape isn't obvious
  yet (`ClaudeSync`'s five-out-parameter methods are already a smell) — extracting it now would be
  guessing from two data points; the plan is to do that once Cursor/CopilotSync exist too.

## CopilotSync: agent + skill files, verified against a real install (2026-09-18, issue #4/M3)

`.github/agents/<role>.agent.md` (+ tier stubs) and `.github/skills/claustrum/SKILL.md`, checked
against the same real `@github/copilot` 1.0.86 install as the copilot backend.

- **No slash-command mechanism exists in Copilot CLI** (confirmed by its full `--help`: no `command`
  subcommand, nothing resembling opencode's `.opencode/command/`). Its only reusable-instruction
  mechanism is skills (`copilot skill list`/`add`/`enable`), which are auto-surfaced by relevance —
  "Use when the user mentions..." is the *built-in* skills' own phrasing — not typed as `/name`. So
  unlike Claude Code and opencode, `/claustrum` cannot be made a literal typeable command on this
  harness; `CopilotSync.WriteSkill`'s description front-loads trigger phrasing instead, and this is a
  real, confirmed limitation of the harness, not a gap in the implementation.
- **Verified live**: a real `.github/skills/claustrum/SKILL.md` (`---`-delimited `name`/`description`
  frontmatter, same shared body as every other harness's own `/claustrum`) was written into a temp
  repo and `copilot skill list` printed it under "Project skills" with its exact description — no
  authentication needed for this check, and it round-tripped byte-for-byte. This is the single
  strongest live confirmation across all four non-claude backends/syncs, because it uses the *exact*
  file this code writes, not an analogous probe.
- **`.github/agents/*.agent.md` frontmatter stayed unconfirmed** (same caveat as the copilot backend's
  own ephemeral agent files) — no authenticated session was reachable to check whether Copilot
  actually loads a persisted project agent file the way `--add-dir`'s help text implies. `model: auto`
  is used for every role/tier (`copilot --help`'s own "use 'auto' to let Copilot pick automatically"):
  only one real model id (`gpt-5.4`, from a --help example) was ever confirmed, nowhere near enough to
  build a tier catalog, so no id was invented the way ClaudeSync's/OpencodeSync's model mappings are.
- Personal/global skill location (`~/.copilot/skills/`) is directly confirmed by `copilot skill
  --help`'s own text; the personal *agent* location (`~/.copilot/agents/`, used for `--global`) is an
  unconfirmed extrapolation from that same convention.

`sync --only claude,opencode,copilot` (any comma-combination) all work through the same
`SyncCommand.MergeResults`; cursor still has no Sync class (fixture-only backend, matches NOTES.md
"The cursor backend").

## claustrum init (2026-09-18, issue #4/M3)

docs/PLAN.md §A5/§D5's `init` verb: scaffolds `.claustrum/{casts,briefs,worktrees}`, writes
`claustrum.json` with the two model aliases the plan itself names (`frontier-coding -> claude:opus`,
`cheap-coding -> opencode:openrouter/deepseek/deepseek-v4-flash`) if one doesn't already exist, appends
`.claustrum/worktrees/`/`.claustrum/briefs/` to `.gitignore`, syncs the harnesses this repo already
uses (or every supported one with `--all`), and appends a short pointer to an existing `AGENTS.md` —
never creating one, and never touching `CLAUDE.md` at all, exactly as specified.

- `claustrum.json` is built by hand with `Utf8JsonWriter` rather than serialized through
  `ClaustrumJsonContext.Default.ConfigDocument`: that shared context also emits `RunResult`'s
  machine-readable `--json` one-liner, whose explicit `null` fields (e.g. `"error":null`) are part of
  the documented output shape, so serializing the *whole* `ConfigDocument` through it would litter
  this hand-editable config file with `"roles": null, "backends": null, ...` for every field `init`
  doesn't set.
- **"claude" is always synced**, `--all` or not, even in a repo with no `.claude/` directory: it's the
  only harness whose `Sync` also merges the `claustrum` MCP server into `.mcp.json`/`.vscode/mcp.json`
  (`McpConfigSync` is `internal` to `Claustrum.Roles`, wired only through `ClaudeSync.Sync` — see
  `OpencodeSync`'s own doc comment on why that merge wasn't generalized in this pass), and its agent
  files are harmless to have even in a repo that hasn't adopted Claude Code. opencode/copilot are
  detected from `opencode.json`/`.opencode/` and `.github/` respectively; cursor is detected
  (`.cursor/`) but only reported, never synced (no `CursorSync` exists).
- Idempotent by construction, same as `sync` itself: reruns skip an existing `claustrum.json`, skip a
  `.gitignore` that already has the entries, skip an `AGENTS.md` that already has the pointer
  (checked by the `## Claustrum delegation` heading), and each harness's own `Sync` already handles
  its own marker-based idempotency.
- Verified live end to end (not just unit-tested): ran against a real temp repo with a hand-written
  `AGENTS.md` and a `.github/` directory — correctly detected and synced claude+copilot, wrote a clean
  two-key `claustrum.json`, appended the AGENTS.md pointer once, and a second `init` run reported
  everything already up to date with zero new writes.

## doctor --probe (2026-09-18, issue #4/M3): auth/mcp/os checks, the real probe call deferred

docs/PLAN.md §B6 lists five checks: `binary` · `auth` · `probe` · `mcp` · `os`. Bare `doctor` already
covered `binary` (M1) and the merged-config dump; `--probe` now adds three of the remaining four:

- **`auth`**: env-var presence only ("value never printed" — only presence is reported), using this
  repo's own already-confirmed variable names (`EnvAllowList.cs`'s prefixes, and copilot's documented
  `COPILOT_GITHUB_TOKEN`/`GH_TOKEN`/`GITHUB_TOKEN` precedence from NOTES.md "The copilot backend").
  Deliberately does **not** check any backend's login-file path: none of the four backends' actual
  credential-storage location was independently confirmed during this work (opencode's and copilot's
  own CLI *behavior* was verified live, not where they cache a token) and a wrong guess would report
  "not set" for someone who is, in fact, logged in — worse than not checking at all.
- **`mcp`**: reads `.mcp.json`/`.vscode/mcp.json` (JSONC-tolerant, same `CommentHandling.Skip` +
  `AllowTrailingCommas` McpConfigSync's own `ParseExisting` uses) and reports whether each registers
  a `claustrum` entry — read-only, `sync` remains the only thing that writes these files.
- **`os`**: generalizes the plan's own example ("warns when a backend resolved from WSL is a
  `/mnt/c/...` Windows exe") to any binary-path/cwd mismatch across the `/mnt/` boundary.
- **`probe` itself — the actual "1-token reply OK, cost shown" round trip — is not implemented.**
  *(Implemented 2026-09-21, issue #12 — see "doctor --probe really calls a backend now" below; the
  paragraph that follows is the receipt for why it was deferred.)*
  It needs a real, authenticated call against whichever backend is being checked, which (a) this
  environment cannot exercise for any of the five backends (no working credential for any provider
  was available anywhere in this session) and (b) genuinely spends the user's own money/quota, which
  is not something to wire up speculatively and leave untested. Left as a known, named gap rather than
  a fabricated "always succeeds" or "always fails" placeholder.

## M3 review fixes (2026-09-19, issue #4/M3)

Fifteen findings from the review of PR #8, plus the red Windows CI leg. The four that changed a
documented decision rather than just the code:

**`dotnet format` needs `end_of_line` spelled out, or Windows disagrees with `.gitattributes`.**
`.gitattributes` says `* text=auto eol=lf`, so every checkout is LF — but `.editorconfig` said
nothing about line endings, and `dotnet format` then takes the *platform* default. On
windows-latest that is CRLF, so the formatter rewrites the endings of any line it normalises and
`--verify-no-changes` fails. It only ever surfaced on four lines (`RunResult.cs`'s new record
parameters, where comment trivia inside a parameter list makes the formatter rewrite that region);
every other line was left alone, which is why ubuntu stayed green and this looked like a
content problem rather than a settings one. `[*] end_of_line = lf` (plus `crlf` for `*.ps1`/`*.cmd`,
mirroring `.gitattributes`) is the fix. Measured on SDK 10.0.401, run 35365332033.

**A `shell` permission level now exists, between `readonly` and `edit`.** `ui-reviewer` shipped as
`edit+shell` while its own ROLE.md says "You never modify code" — not carelessness: `readonly` maps
to Claude's `--allowedTools Read,Glob,Grep,Bash(git …)`, an allow-list that also shuts out the
Browser MCP tool the role requires and any dev-server command, so the role was unusable at that
level and `edit+shell` was the only rung left. The missing rung is "read the tree, run commands,
change nothing": `plan` mode plus `--disallowedTools Edit,Write,NotebookEdit`, which leaves MCP
tools reachable because it names only built-ins. Mapped for all four backends; docs/PLAN.md §A3's
permission table and §A5's `--permission` grammar updated with it.

**cursor's ReadOnly no longer uses `--mode ask`.** docs/PLAN.md §A3's cursor column says
`--mode ask` (no `-f`), but `-p` is headless and ProcessRunner closes the child's stdin, so an
approval prompt is a guaranteed stall until `--timeout` kills the run — and readonly is the level
the most likely cursor role (code-reviewer) uses. Same deviation, for the same reason, as the one
`CopilotBackend` already documents for its own row. Nothing is given up: cursor has no native deny
mechanism at *any* level, which is why the plan already routes its deny list through the prompt and
has `doctor` mark it advisory — so the read-only rule goes there too. Still unverified against a
real `cursor-agent`; it remains the one backend awaiting a teammate's validation.

**A worktree left by a run that never finished used to be uncleanable.** `DelegateEngine` created
the worktree and branch before `Runner`'s own gates ran (`ValidateTimeout`, the blind gate), and
nothing removed them when the run never reached a `result.json` — which is precisely the signal
`jobs clean` waits for, so the orphan was permanent and its `claustrum/<job>` branch accumulated.
Two halves to the fix: the isolated path now undoes its own worktree *and branch* on any failure
(`JobWorktree.TryRemoveAbandonedAsync`), and `jobs clean` additionally treats a worktree whose job
directory is gone entirely as cleanable — the hard-kill case the first half cannot cover. `clean`
also no longer aborts the whole sweep on the first directory git refuses to remove.

Smaller, each with a regression test: opencode's own `type:"error"` event now marks the run failed
regardless of exit code; the `api` backend's curl config (which holds the API key in clear text) is
created owner-only and curl is spawned with `-q` so `~/.curlrc` cannot redirect the response;
`claustrum init` ignores `.claustrum/locks/` as well as `worktrees/`, and adds only the lines an
older `.gitignore` is missing; job ids carry 32 bits of entropy and claim their directory with an
atomic `CreateNew`, since M3 is the first thing to create them concurrently; `RoleConcurrencyGate`
keys its slot pool per cast (§D4 says per cast, not per role) and gives up with a named
`TimeoutException` instead of polling forever; `cursor` refuses an over-long prompt by name rather
than failing inside `execve`; the cast questionnaire finally asks for `max_parallel`, which had no
way in short of hand-editing the cast JSON; `## Access` is in the shared `/claustrum` skill, so the
brief ui-reviewer is told to read can actually be written; and ClaudeSync/OpencodeSync/CopilotSync's
three verbatim copies of the marker machinery are now one `SyncWriter` + `SyncAccumulator` — the
extraction OpencodeSync's own comment deferred until "CopilotSync exists too".

## Development moved off WSL to native Windows + native Linux (2026-09-19)

Until now the Linux half of "must work on Windows and Linux" was WSL, over a `/mnt/d` view of the
same Windows clone — hence `AGENTS.md`'s separate-output-tree flag (one `obj/` reached from two path
styles) and a scattering of acceptance criteria phrased as "green in WSL". Both OSes now have their
own native clone, which is also what CI has always actually run (`windows-latest` + `ubuntu-latest`),
so the criteria and the build notes were saying something narrower than the gate they stand for.

Realigned: `docs/PLAN.md`'s owner decisions, machine facts, M0 and M3 acceptance rows, `AGENTS.md`'s
build section, and issues #4/#10. "Green in WSL for every installed backend" became "`smoke.sh` green
on Linux and `smoke.ps1` green on Windows" — the same bar, stated as the two platforms rather than
one person's route to one of them.

**What did NOT change, and must not be mistaken for stale:** WSL remains a first-class way to *run*
Claustrum, and everything that exists for those users stays exactly as it is — the §A4 path policy
(never translate `D:\` ↔ `/mnt/d`; a Windows binary spawns Windows harnesses, a Linux binary spawns
Linux ones), `doctor --probe`'s `os` check warning when a backend resolved under WSL is a `/mnt/c/…`
Windows exe, and the path-separator-agnostic assertions in ConfigTests/McpConfigSyncTests. The
measured WSL datapoints in this file (the 2.6s git-stdin timing, the sync-manifest portability
finding) are dated observations and stay as written. The change is about how *we* build, not about
what Claustrum supports.

## Tree budget accounting is a file ledger (2026-09-21, issue #9/M3)

`docs/PLAN.md` §D4's second sentence — "`budget_usd` is enforced across the job tree: a child that
would exceed the remaining budget is refused with status `BudgetExceeded`" — was the last unbuilt
half of M3. Until now `ResolvedRun.BudgetUsd` reached the backend as `--max-budget-usd` and nothing
else: `RunStatus.BudgetExceeded` and its exit-5 row in `RunCommand.ExitCodeFor` were the only
references to the concept in the repo, so N children of one cast could each spend the *whole* cast
budget.

**A tree exists only when `CLAUSTRUM_PARENT_JOB` says so.** §D2/§D3 already reserve that variable to
"link child jobs to the coordinate job", so it is also the tree's identity here: trimmed and
non-empty, its value *is* the tree id (`BudgetLedger.TreeIdFor`). No variable ⇒ no tree ⇒ behaviour
is exactly what it was before this slice — the per-run cap `--budget ?? cast budget_usd ??
defaults.budget_usd`, and no cap at all for a cast that says `budget_usd: null`. Measured
2026-09-21 on a fresh home: no tree ⇒ `success`, the explicit `--budget 0.10` reaches `request.json`
unchanged, no warnings, and **no `budget/` directory is created at all**. The value is never checked
against the job store: a tree is an accounting bucket, not a job that has to exist, which is what
lets a *host* architect (§D3's other mode, where no `coordinate` job exists at all) export one by
hand and get the same accounting.

**The total has to survive across processes**, for the reason `RoleConcurrencyGate` already exists:
a spawned architect fans children out as separate `claustrum run` CLI processes, so an in-memory sum
in `JobManager` would cap nothing. Hence a ledger on disk, deliberately in the same shape as the
gate's slot files — `JobDirectory.ResolveHome` was extracted from `ResolveRoot` so that `jobs/` and
`budget/` hang off one root and `CLAUSTRUM_HOME` relocates both together (a test that isolates one
and not the other would be worse than no isolation).

```
<claustrum home>/budget/<sanitized tree id>/
  .lock                 # exclusive-open mutex, never deleted
  <jobId>.json          # one BudgetLedgerEntry per job
  <jobId>.live          # held FileShare.None while that job runs; deleted when it completes
```
```json
{"job_id":"20260921-160924-6e3a9f88","role":"builder","cap":0.10,"cost":0.10,
 "started_at":"2026-09-21T16:09:24.4730809+00:00","finished_at":"2026-09-21T16:09:24.4733942+00:00",
 "abandoned":false}
```

`BudgetLedgerEntry` goes through `ClaustrumJsonContext` like every other DTO (AGENTS.md), so the
files are snake_case and the AOT publish stays at zero trim warnings. `cap` is the slice admission
granted — and what the job *reserves* while it is still live; `cost` is what it was finally charged
and stays `null` until it finishes. `abandoned` is additive and defaults to `false`, so an entry
written before this revision still deserializes (measured: a hand-seeded entry with no `abandoned`
key reads back as `done`).

### The admission rule

`BudgetLedger.AdmitAsync`, entirely under the lock:

1. **Survey.** For every entry: a finished one contributes its `cost`; an unfinished one is probed by
   trying to open its `<jobId>.live` exclusively. The open *failing* means a live holder ⇒ its `cap`
   is added to `reserved`. The open *succeeding*, or no such file, means the holder is gone ⇒ the
   entry is rewritten `cost: null, abandoned: true, finished_at: now` and counts $0 from then on.
2. `remaining = treeBudget - spent - reserved`.
3. `remaining <= 0` ⇒ refused, "nothing left for role '<role>'".
4. an explicit `requestedCap > remaining` ⇒ refused, `--budget 0.50 exceeds it`.
5. otherwise `effectiveCap = floor_to_cents(min(requestedCap ?? remaining / share, remaining))`; a cap
   that floors to `$0` is refused with its own reason (see "Step 3 refuses with a reason…" below:
   the share slice names the division and the `--budget` that would get past it; an explicit cap
   that rounds away says so), and otherwise the `.live` handle is taken,
   the entry is written with `cost: null`, and the job is admitted.

`share` is `JobTreeBudget.Share` = `max(1, the role's max_parallel ?? 1)`, set by `DelegateEngine`: a
fan-out role takes a slice of what is left rather than all of it, a sequential role takes the whole
remainder. An explicit `--budget` is the caller's own ceiling and is never divided, only clamped.

The refusal message names the tree, the two sums, the budget and the remainder, because it is the
only thing the caller sees: `tree 'tree-abc': $4.90 spent + $0.00 reserved of $5.00, $0.10 remaining;
--budget 0.50 exceeds it`. A negative remainder prints as `-$0.50`, not `$-0.50` — it is a normal
state, not an anomaly (step 3's own message shows it), since a backend can overshoot the
`--max-budget-usd` it was given.

**Step 3 refuses with a reason a caller can act on, and there are three of them.** A sequential
role divides by 1, so its first child reserves the entire remainder and its second is refused while
the first still runs — the arithmetic is right, and the first cut of the message was useless (it
suggested `--budget`, which the `remaining <= 0` check never reads). Measured 2026-09-21: exhausted
with a live sibling ⇒ `…$0.00 remaining; nothing left for role 'builder' while 1 running job(s) hold
$2.00 — wait for one to finish`; exhausted alone ⇒ `…; nothing left for role 'builder'`; a share
slice that floors to zero (`$0.05` tree, share 10) ⇒ `…; the slice for role 'builder' ($0.05 / 10)
rounds to $0.00 — pass --budget (at most $0.05) to claim an explicit slice`; an explicit
`--budget 0.004` ⇒ `…; --budget 0.004 rounds to $0.00`; `--budget 0.50` over `$0.10` ⇒ `…; --budget
0.50 exceeds it`. Only the third case is one `--budget` can get past, and it is the only one that
says so.

**Step 5's clamp is what makes the cap hard per child.** `Runner` rewrites the request with the
granted cap (`request = request with { BudgetUsd = granted }`) *before* `request.json` is written, so
both the persisted request and the backend's own `--max-budget-usd` carry the slice, not what the
caller asked for. Measured through the real binary, tree seeded with $4.90 of a $5.00 budget:
`--budget 0.5` ⇒ exit 5 and `"status":"budget_exceeded"`; no `--budget` ⇒ admitted with
`"budget_usd":0.10` in `request.json`. Correspondingly, `DelegateEngine` passes only an *explicit*
`--budget` as the per-run cap once a tree is in play — the cast's number is the tree's, and handing
it to the child as well would ask the ledger "may this child spend the whole tree budget?" on every
single call.

### Reserving is what the first cut got wrong

The first cut summed only *finished* costs, so `remaining` ignored everything still in flight. Review
measurement, three concurrent children of a $2.00 cast: `A cap=2, B cap=2, C cap=2` — the tree cap
was hard per child and did nothing at all across a wave of siblings, which is the one case §D4 exists
for. The fix is the `.live` file, and it is `RoleConcurrencyGate`'s exclusive-open trick read
backwards: the gate opens a file *to claim* a slot, the ledger tries to open one *to ask whether
somebody else still holds it*. Measured 2026-09-21 with an unrelated `flock -x` holding
`sibling.live` for an entry with `cap 2.50` on a $5.00 tree: `jobs budget` reports it `running` with
`reserved $2.50`, the next admission is granted exactly $1.25 (`share 2`), and `--reset` refuses with
exit 2 naming the job. Release the handle without completing and the next admission rewrites that
entry `abandoned: true` and hands out the full $5.00.

**The probe must be instantaneous, and it is**: an exclusive open against a live holder fails at once
rather than waiting, which is what makes it safe to run inside the ledger lock. `FileNotFoundException`
is caught *before* `IOException` (it derives from it) because "no file" and "file I could open" are
the same answer — the holder is gone.

**`.live` is opened before the entry is written**, inside the same critical section, so no rival can
read an entry whose holder has not yet claimed its handle and mistake it for a dead one.
`CompleteAsync` runs the other way round: the final entry first, then the handle is closed and the
file best-effort deleted — safe, because a finished entry is never probed again.

### Cost-less backends are charged their cap

cursor and copilot report no cost at all, so their entries closed at $0 and the cap was a no-op: a
tree could run forever on backends that never bill it (review finding). `Reservation.CompleteAsync`
therefore charges `cost ?? (ran ? cap : 0)`, where `ran` is `Runner`'s "the backend process actually
came back with a `ProcessOutcome`". Measured with a stub backend that exits 0 printing
`{"result":"OK"}`: the entry closes at the granted `$0.10` and the result carries one warning,
`cost not reported by backend 'claude'; charged the granted cap $0.10 to tree 'tree-costless'` — a
caller reading `cost_usd: null` has to be told the tree was charged anyway.

⚠ The same rule charges the full cap for a `Timeout` or `Cancelled` run on a cost-less backend. That
is deliberate: the process did burn whatever it burned, and the only two alternatives were to charge
$0 (the bug above, back again through a side door) or to guess.

The paths where **nothing** ran still charge $0 — `BackendMissing`, a bad `cwd`, a `backend.Build`
throw. Measured: `--backend nonexistent` inside a tree writes `cost: 0` and leaves the remainder
untouched, so a wave of typos cannot spend a tree.

### One funnel closes the entry

Every return after admission goes through `Runner.FinishAsync`, which writes `result.json` and — only
for a job the ledger admitted — completes its reservation. Routing all of them through one place is
the point: a new `return` added later cannot forget to record, and it is also where `ran` is decided
per path. `MissingBackendResult` delegates to a shared `NoProcessResult` builder (status + message are
the only difference between it and the refusal result), so the refusal's "nothing ran" shape is not a
third copy of the same 20 fields.

**Where the accounting lives, and why not `JobManager`.** The issue suggested `JobManager` "already
owns the async job tree", but it owns only the jobs *this MCP server* started; the CLI door never
touches it. Admission therefore sits in `Runner.RunCoreAsync`, the one place both doors reach, right
after the job directory exists and before anything is written or spawned — a refusal costs one job
directory with a `result.json` in it and nothing else (no `system.md`, no `request.json`, no process,
no temp files). `RunOptions.Tree` (`JobTreeBudget`) is how the tree reaches Core: call-site data,
like `BackendConfig` and `EnvPassthroughAll`, so Core still never reads a cast or an env var of its
own (NOTES.md "Backend config and env passthrough are call-site data, not RunRequest fields").

**…except for the one caller that has to spend before Runner is entered**, `DelegateEngine`'s
`max_parallel > 1` path: it cuts a branch and holds a concurrency slot first, so it calls
`AdmitAsync` itself and passes the answer in as `RunOptions.Admission`. Runner's rule is then one
line — `options.Admission ?? admit here` — so there is still exactly one admission per job and the
direct path is untouched. The slot is taken *before* the admission on purpose: a reservation must
never sit behind a gate that can hold it for the role's whole timeout, and a sibling that finishes
during that wait releases budget this child can then be granted.

**Ownership of the reservation is split at one point: `Runner`'s `await using` of it** (a brief that
trips the blind gate has entered `Runner` but not reached that line, and the engine's `catch` is what
releases the handle then). Up to there it
belongs to whoever admitted — `DelegateEngine` releases it in its own `catch` if `git worktree add`
throws, which merely closes the handle and leaves the entry for the next admission to write off as
abandoned ($0). From there on `Runner.FinishAsync` completes *and* disposes it on every path, so the
engine never completes one and the two can never double-charge. Disposing twice is harmless
(`FileStream.Dispose` is idempotent), which is what makes the split safe to state so simply.

**A ledger error at admission time is a `Failed` result, not a throw.** It is the symmetric case to
the one `CompleteAsync` already had: `TimeoutException`/`IOException`/`UnauthorizedAccessException`
around `AdmitAsync` leaves through the same funnel with `RunStatus.Failed`, the message in `Error`,
and nothing spawned. Measured by making the tree's ledger path a *file*: `failed`, exit 1, one
`result.json` in the job directory and no `request.json` beside it. The isolated path keeps that
property by *not* deciding: the same three exceptions leave it with no admission at all, and Runner —
called with the slot still held and the worktree already created — admits, fails the same way and
writes the same `Failed` result. Falling through to the direct path there would have been the cheaper
code and the wrong one: a retry that *succeeded* would have run the job in the caller's cwd, outside
both the worktree and the cap.

### The gaps this shape still accepts, deliberately

- **Until M4's `coordinate` exists, nothing in the repo hands a tree id across a process hop.**
  `CLAUSTRUM_PARENT_JOB` is read by the ledger and written by nobody: a claustrum process is a member
  only when its *own* environment carries the variable (a shell export, or `--env`/`Env` on the
  request that spawns its backend), and a member never forwards it (see the allow-list bullet
  below). So the spawned-architect topology — `claustrum run architect` whose backend fans out
  `claustrum run builder` — is **not enforced today**: the architect's run holds the tree's whole
  remainder as its reservation while its children see no tree at all and spend under the §D1 per-run
  cap. Before 2026-09-21's second remediation the same topology failed loudly (`$0.00 remaining`);
  it now fails silently in the direction that costs money, which is the honest state of a feature
  whose producer side is M4's. The host-architect topology (the chat agent exports the variable and
  calls `claustrum run` itself) is fully enforced. ⚠ `coordinate` must pass `CLAUSTRUM_HOME` along
  with the tree id whenever it is set, or `claustrum jobs budget` reads a different ledger than the
  children write to — and `--reset` would clear the wrong one.
- **The slice decays across an overlapping wave.** `remaining / share` divides what is left *after*
  live siblings are subtracted, so siblings admitted back to back on a $5.00 tree with `share 2` get
  $2.50 and then $1.25, not $2.50 twice (both numbers measured). It is monotone and can never
  over-commit, which is the property that matters; "equal slices" only holds for the first admission
  of a wave. Dividing by `share` before subtracting reservations would over-commit instead, and
  nothing at admission time knows how many siblings are actually coming.
- **A holder that closes its handle and completes anyway wins.** If a sibling has already rewritten
  the entry as `abandoned` (worth $0) and the real holder then calls `CompleteAsync`, the entry is
  reopened with its true cost and `abandoned: false` — a real completion outranks a guess, at the
  price of the sibling having decided on a stale $0. In `Runner` this cannot happen (Complete always
  precedes Dispose); it is reachable only by a caller that drops the reservation by hand.
- **An abandoned job's `.live` file is left on disk.** It is empty, its entry is finished so nobody
  probes it again, and deleting a file while holding an open handle to it is not portable. `jobs
  budget --reset` is the broom.
- **A `CompleteAsync` that genuinely cannot write** (lock timeout, read-only mount) does not throw
  over an already-finished run — NOTES.md "Runner always yields a result after the process ran" — but
  it does not vanish either: the failure rides out as a warning on the `RunResult`, because a silently
  unrecorded cost is exactly the error that overstates what is left.

### Decisions worth knowing

- **Membership in a tree is handed out by a coordinator, never forwarded by a member** — which is why
  `CLAUSTRUM_` is *not* on `EnvAllowList.prefixes` (it was, for one revision, and the list is back to
  its previous seven entries byte-for-byte). A member holds its reservation for its whole lifetime, so
  a `claustrum run` that inherited its `CLAUSTRUM_PARENT_JOB` would ask the ledger for a slice its own
  parent has already reserved: measured, a share-1 member's nested child saw `$0.00 remaining` and was
  refused. §D3's `coordinate` will instead put `CLAUSTRUM_PARENT_JOB` (and `CLAUSTRUM_HOME` when set)
  on its architect's *request env*, where caller-supplied values beat the allow-list, and that
  architect's own children inherit both from the backend's shell; the coordinator is not a member.
  ⚠ The allow-list is therefore load-bearing for `ProcessRunnerTests`' "filtered variable" marker
  again: it is named `CLAUSTRUM_TEST_SECRET_<guid>`, and it only proves filtering while no
  `CLAUSTRUM_` prefix is on the list.
- **The whole ledger API is async** (`AdmitAsync`/`CompleteAsync`/`PeekRemainingAsync`/`ReadAsync`):
  `Runner` is async and the first cut blocked a pool thread on `Thread.Sleep` while polling the lock.
  The poll takes no `CancellationToken` on purpose — the wait is already bounded at 5s, and the one
  caller that must write whatever happens is the finish path, whose token has usually already fired.
  A concurrency test still has to put two admits on two threads: the lock serializes them, so what it
  observes is the *second* admission seeing the first one's reservation. ⚠ `PeekRemainingAsync` now has
  **no caller in the repo** and is kept deliberately, as the read-only remainder a *reporting* caller
  wants; its doc comment carries the trap it cost us, dated, so the next caller reads it there: a peek
  is never binding, and anything that has to decide calls `AdmitAsync`.
- **The lock is the gate's trick, and the same reasoning applies to not deleting it**: unlinking it
  while a rival races to reopen the same path lets that lock land on an orphaned inode while a third
  process opens a fresh file at the same path and believes it holds the same lock. The wait is
  bounded at 5s with a `TimeoutException` naming the directory, so one stuck holder cannot turn every
  other child of the tree into a run with no output and no end.
- **The tree id is sanitized into a directory name** the way the gate sanitizes its slot keys, and
  the sanitizer is a second copy rather than a shared helper: this slice deliberately left
  `RoleConcurrencyGate` untouched. Two ids differing only in unsafe characters therefore share one
  ledger, which is the same trade the gate already makes for cast/role names; a bare `.` or `..`
  additionally loses its dots, because it would otherwise name the `budget/` root or the home above
  it instead of a tree. Since `/` and `\` collapse to `_`, `jobs budget --reset` can never be talked
  into deleting anything outside `budget/`.
- **`claustrum jobs budget <tree> [--reset]` exists because exporting a tree id makes the ledger
  permanent.** It prints the directory, one line per entry (job id, role, cap, cost,
  `running|done|abandoned` from the same probe) and then `spent`/`reserved`; `--reset` deletes the
  tree's directory and refuses with exit 2, naming the job, while any entry is still live. The read
  path is genuinely read-only: a dead holder is *reported* abandoned without rewriting the file, which
  stays the next admission's job. The delete happens after the lock is released, because the directory
  being removed contains the lock file and Windows will not unlink a directory with an open handle in
  it; a child admitted in that gap is the race any explicit reset has.
- **A refused child in the `max_parallel > 1` path is refused before it is isolated** — and the
  decision that does it is binding, because the first attempt was not. That one peeked with
  `PeekRemainingAsync` and fell through to the direct path on `<= 0`, leaving the real admission to
  `Runner`; a sibling's reservation is released the moment it finishes, so in the window between the
  peek and `AdmitAsync` (job directory, prune, up to 5s of lock polling) the remainder climbed back and
  the job was admitted — and then ran **directly in `request.Cwd`, concurrently with isolated siblings
  and outside the per-cast cap** (reproduced with a FIFO `--file` to hold the window open). The peek
  also only covered `remaining <= 0`, so a `--budget` larger than the remainder still cut a
  `claustrum/<job>` branch before `Runner` refused it — and the refusal advertised that branch, since
  `isolatedResult with { Worktree, Branch }` was unconditional. Now: slot, admission, and only then
  `git worktree add`; a refusal releases the slot, writes its `result.json` against `request.Cwd`, and
  returns `worktree`/`branch` null. Measured 2026-09-21 on a cwd that is *not* a git repo (any attempt
  to isolate would have failed loudly), `max_parallel: 2` — spent tree ⇒ exit 5, `budget_exceeded`,
  `worktree`/`branch` null, **no `.claustrum/worktrees` at all**; `--budget 0.5` against a $0.10
  remainder ⇒ the same. ⚠ Unlike the peek, this path *does* create the slot file
  `.claustrum/locks/<cast>__<role>.0.lock` — it takes a slot in order to ask, then releases it — so
  "a refusal touches no lock file" is no longer true; "a refusal holds no slot and cuts no branch" is.
- **A refused job that did get isolated is cleaned the normal way.** Only one route still leads there
  — the ledger error above, where the engine isolates without an admission and `Runner` refuses under
  the slot — but the cleanup is the same as it always was: a refusal is a *returned* result with a
  `result.json` on disk, not a throw, so `JobWorktree.TryRemoveAbandonedAsync` (which exists for
  throws) does not fire and `jobs clean` — which treats `result.json` as "finished" — removes the
  worktree and keeps the branch, exactly as it does for a `backend_missing` job.
- **Nothing was needed for MCP.** `RunStatus` already serializes snake_case, so `delegate` and
  `job_result` return `"status":"budget_exceeded"` from the same `DelegateEngine`; only the CLI has
  an exit code to map, and `RunCommand.ExitCodeFor` already mapped it to 5.
- **The granted cap is floored to whole cents.** `remaining / share` is an exact decimal — a $2.00
  tree split three ways gave `0.6666666666666666666666666667`, and that string reached
  `--max-budget-usd`, `request.json` and the ledger's `cap` verbatim while `jobs budget` printed
  `$0.66` beside it. `Math.Floor(x * 100) / 100` makes the stored number the printed one; *down*,
  because rounding up is the only direction that can over-grant. The two prices of that: a cap that
  floors to `$0` is refused with step 3's message (a sub-cent slice buys nothing, and a `$0`
  `--max-budget-usd` would be a lie either way), and an explicit `--budget 0.125` is granted `0.12`
  — measured through the real binary, `request.json` `"budget_usd":0.12` and the entry's `cap` `0.12`.

## The claustrum MCP server is registered in opencode.json too (2026-09-21, issue #15/M3)

`OpencodeSync` wrote only `.opencode/agent/*.md` + `.opencode/command/claustrum.md`; the MCP server
that makes `delegate` available inside an opencode session had to be registered by hand. It now
merges a `claustrum` entry into the config's own top-level `mcp` key, through the *same*
`McpConfigSync` that already handles `.mcp.json`/`.vscode/mcp.json` — so the idempotency and
foreign-key rules are one implementation, not two that drift.

**Documented target shape** (opencode.ai/docs/mcp-servers, checked 2026-09-21):

```json
{ "$schema": "https://opencode.ai/config.json",
  "mcp": { "claustrum": { "type": "local", "command": ["claustrum", "mcp"] } } }
```

`command` is an **array** here, unlike the `command` + `args` split `.mcp.json` and
`.vscode/mcp.json` use — the one real shape difference between the two sides. The optional
`enabled`/`environment`/`timeout`/`cwd` fields are deliberately omitted so opencode's own defaults
apply.

**Three decisions the shape forced:**

- **`$schema` only on creation.** A file created from nothing gets `$schema` as its first property
  (it is what opencode's docs show, and opencode's editor tooling keys off it); a config a human
  already has is never given one, and an existing one is never rewritten. Implemented as
  `McpConfigTarget.RootOnCreate`, which seeds the root object only on the `!File.Exists` path.
- **`opencode.jsonc` is targeted only when `opencode.json` is absent.** opencode reads either name.
  Writing back through `JsonNode` drops comments (the trade-off `McpConfigSync` already documents for
  `.vscode/mcp.json`), so preferring `.json` means a repo with neither gets the documented plain-JSON
  name, and a repo that deliberately chose JSONC is not given a second, shadowing config file.
- **`--global` never touches it.** `opencode.json` is a repo-root file, not one of docs/PLAN.md §B4's
  `--global` targets (agent directories, the desktop app's own config) — the same rule ClaudeSync
  applies to `.mcp.json`, and the same reason the manifest is skipped there too.

### Two refactors this needed, and why they are not incidental

**`McpConfigSync` now merges one harness-declared target, not two hardcoded claude ones.** It takes a
`McpConfigTarget(Harness, Path, SectionKey, Entry, RootOnCreate)` and the `SyncAccumulator` the
harnesses already thread around, instead of the five parallel out-lists it had — which is exactly the
smell `SyncAccumulator`'s own doc comment says it was created to end (it had been copied into
`OpencodeSync` and `CopilotSync` before that extraction; `McpConfigSync` was the last holdout). The
claude entries moved to `ClaudeSync.MergeMcpConfigs`, next to the harness that owns them: where the
file lives and what the entry looks like is precisely what differs per harness, everything else is
shared. The manifest record's harness field is now the target's, so an `opencode.json` entry is
attributed to `opencode` rather than to `claude`.

**`ReadManifest`/`UpdateManifest` moved out of `ClaudeSync` into `SyncManifestStore`.** Two harnesses
now both write `.claustrum/sync-manifest.json` in a single `sync --only claude,opencode` run.
`Update` already merged into what was on disk keyed by path rather than replacing the file, which is
what makes that safe — the second harness's write preserves the first's entries. Verified live: one
run of `sync --only claude,opencode --roles builder` produces a manifest holding all eleven paths
(four `.claude/…`, `.mcp.json`, `.vscode/mcp.json`, four `.opencode/…`, `opencode.json`), and the
rerun reports every one of them skipped.

A side effect worth knowing: `sync --only opencode` now creates `.claustrum/sync-manifest.json` where
before it created nothing, and records opencode's *agent and command* files in it as well — those
were previously written with no manifest trace at all, despite `SyncManifest`'s doc comment claiming
it holds "every file the last `sync` run wrote, across roles/harnesses". `CopilotSync` still writes
no manifest; it has no JSON target that needs one, so it was left alone.

## doctor --probe really calls a backend now (2026-09-21, issue #12/M3)

docs/PLAN.md §B6's fourth check — "`probe` (1-token 'reply OK' call, cost shown)" — was the one
bullet PR #8 left out ("doctor --probe (2026-09-18, issue #4/M3)" above says why: no working
provider credential was reachable then). It is now implemented in `src/Claustrum/Cli/DoctorProbe.cs`,
wired into `backends doctor --probe` only. The MCP `doctor` tool (`ClaustrumTools.DoctorAsync`) is
deliberately untouched: no MCP tool may spend the user's money without a `--probe` typed by hand.

**The probe is a real role run, not a bespoke HTTP call.** It goes through the same
`Config.Resolve` → `Runner.RunAsync` pipeline every other run uses, so whatever `claustrum.json`,
`backends.<name>.path` and `defaults.env_passthrough` say about a backend is exactly what the probe
exercises. A hand-rolled call would test a code path no real run takes — the failure mode worth
catching here is "this machine's configured backend cannot reach its provider", not "the network is
up". Consequences of reusing the pipeline, all deliberate:

- **`ReportSchema: ""`** makes `ResolvedRole.HasReport` false, so `Runner.AppendReportTrailer` adds
  nothing and the brief stays the single sentence. A probe that demanded a ```claustrum-report fence
  would cost several hundred output tokens instead of one.
- **A `Directory.CreateTempSubdirectory("claustrum-probe-")` cwd**, deleted in `finally`. `Runner`
  snapshots its cwd before and after every run; pointing the probe at the user's repo would hash a
  whole worktree twice to learn nothing. The temp dir is not a git repo, so `WorktreeSnapshot` takes
  its file-scan branch over an empty directory.
- **`BudgetUsd: 0.50` (was `0.05` for a few hours — see "The cap is $0.50, not $0.05" below), `Timeout: 120s`,
  `Effort: "high"`, `Permission: "readonly"`.** The cap is per
  backend and is passed to the backend's own budget flag where it has one (`claude
  --max-budget-usd`). It is a guard against a backend that ignores the brief and starts working, not
  an estimate: a haiku-class "reply OK" is orders of magnitude under it.
- **The job is a normal job** under `~/.claustrum/jobs/<id>/` with `system.md`, `request.json`,
  `result.json` and the stdout log — which is what makes the failure lines able to point at a log
  path worth opening.

**Cheapest alias first, and only a configured alias.** `SelectModelAlias` walks
`["fast", "cheap-coding", "standard-coding", "frontier-coding", "frontier-reasoning"]` over
`config.Merged.Models` and takes the first whose `ResolveModelBackend` equals the backend being
checked. Two traps are baked into that loop:

- **The `models.ContainsKey(alias)` guard is load-bearing.** `Config.ResolveModel` falls through to
  `SplitBackendModel` for anything it does not find in `Models`, and a bare word with no colon
  splits to `("claude", word)`. Without the guard, `"fast"` would "resolve" to backend `claude` even
  in a config that defines no aliases at all — every backend would then probe claude's model name.
- **A `ConfigException` from a cyclic or too-deep alias is swallowed per alias.** The merged-config
  dump printed by the same command already shows the broken key; a diagnostic command that dies on
  the thing it is diagnosing is worse than one that reports "no alias".

With no `claustrum.json` at all, the built-in defaults map every class to `claude`, so on a stock
machine `claude` is the only backend that probes and every other installed backend prints
`skipped (no model alias in claustrum.json resolves to this backend; add e.g. "fast": "<name>:<model>")`.
Measured 2026-09-21 on the owner's Fedora box: `claude` and `cursor` and `api`(curl) all report
`found: True`, and a bare `--probe` makes exactly **one** paid call — `cursor` takes the no-alias
skip, `api` the "neither OPENROUTER_API_KEY nor ANTHROPIC_API_KEY is set" skip.

**Four gates before any money is spent, in this order:** `CLAUSTRUM_SKIP_PROBE`, then
`doctor.Found`, then `doctor.Problems` non-empty (its first problem is printed as the skip reason),
then the alias. Ordering the flag first means the reason reported on the owner's box is his own
skip, while CI — where no backend is installed — still reports `binary not found`.

### CLAUSTRUM_SKIP_PROBE exists because `--probe` is now inside the test suite

`tests/Claustrum.Tests/Cli/CliEndToEndTests.cs` spawns the real built binary with
`backends doctor --probe` in four tests that only assert on the free `auth:`/`os:`/`mcp:` lines.
Before this change those were free; after it, on any machine with a backend logged in, each one
would make a real paid call inside `dotnet test` — and a network round trip inside that test's 60s
process timeout is a flakiness source on top of the cost. `CLAUSTRUM_SKIP_PROBE=1` (or `true`, case
insensitive; anything else, including empty, probes for real) turns every probe into
`skipped (CLAUSTRUM_SKIP_PROBE set)` and replaces the pre-loop banner, so the banner never promises
a paid request it will not make.

It is read straight from `IPlatform.GetEnvironmentVariable` in `DoctorProbe.IsSkipped`, **not**
added to `Config.ReadEnvLayer`. It is a CLI escape hatch, not a config key: it has no
`claustrum.json` counterpart, so it has no winning layer to show in the `merged config:` dump, and
putting it there would invent one. `CLAUSTRUM_HOME` is the precedent for a `CLAUSTRUM_*` variable
read outside the merged layer.

Rejected alternatives: blanking `PATH` in the test harness (a fifth backend or a resolver change
silently re-enables paid calls, and an empty `PATH` on the Windows runner is a gamble), and
`CLAUSTRUM_BUDGET_USD=0` (muddles budget semantics — "no money" would have to mean "no call").

### "no credential" vs "failed" is a text heuristic, and says so

The issue asks the probe to "degrade to a clear 'no credential' line rather than an error for
backends it cannot reach". No backend reports "you are not authenticated" in a machine-readable
field — `RunResult` carries only `Error`/`FinalMessage` text — so `LooksLikeMissingCredential`
matches `auth`, `login`, `unauthorized`, `401`, `403`, `api key`, `credential`, `token`
case-insensitively across both. This is loose on purpose and safe in both directions: a false
positive still prints the backend's own first error line and the log path, and a false negative
prints the same text under `failed (...)`. The word `token` is the widest of the seven (a
token-limit error would read as a credential problem) and is the first one to drop if that shows up
in practice. Nothing here is a substitute for the `auth:` line, which reports env-var presence only
and never a value.

**The cap is $0.50, not $0.05 — measured (2026-09-21, issue #12).** The first real
`claustrum backends doctor claude --probe` never got an answer: on Claude Code 2.1.278 the one-word
round trip cost **$0.1200704** and `claude` killed it at its own flag
(`subtype: "error_max_budget_usd"`, `terminal_reason: "budget_exhausted"`,
`errors: ["Reached maximum budget ($0.05)"]`). Two tokens of conversation (2 in, 4 out) but a cold
cache: 28 987 cache-creation tokens for the system prompt plus the tool definitions, which is what
gets billed and what no "1-token call" estimate accounts for. `ProbeBudgetUsd` is therefore `0.50m`,
and the banner now interpolates the constant instead of repeating the number, so the next move
cannot leave a stale string behind. What did **not** change is what the cap is *for*: a guard against
a backend that ignores the brief and starts working, not an estimate of the round trip — a probe that
actually answers still costs a few cents (the recorded success is $0.0384621), and the tool-definition
surface, not the reply, is what sets the floor. The same run exposed two defects one level down, both
fixed with it: `ClaudeBackend` read the final message only from `result`, which this document does not
carry at all, so the probe printed `failed (no error message)` over a perfectly explicit error — it
now falls back to the `errors` array joined with `; `, then to an `error_*` `subtype`; and the non-git
`ScanFiles` fallback threw `UnauthorizedAccessException` out of the *after* snapshot when the probe's
temp cwd held an unreadable directory, failing a run that had already succeeded — it now walks
directories itself and skips what it cannot read, the same trade the "unreadable file is unhashable"
note already accepted.

## The cursor backend, validated against a real install (2026-09-21, issues #13/#14)

Supersedes "The cursor backend (2026-09-18, issue #4/M3): fixture-only, as the plan already
expected". `cursor-agent 2026.09.18-9a7762b` is installed at `~/.local/bin/cursor-agent` and logged
in, so every line of docs/PLAN.md §A3's cursor row was checked against the real CLI. **10 paid agent
invocations**, all of them in throwaway `git init` repos under a scratch directory (`<tmp>` below),
always as `timeout 180 cursor-agent … < /dev/null`, never in a real checkout. Two further runs cost
nothing because the CLI refused before reaching the API.

Everything that was a guess is now measured, and three of the guesses were wrong: `usage` uses
camelCase keys, the prompt does not have to be on argv at all, and `readonly` has a native mode after
all.

- **The account is on Cursor's Free plan, so `auto` is the only model id that works** — every probe
  below therefore ran `--model auto`. `cursor-agent --model gemini-3.6-flash-low --trust -p
  --output-format json "reply with the single word PINEAPPLE"` exits **1** in 5.4s with **empty
  stdout** and one stderr line: `ActionRequiredError: Named models unavailable Free plans can only
  use Auto. Switch to Auto or upgrade plans to continue.` Identical output for `--model opus`, which
  is exactly what `claustrum run <role> --backend cursor` sends today (see the model-naming bullet).
  `cursor-agent models` lists ~230 ids (`gpt-5.4-nano-*` and `gemini-3.6-flash-minimal` are the
  cheapest named ones, `auto` is the default); nominal cheapness was moot here.

1. **Doctor — confirmed.** `cursor-agent --version` prints the bare string `2026.09.18-9a7762b`
   (exit 0), which `VersionProbe` passes through unchanged: `claustrum backends doctor cursor` →
   `found: True`, `path: /home/…/.local/bin/cursor-agent`, `version: 2026.09.18-9a7762b`, no
   problems. No code change was needed here.

2. **Headless spawn shape — the stall question answered, and it is not a stall.** In an untrusted
   directory with stdin closed, `cursor-agent -p --output-format json --workspace <tmp>/r8 "say hi"`
   returns in **~1s with exit 1**, empty stdout, and a stderr block: `⚠ Workspace Trust Required …
   To proceed, you can either: • Run 'agent' interactively to decide • Pass --trust, --yolo, or -f
   if you trust this directory`. So the trust gate is a **fast refusal, never a hang** — the M3
   review's worry ("`-p` cannot answer an approval prompt, so it stalls until `--timeout`") does not
   reproduce; cursor-agent notices stdin is not a TTY and gives up. What it *does* mean is that one
   of `--trust`/`-f`/`--yolo` is **mandatory** for every headless run, which is why `-f` is in every
   permission row below. Measured separately: `--trust` alone is enough for a read-only run (probe 6a
   returned fine without `-f`), and `-f` alone satisfies the trust gate *and* allows writes (probe 3),
   so `--trust` never has to be passed alongside `-f`.

3. **Success JSON — captured, one single-line document.** `cursor-agent -p --output-format json
   --model auto -f --workspace <tmp>/r2 "create hello.txt containing hi"` → exit 0 in 13.2s, empty
   stderr, `hello.txt` really created with `hi`, and stdout = exactly one 349-byte line, now
   `tests/fixtures/cursor/success.json`:
   `{"type":"result","subtype":"success","is_error":false,"duration_ms":8623,"duration_api_ms":8623,`
   `"result":"Created `hello.txt` with the contents `hi`.","session_id":"79f194af-…",`
   `"request_id":"b3f491c8-…","usage":{"inputTokens":9277,"outputTokens":110,"cacheReadTokens":26240,`
   `"cacheWriteTokens":0}}`. `result`, `session_id` and `is_error` were guessed right; **`usage` was
   not** — its keys are camelCase (`inputTokens`/`outputTokens`/`cacheReadTokens`/`cacheWriteTokens`),
   so the old `input_tokens` lookup silently reported no usage at all on every run. Never JSONL,
   never several documents, in any of the ten captures: `Parse` keeps parsing stdout as one document.
   **There is no cost field anywhere** (and `--help` lists no budget flag), so `cost_usd` is null by
   measurement and `--budget` neither caps nor accounts for a cursor run — the §D4 ledger records
   nothing for a cursor child. That gap is cursor's, not Claustrum's, and it wants a decision.

4. **Error capture — there is no JSON error document to capture on this plan.** Both failure modes
   reachable here (workspace trust, named-model refusal) print human text on **stderr**, leave stdout
   **empty**, and exit 1 — the same shape `CopilotBackend` documents for its own auth failure, and
   the branch `Parse` already had (`stdout.Trim().Length == 0` → stderr as the final message,
   `IsError` from the exit code) is therefore a *measured* path now, not a defensive guess. The
   fabricated `tests/fixtures/cursor/error.json` is deleted and replaced by the real
   `tests/fixtures/cursor/named-model-refused-stderr.txt`. An `is_error: true` JSON document is still
   unobserved: nothing on a Free plan gets far enough into a session to produce one.

5. **ReadOnly mapping — plan mode is real, works headless, and is now used.** Two paid probes, both
   `-p --output-format json --model auto --mode plan -f --workspace <tmp>/r4`:
   - asked for both an edit and a shell write ("(1) create plan-edit.txt containing x; (2) run the
     shell command: echo ran > plan-shell.txt") → exit 0 in 18.1s, `result` = *"Plan mode blocks both
     of those actions. Creating a short plan that names the blocked tools and what will run after you
     confirm."*, and the directory afterwards held **neither file** (`ls -a` = `.git`, `seed.txt`).
     Capture kept as `tests/fixtures/cursor/plan-mode.json`.
   - asked to run `git log --oneline -1` and quote it → exit 0 in 17.2s, `result` ended with
     `c089c56 seed`, which is that repo's real HEAD (`cat .git/refs/heads/master` =
     `c089c56dcc903d25e2d02a8d1763e36f3f1920d9`). So **read-only commands still run in plan mode**.
   Plan mode is thus the native "read the tree, run commands, change nothing" rung, and both
   `readonly` and `shell` use it. It is behaviour, not a sandbox: the evidence cannot distinguish
   "the edit tool was withheld" from "the model declined", and a shell command can still write, so
   `readonly` keeps its prompt rule (now narrowed to "never run a command that changes anything").
   Caveat recorded rather than measured: a `shell`-level role that needs to *start* something
   (`npm run dev`) may find plan mode classifies that as unsafe — ui-reviewer, the role that would
   care, is claude-only. `--mode ask` was never tried: it would take two more paid runs to characterise
   and plan mode already covers both rungs.

   | level | argv `Build` emits (after `-p --output-format json --model X`) | prompt rule |
   |---|---|---|
   | `readonly` | `--mode plan -f --workspace <cwd>` | READ-ONLY: never run a command that changes anything |
   | `shell` | `--mode plan -f --workspace <cwd>` | — (plan mode carries it) |
   | `edit` | `-f --workspace <cwd>` | Never run a shell command |
   | `edit+shell` | `-f --workspace <cwd>` | — (deny list only) |
   | `full` | `-f --sandbox disabled --workspace <cwd>` | — (deny list only) |

   All five verified as emitted, without paying, by pointing `backends.cursor.path` at a fake
   `cursor-agent` that dumps its argv and stdin (`claustrum run builder --backend cursor --permission
   <level> …`). `--resume <id>` is appended last when a session is resumed.

6. **#14 — the prompt is off argv, through stdin.** Both mechanisms the issue names work, and stdin
   wins:
   - **stdin: confirmed.** `printf 'reply with the single word PINEAPPLE' | timeout 180 cursor-agent
     -p --output-format json --model auto -f --workspace <tmp>/r5` (no positional prompt) → exit 0 in
     9.3s, `result: "PINEAPPLE"`. So `cursor-agent -p` reads the whole prompt from stdin when argv
     carries none.
   - **workspace rules: also confirmed.** `<tmp>/r6/.cursor/rules/claustrum-test.mdc` with
     frontmatter `alwaysApply: true` and body "Begin every reply with the word PINEAPPLE", then a
     trivial `'What is 2+2? Answer in one short line.'` → `result: "PINEAPPLE 2+2 = 4."`. The CLI does
     apply `.cursor/rules/*.mdc` (input tokens jumped 9107 → 16663, so it really loads them).
   - **Why stdin, not the rules file:** a per-job rules file lands *inside the workspace under
     review*, and `ProcessSpec.TempFiles` cannot save it — `Runner` deletes temp files in its
     `finally`, i.e. **after** the after-snapshot. Measured with the fake backend writing
     `.cursor/rules/claustrum-probe.mdc` during a run: the RunResult came back with
     `changed_files: [{"path":".cursor/rules/claustrum-probe.mdc","kind":"A"}]` and the file's full
     content in `diff`. A blind code-reviewer would be handed its own role body as part of the diff it
     is reviewing. Stdin touches nothing.
   - Consequences in code: `ProcessSpec` grew `string? StdinText` (defaulted, so the other four
     backends are untouched); `ProcessRunner` writes it and then closes stdin, keeping the
     close-immediately behaviour for every spec without text (NOTES.md "MCP child stdin inheritance
     hung git" is unaffected — stdin still ends up closed, still never inherited). The write happens
     *after* `BeginOutputReadLine` (a prompt past the pipe buffer would otherwise deadlock against a
     child blocked on an undrained stdout) and *inside* the run's timeout, with
     `StandardInputEncoding` pinned to UTF-8 **without** a BOM so Windows cannot re-encode the prompt
     in a legacy codepage or prepend three bytes to it. A broken pipe (child already dead) is
     swallowed: its exit code and stderr are the better diagnosis. The 96 KB argv guard and its
     `InvalidOperationException` are **gone** — no length limit applies to stdin, and cursor has no
     documented prompt cap to replace it with. A real 6515-byte rendered builder prompt went through
     stdin intact in the end-to-end runs.

7. **Resume — confirmed, with the id the JSON returns.** `printf 'which file did you create earlier?
   reply with its name only' | timeout 180 cursor-agent -p --output-format json --model auto -f
   --workspace <tmp>/r2 --resume 79f194af-4fcb-4d3b-8201-6c9caa501e27` (the `session_id` from probe 3)
   → exit 0 in 8.5s, `result: "hello.txt"`, the same `session_id` back, and `inputTokens: 122` — the
   history is server-side, the flag really resumed that chat, and it composes with a stdin prompt.

8. **Model naming — and the default is broken for cursor.** `--model` wants cursor's own ids
   (`gpt-5.3-codex`, `claude-sonnet-5-thinking-high`, `composer-2.5`, `auto`, …; parameterised forms
   like `'claude-opus-4-8[context=1m,effort=high]'` are also accepted per `--help`). With no alias
   configured, `Config.BuiltInDefaults` maps the builder's `frontier-coding` class to `claude:opus`,
   `--backend cursor` overrides only the backend, and cursor is handed the bare id **`opus`** — which
   it rejects (on this plan with the Free-plan message; on any plan it is not a cursor id). As
   instructed, no cursor model was added to `BuiltInDefaults`. A user needs an alias, e.g.
   `{"models": {"frontier-coding": "cursor:auto"}}` or a named one — `{"models": {"fast":
   "cursor:composer-2.5"}}` then `claustrum run builder --model fast`. Worth considering: have
   `claustrum init`'s template mention it, since `--backend cursor` alone cannot work today.
   Related: cursor has **no effort flag** — reasoning effort is part of the model id (`…-high`,
   `…-xhigh`) or a bracket override (`'claude-opus-4-8[effort=high]'`), so a role's `effort` is
   silently ignored on this backend, exactly as it already was before this pass.

   ⚠ **`scripts/smoke.sh` would have FAILed its cursor row on this machine** when this was measured:
   it gave every installed builder-harness backend a paid `run builder … --budget 0.5` with no
   `--model` (only `claude` got an explicit `sonnet`), so cursor was handed `opus`. Closed the same
   day by issue #10's remediation: each backend's smoke model now comes from
   `CLAUSTRUM_SMOKE_MODEL_<NAME>` (claude defaults to `sonnet`; unset ⇒ the row SKIPs with the
   variable to set), so a machine with cursor installed runs its row only when told which model —
   here `CLAUSTRUM_SMOKE_MODEL_CURSOR=auto`.

9. **Roles — nothing to add.** `builder`, `code-reviewer` and `tester` already list `cursor` in
   `role.json`'s `harnesses`; `ui-reviewer` stays `["claude"]`. Confirmed by running, not by reading:
   all three render on the cursor harness through the `environment.default.md` fallback (there is no
   `environment.cursor.md`), and builder and code-reviewer additionally completed real paid runs.

10. **End to end — green, twice, on the shipped code.** `claustrum run builder --backend cursor
    --brief "create hello.txt containing hi" --json --cwd <tmp>/r11 --budget 0.5 --model auto` →
    `status: success`, `changed_files: [{"path":"hello.txt","kind":"A"}]`, a real diff,
    `report_status: ok` with every field of the builder report schema filled, `session_id` set,
    `usage: {input 33530, output 804, cache_read 45696}`, `cost_usd: null`, exit 0, 22.5s. The
    `code-reviewer` role at its default `readonly` (so `--mode plan -f`) also came back
    `status: success` with `report_status: ok`, the code-reviewer schema
    (`status`/`findings`/`would_change_if_broader`), `changed_files: []` and 34.1s — plan mode does
    **not** cost you the report fence, which was the open worry about using it for `readonly`.

11. **Deny list — no native mechanism, confirmed from `--help`.** The only permission-ish flags
    cursor-agent has are `-f/--force` ("Force allow commands unless explicitly denied"), `--yolo`,
    `--auto-review`, `--sandbox enabled|disabled`, `--mode plan|ask`, `--trust` and
    `--approve-mcps`. There is no `--deny-tool`/`--disallowed-tools` equivalent at any level, so the
    deny list stays a prompt rule (`## Hard rules (never violate)` / `- Never run: <pattern>`) — and
    `-f`'s own "unless explicitly denied" refers to cursor's config file, not to anything Claustrum
    can pass per run. docs/PLAN.md §A3's promise that "`doctor` marks it advisory" is **still
    unimplemented**, and implementing it as a `Doctor.Problems` entry would be a regression:
    `BackendsCommands.ProbeLineAsync` treats any first problem as a reason to skip `--probe`'s paid
    round trip, so an advisory note would silently disable cursor's probe. It needs its own field.

**Still unverified, for the record:** any named model (Free plan), an `is_error: true` JSON document,
`--mode ask`, whether plan mode would allow a long-running dev-server command, and the Windows leg of
all of this (the stdin write is where Windows could differ — that is what `StandardInputEncoding`
guards against).

## `sync --global --only claude` against the owner's real agent files: not header-only (2026-09-21, issue #11/M3)

Issue #4's third acceptance clause — "`sync --global --only claude` reproduces the owner's five agent
files with header-only diffs" — was never evidenced. Measured 2026-09-21 on the owner's own machine
(`~/.claude/agents/`: 12 claustrum-role files, 4 roles × 3 tiers, plus `architect*.md` ×3 and
`mc-*.md` ×8), with the renderer at library 1.0.0 on commit `4c91f9b`:

- **`--dry-run` cannot even show the diff**: all 12 files come back **foreign** — none carries the
  `claustrum:generated` marker, because the first adoption with `--force` that docs/PLAN.md §B6
  describes was never run. The `mc-*.md` files are untouched, as specified. So the comparison was
  made by rendering into a redirected `HOME` and diffing by hand.
- **Raw diff size** (lines changed, owner → generated): builder 164, code-reviewer 221, tester 133,
  ui-reviewer 257; tier stubs 27–35 each. Almost all of it is *reflow*: the owner's files wrap at
  ~80 columns, the library at ~100.
- **Reflow-insensitive**, the four base roles fall into two groups:
  - **builder and code-reviewer are the same text**, modulo deltas that are *by design* and will
    never be header-only: the generated `## Report format` section (the `claustrum-report` fence
    Claustrum parses), the shared `## House rules` section, `PowerShell` in the `tools:` list, a
    single-line `description:` where the owner uses a folded `>-` scalar, "the `Agent` tool with
    `subagent_type`" wording, "caller" for "user", tier bullets naming model classes instead of
    model ids, and `you'd` → `you would`.
  - **tester and ui-reviewer diverge in substance**: the owner rewrote both *after* they were ported
    (tester ported 2026-09-14, ui-reviewer 2026-09-18). The owner's tester now opens with "You prove
    a change is correct by writing and running its tests…", has a "The quality gate — run it, report
    the exact result" section and the "a declared skip is an honest result" paragraph; the owner's
    ui-reviewer is a different document (Preconditions, the browser-adapter verb table, data rules
    on shared targets, its own tier section). The library still renders the older texts.
- **`architect` is not in the library at all** — the plan's "five files" counts it, but no
  `roles/architect/` exists; it arrives with M4's `coordinate`.
- The owner's files are themselves mid-edit: `tester.md` carries the 2026-09-21 "whichever shell
  tools you actually have" wording, `code-reviewer.md` still the older "both a `Bash` tool and a
  `PowerShell` tool" one.

**Outcome:** the clause as written is unsatisfiable *by design* (the report and house-rules
sections must exist), and separately unmet for two roles because the library lags the owner's
edits. What closes the gap: (1) restate the criterion as "body-identical outside the generated
`## Report format`/`## House rules` sections and the frontmatter", (2) re-port `tester` and
`ui-reviewer` from the owner's current files — tracked as a follow-up issue, since porting the
owner's prose is a content decision the owner should see, not a renderer fix.

## Issue #13's non-cursor boxes stay open (2026-09-21)

The cursor checklist — the M3 gate — is ticked above with real captures. The `copilot`, `opencode`
and `api` boxes are unchanged: as of 2026-09-21 this machine has none of those binaries installed
and no `OPENROUTER_API_KEY`/`ANTHROPIC_API_KEY`, and `gh auth` carries no Copilot entitlement to
lend the copilot CLI. They remain fixtures-plus-inference, as the per-backend sections of
2026-09-18 state, until an install or a credential exists somewhere.

Docs checked for #14 the same day, before measuring: cursor.com/docs/cli/reference/parameters lists
no prompt-file flag and says nothing about stdin; cursor.com/docs/context/rules documents
`.cursor/rules/*.mdc` and `AGENTS.md` for "Agent (Chat)" only. Both mechanisms were then measured
against the real CLI — see the cursor section: stdin works and is what shipped.
