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
