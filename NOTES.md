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

*Superseded 2026-09-22 by "The opencode and api backends, validated against real endpoints" below,
which replaced three of the four fixtures here with genuine captures — kept as the record of why
`curl` and the `api:<provider>:<model>` spec are shaped this way.*

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

*Superseded 2026-09-22 by "The opencode and api backends, validated against real endpoints" below.
⚠ Every argv and env fact confirmed here was confirmed against **1.18.31**, and 2.0.12 removed
`--dir`, `--variant`, `OPENCODE_PERMISSION` and the run-in-this-process's-environment assumption.
Read it as history, not as a reference.*

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

*Its unconfirmed halves are superseded 2026-09-22 by "The copilot backend, validated against a real
install" below — kept as the record of what was guessed and why. Three of the guesses were wrong.*

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

*The frontmatter and `~/.copilot/agents` caveats below are superseded 2026-09-22 by "The copilot
backend, validated against a real install": both are measured now, and the frontmatter it wrote was
silently failing to load.*

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
"The cursor backend") — superseded by CursorSync (2026-09-22, #25): `cursor` is a fourth `--only`
value, see "CursorSync: agents, skill and mcp.json, doc-confirmed but never round-tripped".

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
  (`.cursor/`) but only reported, never synced (no `CursorSync` exists) — superseded by CursorSync
  (2026-09-22, #25): a detected `.cursor/` is now synced like the rest.
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

⚠ Since 2026-09-22 the funnel is no longer the *only* writer of an entry:
`BudgetLedger.RecordFinishedAsync` writes one finished entry directly, with no admission, no `.live`
file and no reservation, for the one run that is deliberately not a member of the tree it pays for —
the coordinator's own architect (see "M4 follow-ups…", issue #21). It is not an exception to the
funnel rule but its complement: the funnel closes entries that were *admitted*, and this one never
was.

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
  whose producer side is M4's. ⚠ Superseded 2026-09-22: `coordinate` shipped and is that producer
  (see "coordinate: a spawned architect…"); this paragraph stays as the record of what the ledger
  shipped against. The host-architect topology (the chat agent exports the variable and
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
  refused. §D3's `coordinate` (M4, 2026-09-22) instead puts `CLAUSTRUM_PARENT_JOB` (and
  `CLAUSTRUM_HOME` when set) on its architect's *request env*, where caller-supplied values beat the
  allow-list, and that architect's own children inherit both from the backend's shell; the
  coordinator is not a member. ⚠ `env_passthrough: "all"` used to defeat the rule wholesale by
  copying the parent environment — since 2026-09-22 it excludes `BudgetLedger.TreeVariable`
  specifically, the one name that must never be inherited.
  ⚠ The allow-list is therefore load-bearing for `ProcessRunnerTests`' "filtered variable" marker
  again: it is named `CLAUSTRUM_TEST_SECRET_<guid>`, and it only proves filtering while no
  `CLAUSTRUM_` prefix is on the list.
- **The whole ledger API is async** (`AdmitAsync`/`CompleteAsync`/`RecordFinishedAsync`/
  `PeekRemainingAsync`/`ReadAsync`):
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
  an estimate — though "orders of magnitude under it" turned out false the first time it ran: the
  reply is tiny, the cold-cache prompt caching is not (measured $0.12; see "The cap is $0.50" below).
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
    on shared targets, its own tier section). The library still renders the older texts — re-ported
    2026-09-22 (#16), see "tester and ui-reviewer re-ported from the owner's current files".
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

*Superseded 2026-09-22 for opencode and api by "The opencode and api backends, validated against
real endpoints" below, and for copilot by "The copilot backend, validated against a real install" —
the machine gained all three since. Kept as the record of what was still fixture-only on
2026-09-21, and for the #14 doc checks in its second paragraph.*

The cursor checklist — the M3 gate — is ticked above with real captures. The `copilot`, `opencode`
and `api` boxes are unchanged: as of 2026-09-21 this machine has none of those binaries installed
and no `OPENROUTER_API_KEY`/`ANTHROPIC_API_KEY`, and `gh auth` carries no Copilot entitlement to
lend the copilot CLI. They remain fixtures-plus-inference, as the per-backend sections of
2026-09-18 state, until an install or a credential exists somewhere.

Docs checked for #14 the same day, before measuring: cursor.com/docs/cli/reference/parameters lists
no prompt-file flag and says nothing about stdin; cursor.com/docs/context/rules documents
`.cursor/rules/*.mdc` and `AGENTS.md` for "Agent (Chat)" only. Both mechanisms were then measured
against the real CLI — see the cursor section: stdin works and is what shipped.

## coordinate: a spawned architect is a job run with the cast injected and the tree handed down (2026-09-22, issue #5/M4)

docs/PLAN.md §D3's `spawned` mode, and the producer side the §D4 ledger has been missing since it
shipped (NOTES.md "Tree budget accounting is a file ledger": *"until M4's `coordinate` exists,
nothing in the repo hands a tree id across a process hop"*). `claustrum coordinate` and the MCP
`coordinate` tool are deliberately **not** a second pipeline: both build a `DelegateRequest` for the
`architect` role and hand it to the same `DelegateEngine.RunAsync` that `run`/`delegate` use. Three
things are added to it and nothing else — the cast appended to the system body, the job id exported
as `CLAUSTRUM_PARENT_JOB`, and `gh` issues folded into the brief — so tier precedence, `--model`
winning over the cast, `CastBudget`, the blind gate and worktree isolation are all the code that was
already there. `CoordinateEngine.PlanAsync` is that assembly step: it returns a `CoordinatePlan` and
runs nothing itself. (Its sibling `CoordinateEngine.RunAsync` came later, for issue #21: it is the
one place both doors share for "run the delegation, then record what the architect cost".)

### The cast rides in the system body as call-site data

`DelegateRequest.SystemAppendix` is appended to `RenderedRole.SystemBody` between
`RoleRenderer.Render` and `Config.Resolve`. The alternative — a `{{cast}}` token in
`roles/architect/ROLE.md` — was rejected for the reason NOTES.md "A cast is call-site data, not a
Core concept" already gives: `Claustrum.Roles` would have to learn what a cast is, every harness's
ROLE.md would gain a token only one caller can fill, and a role rendered by `sync` (no cast in
sight) would have to render it empty. Appending after the renderer costs two lines in
`DelegateEngine`, keeps the appendix out of `sync` output entirely, and lands it in the job's
`system.md` for free, because `Runner` writes whatever `Config.Resolve` produced. Measured
2026-09-22: `system.md` ends with the `## Coordination` section and `request.json`'s `env` carries
the tree id, both written *before* the backend lookup — so a `--model nonexistent:x` run
(`backend_missing`, exit 3) still leaves both artifacts to inspect for free.

The appendix itself is `CoordinationBrief.RenderSystemAppendix(cast, castName, cwd, modelOverride)`
— pure and dependency-free so it can be asserted against a hand-built `Cast` with no gh, no backend
and no job on disk. ⚠ It took a `jobId` until 2026-09-22; the three lines that name the job now emit
`DelegateRequest.JobIdToken` instead, because the id does not exist when this renders any more (see
"M4 follow-ups…", issue #23). A 4-argument call still compiles and now means `modelOverride`. It states facts, not prose: one line per role (architect first, then builder,
code-reviewer, ui-reviewer, tester, then any other cast role alphabetically), the tree budget, the
`jobs budget <id>` command, the exact `claustrum run … --cast … --cwd …` line to delegate with, the
`claustrum/<jobId>` work branch and the rebase-never-merge rule (owner's rule 2), and
`.claustrum/briefs/<n>-<role>.md`. A role whose cast entry is null — or absent, which
`CastApplication` already treats identically — prints `not needed for this cast — do not delegate to
it`, because an architect that cannot see the cast file has no other way to know.

⚠ The appendix describes **the cast**, not this invocation, and that is deliberate: it is what the
architect delegates *from*. The one place the two could silently disagree — `coordinate --model X`
overrides this run only — now names both: the architect's line appends `(this run: X)` whenever the
`--model` flag was given (the flag, not the resolved value, which is the cast's own model on every
invocation that did not override it).

### `budget_exceeded` is four messages, and only one of them means stop

The first version of this appendix told the architect that a `budget_exceeded` child *"means the
tree is spent: stop and report what is done. Do not retry it."* That is wrong for the commonest way
it happens, and step 5 of the architect role starts `code-reviewer ‖ ui-reviewer` in exactly the
shape that triggers it. **Both texts said it** — `roles/architect/ROLE.md` in its own voice as much
as the injected `## Coordination` — so the standing instruction and the per-invocation one agreed on
the wrong answer, and an architect obeying them silently drops half a review pass: the sibling
refused in milliseconds gets reported as a spent tree and is never restarted.
`BudgetLedger.AdmitAsync` grants `min(requestedCap ?? remaining / share, remaining)`, and `Share`
is the role's `max_parallel` — **1 for every role that has none**: both reviewers, the tester, a
builder in a cast that omits it. So the first live child of such a role
reserves 100 % of the remainder for its whole lifetime, and a sibling started alongside it comes
back refused in milliseconds **having spent nothing**.

A second pass found that two branches were still one too few: `AdmitAsync` writes **four** refusal
messages and the text routed two of them, so the third and fourth fell through to whichever branch
the architect guessed. All four measured 2026-09-22 against the real binary (a fake backend on
`backends.claude.path`, nothing paid), and the rule now quotes the fragment that identifies each:

- `…, $0.00 remaining; nothing left for role 'tester' while 1 running job(s) hold $2.00 — wait for
  one to finish` — a live sibling holds the remainder. Wait for one running child to finish, then
  start it again.
- `…, $2.00 remaining; --budget 5.00 exceeds it` — **the same bullet's own advice causes this one**:
  an architect told to give each concurrent child an explicit `--budget` lands here the moment it
  asks for more than is left. Start it again with `--budget` at most the remainder, or wait for a
  sibling to free more.
- `…; the slice for role 'tester' ($0.00 / 1) rounds to $0.00 — pass --budget (at most $0.00) to
  claim an explicit slice` — the ledger itself advises the retry, and it admits a `--budget` at or
  under the remainder.
- `…, $0.00 remaining; nothing left for role 'tester'`, with **no** `while … hold …` clause — the
  tree really is spent. Stop and report what is done.

⚠ **"the retry is refused too" was false, and it is gone from both texts.** It survived the first
pass in `roles/architect/ROLE.md`, attached to the floored-slice case — the one refusal whose own
message names the `--budget` to retry with. A refusal only stands as long as its cause does: a
sibling's reservation ends when the sibling does, and the two `--budget` refusals refuse a *number*,
not the job.

The other half of the rule is preventive, because waiting is not free: two children started at once
must each carry an explicit `--budget <usd>` **that together fit the remainder**, which
`AdmitAsync` clamps but never divides, so neither reserves the whole of it. ⚠ `--budget` is still no
escape for a child refused by a sibling's reservation — the ledger comment in `BudgetLedger.cs` says
why — only for the one that has not started yet, or for a retry once the remainder has moved. The
appendix also carries a line telling the architect to read `error` whenever `status` is not
`success` — it is the only field carrying the reason, for a refused budget as much as for a
blind-gate rejection.

**The two texts are now identical word for word**, the `roles/architect/ROLE.md` copy merely wrapped
at 100 columns: one is the role's standing instruction, the other is what this invocation injects,
and the first pass proved they drift — ROLE.md kept a bold lead-in *and one extra clause*, and that
extra clause is exactly the sentence that turned out to be wrong.

### The coordinator is not a member of its own tree

Membership is decided in `DelegateEngine` by `BudgetLedger.TreeIdFor(AppServices.Platform)` — the
*coordinate process's own* environment. `coordinate` puts `CLAUSTRUM_PARENT_JOB` only on the
architect's **request env**, where `EnvAllowList` lets caller-supplied values win, so the backend
process and everything it spawns are members while the coordinator is not. Making it a member
instead would have it hold the whole tree cap as a reservation for its entire life and leave every
child `$0.00 remaining` — measured 2026-09-21 for the nested case, and the reason `CLAUSTRUM_` is
deliberately absent from the allow-list's prefixes.

⚠ **The price is that the cast's `budget_usd` can be spent twice.** With no tree of its own, the
architect's run takes the §D1 per-run cap, which *is* the cast's `budget_usd`; its children are then
capped by the ledger against the same number. Worst case is 2× the cast budget. This is not hidden — but until
2026-09-22 it was only *claimed* here: `PrintHuman` printed no cost at all and `--json` never read
the ledger. What is printed now: human output's header ends with `architect cost $X` (`-` when the
backend reported none) followed by `tree spent $Y, reserved $Z`, the children's ledger totals;
`--json` keeps stdout to exactly one RunResult document (`cost_usd` is the architect's own number)
and writes the same tree line to **stderr**. Both print on every run, but that line has two shapes.
A **capped** cast reads the ledger — `tree spent $Y, reserved $Z`, and a ledger directory that was
never created is a true `$0.00`. An **unlimited** one reads `tree: unlimited (children not
accounted)`, because `DelegateEngine` builds no `JobTreeBudget` without a cap and its children
therefore write no ledger entry at all: `$0.00` there was a measurement nobody took, and `claustrum
jobs budget <id>` says `(no entries)` for the same reason. The appendix's `Budget:` line carries the
same warning.

⚠ The closing read is guarded (`TimeoutException`/`IOException`/`UnauthorizedAccessException` →
`tree: ledger unavailable (<message>)`, exit code untouched) and skipped entirely when the ledger
directory does not exist: it is another process's locked file, and `LockAsync` gives up after 5s —
unguarded, that read could turn a successful run into exit 1.

⚠ **Superseded 2026-09-22 (issue #21): the ledger entry the rest of this paragraph called "the fix,
when it bites" exists.** The cap decision did not change — the architect's per-run cap is still the
whole cast `budget_usd`, for the reason recorded in "M4 follow-ups…" — but its cost is no longer
invisible to the tree: `CoordinateEngine.RunAsync` writes the finished run as an entry of its own
tree, so `jobs budget <id>` totals the coordinator with its children and the two printed lines read
`architect cost $X` and `tree spent $Y (architect $X included), reserved $Z`. The doubling above is
therefore *measured* now rather than merely argued. The original plan is kept for the reasoning it
carries: `Runner`'s admission/completion funnel exists for *members*, and a non-member has no
reservation to complete — which is exactly why the entry is written outside that funnel.

### `CLAUSTRUM_HOME` travels with the tree id

Forwarded on the same request env whenever the coordinate process has it set, because the ledger and
the job store hang off one home (`JobDirectory.ResolveHome`): without it the children write their
entries under `~/.claustrum/budget/<tree>` while `claustrum jobs budget <id>` — and `--reset` —
reads the relocated one. The ledger's own section already carried this as a ⚠ addressed to M4;
this is it being honoured. Measured 2026-09-22 with `CLAUSTRUM_HOME` pointed at a scratch dir:
`request.json` `env` = `{CLAUSTRUM_PARENT_JOB: <jobId>, CLAUSTRUM_HOME: <scratch>}`.

### The `architect` questionnaire key became the architect role's question

`CastQuestionnaire` asked a fixed `"architect"` mode question and then one question per
`roleLibrary.ListRoles()`, and `CastBuilder.FromAnswers` *threw* if that list contained
`"architect"` — a guard written in M2 for a role that did not exist yet. M4 adds
`roles/architect/`, so on the day it landed `cast questions` would have thrown for every user and
`cast create` with it. The fix is not a call-site skip: the fixed question **is** the architect
role's question (§D2 lists it exactly that way: *"architect (host | spawned on …)"*), so the
per-role loop in `CastQuestionnaire` and the one in `CastBuilder` both skip the role named
`architect`, the throw is gone, and `Cast.ArchitectRole` names the one role that is never a key of
`Cast.Roles`. Its options are now `host` plus one `spawned on <alias>` per live model option, with
`AllowFreeForm: true` so `spawned on backend:model-id` works.

Measured 2026-09-22 on a scratch clone *with* `roles/architect/role.json` in the embedded library:
question keys are `[architect, builder, code-reviewer, tester, ui-reviewer, builder_max_parallel,
budget]` — architect asked once — and `cast create` writes
`"architect": {"mode":"spawned","model":"frontier-coding","tier":null}` with `roles` keys
`[builder, code-reviewer, tester, ui-reviewer]`. `host` round-trips as
`{"mode":"host","model":null,"tier":null}`, and a pre-M4 `{"mode":"host"}` still loads (the new
`Model`/`Tier` are optional constructor parameters).

`CastApplication.Resolve` completes the picture: for `role == "architect"` the entry is derived from
`cast.Architect` instead of `cast.Roles["architect"]`, so `claustrum run architect --cast x` and
`coordinate` resolve through one path — measured, a cast whose architect is `spawned on fast`
resolves `run architect --cast …` to model `haiku` while the role's own tier default is `opus`.

Two tests written against the old shape now contradict the design on purpose and are the tester's to
rewrite: `CastBuilderTests.ARoleNamedArchitectThrowsInsteadOfCollidingWithTheModeQuestion` (the
throw is gone) and `CastQuestionnaireTests.NoLibraryRoleCollidesWithAFixedQuestionKey` (which pins
`ListRoles()` *not* containing `architect` — true only until M4's role lands).

### `gh` failures are exit 2, and `gh` is a free doctor check

Everything `gh issue view` can fail on is the user's environment, not a backend: gh not installed,
not authenticated, a cwd with no GitHub remote (measured: `gh issue view 12 failed: no git remotes
found`, exit 2), an issue that does not exist. So `GhIssueSource` raises `CliUsageException` for a
non-zero exit, a start failure (`Win32Exception` — a missing binary), a timeout and unparsable JSON
alike, and `ExceptionBoundary` maps it to exit 2 with no stack trace. `CliUsageException` was
therefore also added to `McpExceptionBoundary`'s domain list, or the MCP `coordinate` tool would
report the SDK's generic "An error occurred invoking 'coordinate'" instead of the message that says
what to fix. A bare `backends doctor` prints a `gh:` block (§D3: "`gh` presence is a `doctor`
check") — unlike `--probe`, locating a binary costs nothing, so it needs no flag. `backends doctor
<name>` does **not**: that form is a question about one named backend, and `gh` is not one of them.
It printed there too until 2026-09-22. `gh --version` answers on two
lines ("gh version 2.97.0 (2026-07-31)" then a release URL) and `VersionProbe` keeps stdout whole,
so only the first line is printed.

### Smaller decisions worth the line

- **`Core/Process/CommandProcess.cs`** is `GitProcess` generalised to `(exe, cwd, args, timeout)`;
  `GitProcess.RunAsync` is now one line over it. A second hand-rolled `ProcessStartInfo` for `gh`
  would have re-introduced the inherited-stdin hang of NOTES.md "MCP child stdin inheritance hung
  git" — the fix lives in exactly one place again.
- **`coordinate` never reads bare stdin** (`BriefSource.TryResolve(…, allowBareStdin: false)`).
  `run` must, because a brief is mandatory there; `coordinate` has `--issues` as the other way to
  state the task, and reading a stdin nobody promised to close is the same hazard from the other
  side. `--brief-file -` still opts in explicitly, and a pipe with no flag is a usage error rather
  than a silent consumption of the caller's stdin.
- **Nothing is minted before the run can fail** — not for a usage error, and not for the cast or
  `gh` either, which the first shape got wrong. `CoordinateEngine.PlanAsync` does everything that
  can be refused (the task-source flags, loading *and resolving* the cast, the `gh` import,
  rendering the user prompt) and returns a `CoordinatePlan` that carries no job id;
  `plan.Prepare()` is the second half — config, role render, model alias — and still mints nothing
  (2026-09-22, issue #23: `ToDelegateRequest` lost its `JobPaths` parameter with it). Both doors
  call them in that order, and only then `JobDirectory.Create`. Before it,
  `JobDirectory.Create` ran first and every one of those failures left a `pending (no result.json)`
  directory behind forever — measured on the pre-fix binary: two job directories holding nothing
  but their `.claim`. `Create` also *prunes* the job store, so a refused invocation aged the user's
  history for nothing. On the MCP door the same split moves the failure out of `job_result` (which
  a caller has to know to ask for) onto the `coordinate` call itself, where `CliUsageException`/
  `CastException` reach the client through `McpExceptionBoundary` — measured 2026-09-22 over real
  stdio JSON-RPC: `cast 'nosuch' not found at …`, `coordinate needs a cast: …` and `use either
  --issues or --brief/--brief-file, not both` all came back as the tool's own error, with no job
  directory created for any of them.
- **`coordinate` does not gate on `Architect.Mode`.** A `host` cast can still be coordinated
  explicitly: the mode tells the `/claustrum` skill which path to take, it does not forbid the other
  one to a human who typed the verb.
- **`JobManager.Start` gained a `Func<JobPaths, …>` overload** because `coordinate` needs the job id
  *before* it can build its request — the id is the tree. The `DelegateRequest` overload is now one
  line over it, so both MCP async paths register their entry the same way.
- **An issue body is untrusted text, and this is the first path in the repo that carries any.** `gh
  issue view` returns whatever a third party typed on github.com, and it lands in the `## Task` of
  an architect running at `edit+shell`. `IssueImporter` renders each issue as `### #<n> — <title>`,
  the issue URL on its own line, then the body **inside a fence tagged `text`** whose length is one
  backtick more than the longest backtick run in that body — Markdown allows any fence of three or
  more, and a closing fence must be at least as long as its opening one, so a body containing a
  triple backtick cannot close it early. One line precedes the first issue saying the text is data,
  not instructions. ⚠ Fencing is containment, not a guarantee: a model can still be talked into
  obeying fenced text, and nothing downstream re-checks what came in. It is the cheapest mitigation
  that does not mangle the issue; the honest claim is "harder to confuse", not "safe".
- **`gh` is located exactly the way `backends doctor` locates it.** `GhIssueSource` handed a bare
  `"gh"` to `CommandProcess`, which on Windows only ever appends `.exe`, while the doctor probes
  through `VersionProbe` → `BinaryLocator` (PATH + PATHEXT + the npm-shim unwrap) — so a gh the
  doctor reported as *found* could fail to start on the very next command. It now goes through
  `BinaryLocator.Locate("gh", args, config: null, platform)` and runs `binary.Executable` with
  `binary.Args`; a missing gh becomes the same `CliUsageException` with the `claustrum backends
  doctor` pointer as every other gh failure. `GhIssueSource` took an `IPlatform` constructor
  parameter for it, and both doors pass `AppServices.Platform`.
- **`--timeout 0` used to mint a job directory.** `Runner.ValidateTimeout` throws the right thing
  (`--timeout must be greater than zero`, exit 2) but runs inside `DelegateEngine.RunAsync` — after
  `CoordinateCommand`/`JobManager` created the job whose id is the tree, so a typo left exactly the
  `pending (no result.json)` directory the plan/run split exists to prevent.
  `CoordinateEngine.PlanAsync` now refuses a non-positive `Overrides.TimeoutSeconds` with the same
  message, for both doors, before anything is minted — measured 2026-09-22: exit 2, and under a
  fresh `CLAUSTRUM_HOME` no `jobs/` directory was created at all. `Runner` keeps its own check: it
  is the last line of defence for `run`/`delegate`, which validate nothing earlier. ⚠ It was also the
  only *validation* on this path, and the classes it did not cover — a `claustrum.json` that fails
  `Config.Load`, a cast whose `architect.tier` no role defines (`RoleRenderException`), an unknown
  permission (`ConfigException`) — have since been fixed the way this bullet already said they had
  to be: by moving config/role loading ahead of `JobDirectory.Create`, not by another guard here
  (2026-09-22, issue #23, "M4 follow-ups…").
- **The `## Coordination` section is injected above `## House rules`, not at the end.** Appending it
  put it dead last in a 25 KB system prompt (measured: 25 456 B) — the exact position
  `RoleRenderer.ComposeSystemBody` moved `## Report format` *off*, because a live smoke test caught
  a model skipping a last-position section on short tasks. `DelegateEngine.InsertAppendix` splices
  it in before the `## House rules` heading when the body has one, and falls back to appending when
  it does not (a `RenderedRole` no `RoleRenderer` composed). The second mitigation is the one
  `Runner.AppendReportTrailer` already uses from the other side: the architect's `## Context` now
  ends with `Your system prompt carries a ## Coordination section … Follow it literally.`, because
  the end of the user prompt is the position a model honours most. Measured 2026-09-22 — the
  headings run `## Environment …`, `## Coordination`, `## House rules`.
- **MCP `coordinate` asks for `Stream: true` with no `OnStreamLine`.** Nothing echoes the output
  anywhere, but `Stream` is what makes claude emit `stream-json` instead of one JSON document at
  exit, and `ProcessRunner.Pump` writes every line to `stdout.log` regardless of the callback. With
  `Stream: false` that log stayed empty until the process ended, so `job_status.last_line` — the
  only progress an async MCP caller has — was null for the whole run. `ClaudeBackend.Parse` tries
  the document as a whole first and falls back to the JSONL reader on `JsonException`, so the
  result parses either way (both read in the code, not assumed; measured: an MCP `coordinate` job's
  `request.json` carries `"stream": true`). ⚠ `Stream: true` turned out to be necessary and not
  sufficient: both log writers were opened without `AutoFlush`, so the 4 KB `FileStream` buffer held
  a quiet run's whole output back anyway. Measured 2026-09-22 with a fake backend emitting 40 lines
  of ~80 B over 12 s (nothing paid): `stdout.log` was **0 bytes at every poll** for the entire run
  and arrived complete, 3610 B, at exit — `last_line` null throughout, on a job that was working the
  whole time. `AutoFlush = true` on both writers now, and the same run grows the log line by line
  (616 B at 2 s, 1144 at 4 s, 1760 at 6 s, …, 3520 at 12 s). A flush per line is nothing at these
  volumes next to a progress field that never moves. `JobManager.LastLine` stays best-effort: a line
  longer than the writer's 1 KB char buffer still reaches disk in pieces.
- **Both values in the delegate command are double-quoted** (`--cast "default" … --cwd "/path with
  spaces"`). Double quotes mean the same thing to bash and PowerShell and leave backslashes intact,
  which is what a Windows cwd needs; unquoted, a single space in either value split the command.
- **`env_passthrough: "all"` no longer forwards tree membership.** `EnvAllowList.Build` copied the
  whole parent environment under `passthroughAll`, so a tree member's inherited
  `CLAUSTRUM_PARENT_JOB` reached the backend it spawned and a grandchild could join a tree whose
  remainder its own parent already holds reserved — the exact failure the missing `CLAUSTRUM_`
  prefix exists to prevent, defeated by a config flag. `passthroughAll` now excludes that one name
  (`BudgetLedger.TreeVariable`, public so the two files share one spelling); `callerEnv` still
  wins, which is how `coordinate` hands it down on purpose.
- **Over-fanning is bounded by the run's timeout, not by patience.** A delegation beyond the role's
  `max_parallel` does not queue indefinitely: it waits for a slot up to `--timeout`
  (`defaults.timeout_seconds`, 1800 s) and then gives up. ⚠ It gave up as an exception on stderr
  with **no JSON** until 2026-09-22 — the one failure shape an architect parsing `--json` cannot
  read — and now comes back as a `status: failed` RunResult naming the cap (issue #20, "M4
  follow-ups…"). `roles/architect/ROLE.md` says the second thing.
- **The tool package describes itself.** `Claustrum.csproj` carries `Description`,
  `PackageLicenseExpression` MIT (matching the repo's LICENSE), `PackageProjectUrl`, `PackageTags`
  and a packed `README.md`; the nuspec previously said `Package Description` and named no licence
  at all — and that nuspec is the page `dotnet tool install -g claustrum` sends people to.

**Measured end to end 2026-09-22** against a real `claude` backend (haiku, $0.046): `coordinate
--cast <spawned-on-fast> --brief "x"` spawned the architect with the `## Coordination` section in
its system prompt, came back `success` with a parsed `claustrum-report` and changed no files. It
printed no cost line at all, which was the defect and not the design; re-measured after the
remediation on a `--model nonexistent:x` run of a **capped** cast (nothing paid), the two lines read
`architect cost -` and `tree spent $0.00, reserved $0.00` — the second because the architect
delegated nothing and the ledger directory for its tree was never created. The same run against an
uncapped cast reads `tree: unlimited (children not accounted)` instead. ⚠ Both capped numbers moved
on 2026-09-22 (issue #21): the tree line now carries the architect's own entry, so the same run
reads `tree spent $0.00 (architect $0.00 included), reserved $0.00` and the ledger directory *is*
created. The uncapped line is unchanged.

## The architect role, ported last (2026-09-22, issue #5/M4)

docs/PLAN.md's milestone table puts `architect` last "because it issues the delegations": it is the
only role whose text has to name machinery — casts, the blind gate, worktree branches, the tree
budget — that M2/M3 had to exist first.

- **`permission: edit+shell`, not `readonly`.** The architect writes brief files and runs
  `claustrum`/`git` (branch, rebase), so the tools have to allow writing; "it never ships production
  code" therefore stays a prose rule, the same compromise §B5 already accepts for Cursor. It is
  stated twice on purpose — a ground-rule bullet in `ROLE.md` and `nonNegotiable[0]`, which the tier
  stubs repeat — because nothing enforces it.
- **What became a part.** `parts/delegation.claude.md` carries the Agent-tool mechanics
  (`run_in_background: false`, several calls in one message, "ending your turn is not waiting",
  "only source-tree changes prove a delegate alive", the `-xhigh`/`-max` variant names);
  `parts/delegation.default.md` expresses the same contract as `claustrum run --cast` / MCP
  `delegate` + `delegate_async`. The owner's memory-file access recipe
  (`~/.claude/projects/<slug>/memory/ui-access-<target>.md`) is `parts/ui-access.claude.md`, with a
  harness-neutral "untracked notes file, never a tracked one" fallback.
- **The tier ladder ported as a rule, not as a table.** The seed names models per role (builder →
  `opus`, tester → `sonnet`, "the code-reviewer's model scales with the tier"). In this library only
  `builder` and `architect` keep one model class across all three tiers; `tester`, `code-reviewer`
  and `ui-reviewer` all go `standard-coding` → `frontier-coding` at `xhigh`. So the ROLE.md says the
  invariant instead: **the architect never picks a model — the library and the cast do — it picks the
  tier**, and a heavier tier also buys a stronger class for the reviewing roles. Writing the seed's
  model names in would have been false for three roles and meaningless for a DeepSeek-only user.
- **`## Working from a cast` is what M4 adds to the seed.** Under a cast every delegation goes
  through Claustrum with `--cast` — a native subagent would quietly run the role on the architect's
  own model and outside the tree budget — a role the cast marks `null` is not delegated to, briefs
  use the fixed H2s (the runner refuses a blind role's brief carrying `## Context`/`## Plan`/
  `## Rationale`/a pasted report), results are read from `status`/`report`/`changed_files`/
  `worktree`/`branch`, branches integrate by rebase + fast-forward and never a merge commit or a
  push, and a `status: budget_exceeded` child is routed on its `error` text (see "`budget_exceeded`
  is four messages") rather than taken as the end of the tree. It deliberately
  restates what `CoordinationBrief.RenderSystemAppendix` injects as `## Coordination`, because a
  *host* architect adopting the role with a cast gets no appendix and must behave identically.
- **"architect" is a library role for the first time.** The cast questionnaire's fixed
  architect-mode question used to guard against exactly that (`CastBuilder` threw; `CastQuestionnaire`
  would have minted a duplicate key). M4 resolves it by making the fixed question *be* the architect
  role's question and skipping that role in the per-role loop, so the guard and its
  `Assert.DoesNotContain("architect", ListRoles())` precondition test are gone.
- **New by-design divergences from the owner's own `architect.md`**, on top of those already listed
  under "`sync --global --only claude` … not header-only": `model: fable` → `model: opus`
  (ClaudeSync's class mapping has no `fable` rung), the `CLAUDE_CODE_MAX_SUBAGENT_SPAWN_DEPTH`
  parenthetical and the `Explore` agent dropped (host configuration and a Claude-only agent, neither
  an instruction), the Odoo-specific access recipes condensed to their rules, and one adaptation: a
  dead delegate's half-written slice is handed to a fresh builder rather than "finished yourself",
  which the seed allowed and `nonNegotiable[0]` now forbids.

## docs/INSTALL.md records one gap rather than papering over it (2026-09-22, issue #5/M4)

*Closed 2026-09-22 by #24 — see "The claustrum MCP server in the Claude desktop config"; the
paragraph below is the record of the gap as it stood.*

docs/PLAN.md §B4 lists `%APPDATA%\Claude\claude_desktop_config.json` among `--global`'s targets, but
`ClaudeSync` writes only `~/.claude/` there. INSTALL.md says so and tells the reader to register the
server by hand — a doc that claimed the file was written would be discovered wrong on a fresh
machine, which is the one place this document is read.

## Releasing: five RIDs, one tool package (2026-09-22, issue #5/M4)

`release.yml` fires on `v*` tags and on `workflow_dispatch` (a dry run that builds artifacts and
publishes nothing). The tag is the version: `v0.1.0` → `-p:Version=0.1.0`, derived once in a tiny
`version` job because the Windows leg's default shell is pwsh and `${GITHUB_REF_NAME#v}` is bash.

- **The tag reaches a `run:` through `env:`, never through `${{ }}`.** `${{ }}` in a `run:` body is
  textual substitution into the script, and `GITHUB_REF_NAME` is attacker-settable from a fork, so a
  tag containing `` ` ``, `$` or `;` executed on the runner. The `version` job now validates the tag
  against `^v[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?$` and fails loudly otherwise, and emits the
  bare version; every consumer receives it as `VERSION` in `env:` and passes it quoted
  (`-p:Version="$VERSION"`) through a bash array, so an empty version on `workflow_dispatch` still
  adds no argument and leaves the `Directory.Build.props` default. The invariant to keep:
  `grep -n '\${{' .github/workflows/release.yml` must show no hit inside a `run:`.
- **Runner labels, checked 2026-09-22:** `macos-13` was retired 2025-12-04, so osx-x64 builds on
  `macos-15-intel` — the last x86_64 macOS label, available to Aug 2027. docs/PLAN.md §A8 named
  `macos-13` until today; the workflow is the current truth. `actionlint` 1.7.7 flags
  `macos-15-intel` as unknown: its label list is older than the label, and the finding is a false
  positive.
- **Packing the tool needs no AOT escape hatch.** Measured:
  `dotnet pack src/Claustrum/Claustrum.csproj -c Release` succeeds with `PublishAot=true` *and*
  `PackAsTool=true` in the csproj — the package carries the IL build, the AOT publish is a separate
  invocation. Do not "fix" the pack step by adding `-p:PublishAot=false`; nothing needs it. Verified
  end to end: install from a local `--add-source`, `claustrum --version` prints `0.1.0-alpha+<sha>`,
  uninstall.
- **The trap `PackAsTool` sets:** it suppresses the apphost, so `dotnet build -c Release` produces
  no `claustrum` executable at all — only `claustrum.dll`. Anything that expects a runnable binary
  from a plain build needs a *publish* first. Both smoke scripts' fallback resolver did not, and was
  fixed on 2026-09-22 (#22) — see "Both smoke scripts' fallback resolver publishes, it does not
  build".
- `NUGET_API_KEY` is declared as **job**-level `env` in the pack job, not on the push step: a step's
  own `env:` block is not visible to that step's own `if:`, so a step-level declaration would make
  the guard read empty and always skip.

## Reading a live job log needs FileShare.ReadWrite (2026-09-22, PR #26 windows leg)

`ProcessRunner` holds `stdout.log`/`stderr.log` open for the whole life of the child process
(`new StreamWriter(path, append: false)`, which is `FileShare.Read`). Every reader in the repo used
the .NET default share mode — also `FileShare.Read` — and on Windows that is not enough: the
*reader* must permit the writer as well, so a read of a still-running job's log failed outright:

```
IOException: The process cannot access the file '…\stdout.log' because it is being used by another process.
```

Measured on CI's `windows-latest` leg of PR #26. It hit MCP `job_status` for **every** running job
(`JobManager.LastLine`, `File.ReadLines`) and `claustrum jobs logs <id>` on a running job
(`JobsCommands.Logs`, `File.ReadAllText`). Linux never noticed: POSIX advisory sharing lets any
reader in regardless of the writer's handle, so the bug is invisible on the platform this was
developed on.

The comment that used to sit above `JobManager.LastLine` — "ProcessRunner opens stdout.log with
FileShare.Read … so a concurrent read here is safe" — had exactly one half of the truth. A writer's
share mode says what *other* handles it tolerates; it says nothing about what the reader must
itself tolerate. Both sides have to agree, and only one side was ever considered.

The fix is one place that knows this: `Claustrum.Core.Jobs.JobLog` opens
`new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)` under a UTF-8
`StreamReader` and exposes `ReadLines` / `ReadAllText` / `LastLine`. Every reader of a job log goes
through it. `result.json` and `request.json` are deliberately *not* in scope: they are written whole
after the run, with no handle held across the read, so the plain `File.*` calls on them are correct.

⚠ Do not "simplify" `JobLog` back to `File.ReadLines`/`File.ReadAllText`: they carry the default
share mode and reintroduce the Windows failure, which no Linux run will ever tell you about.

## M4 follow-ups: prepare before mint, a gate timeout is a result, the architect is in the ledger (2026-09-22, issues #23 #20 #21)

Three findings of the M4 blind review, fixed in one pass. They share one theme: the moments where
`coordinate` and `delegate_async` could still produce *no* answer — a job directory nobody would
close, an exception where a JSON document was promised, a cost nobody counted.

### Resolve everything before the job directory exists (#23)

`DelegateEngine` is now cut in two at the mint: `Prepare(DelegateRequest) -> PreparedDelegation`
(config layers, tier model class, backend, `RoleRenderer.Render`, the appendix, `Config.Resolve`,
the tree, the per-run budget/timeout/permission, `RunOptions` without its `Admission`) and
`RunAsync(PreparedDelegation, JobPaths?, ct)` (the concurrency gate, worktree isolation, `Runner`).
The two `RunAsync(DelegateRequest, …)` overloads survive as `RunAsync(Prepare(request), …)`, so
`run` and `delegate` are unchanged; the callers that mint a directory themselves — `JobManager.
Start(DelegateRequest, …)` for MCP `delegate_async`, and both `coordinate` doors — call `Prepare`
**first**, so the whole class of "malformed input read after the mint" now refuses with nothing on
disk. Measured under an isolated `CLAUSTRUM_HOME`, nothing paid: a syntactically broken
`claustrum.json` ⇒ `coordinate` exits 2 with the parser's own message and **no new job directory**
(9 → 9); the same over real stdio JSON-RPC ⇒ the `coordinate` *and* `delegate_async` tool calls come
back `isError: true` carrying that message, still 2 → 2 job directories. A cast whose
`architect.tier` is `bogus` ⇒ `role 'architect' has no tier 'bogus'`, exit 2, nothing minted.

**The job id is the one thing `Prepare` cannot know, and `coordinate` needs it in two places** — the
`## Coordination` appendix (`Job tree:`, `Inspect:`, `Work branch:`) and the architect's request env
(`CLAUSTRUM_PARENT_JOB`, whose value *is* the id). Neither can be patched in afterwards from outside:
the appendix is spliced into `RenderedRole.SystemBody` *before* `Config.Resolve`, so by the time a
caller holds a `PreparedDelegation` it is buried inside `ResolvedRole.SystemPrompt`. So the text is
rendered with an exact placeholder, `DelegateRequest.JobIdToken` = `{{job_id}}`, and
`PreparedDelegation.ForJob(jobId)` replaces it in the system prompt and in every env value at the
last possible moment, inside `RunAsync`. A plain `string.Replace` of an exact token — deliberately
not a template engine, and a no-op for every other caller.

⚠ **It borrows `{{…}}` from `TemplateRenderer` but is not one of its tokens** (docs/PLAN.md B2:
"plain `{{token}}` replacement, no engine. Unknown token = render error"). The appendix is spliced
into the body *after* `RoleRenderer.Render` has finished — that is the whole point of
`SystemAppendix` — so the role templater never sees `{{job_id}}` and cannot refuse it. Anything that
ever routes call-site text back through the renderer would turn this into a render error; the two
layers share a spelling, not a mechanism.

⚠ **Substitution only happens when a caller hands in a `JobPaths`.** The `job is null` path is the
CLI `run` ordering, where `Runner` mints the directory itself *after* its blind gate (NOTES.md
"Runner always yields a result after the process ran" and review finding #7) — that ordering is
deliberately kept, and it means the id genuinely does not exist at substitution time. Nothing breaks,
because `coordinate` is the only renderer of the token and both its doors mint the job themselves.
A `--env FOO={{job_id}}` typed by hand on `run` would survive verbatim; on `delegate_async` it would
not. That is a curiosity, not a feature.

**`CoordinatePlan` now carries the resolved cast, not the cast's file path.** `PlanAsync` calls
`CastApplication.Resolve` once and keeps `Tier`/`Overrides`/`CastBudget` on the plan, and
`ToDelegateRequest()` takes `CastName` from the `Cast` object it already holds — so the second read
of `.claustrum/casts/<name>.json` that used to happen *after* the mint (and could throw over a job
nobody would close) is gone. ⚠ `PlanAsync` does read the cast file twice in a row (`CastStore.Load`
for the plan's own `Cast`, then `CastApplication.Resolve` inside it): both reads are pre-mint, so
the failure mode is gone, but deduplicating them wants a `CastApplication` overload that takes an
already-loaded `Cast` — deliberately not written here, because `CastApplication` is the one place
that maps `cast.Architect` onto a role entry and this slice did not own that file.

The order both `coordinate` doors now run is: **plan → prepare → mint → run**. `Runner.ValidateTimeout`
stays where it is: it is the last line of defence for `run`/`delegate`, which validate nothing earlier.
Good-path behaviour is unchanged, measured on the same runs: `system.md` carries `## Coordination`
between `## Environment` and `## House rules` with the real job id on all three lines, `request.json`'s
`env` is `{CLAUSTRUM_PARENT_JOB: <id>, CLAUSTRUM_HOME: <scratch>}`, and neither file contains the
token.

- **The MCP tool descriptions say so now (2026-09-22, review of the follow-ups batch).**
  `delegate_async` still promised a plain "returns immediately with a job id", which after this
  change is half the story: config, role and model resolution happen **in the tool call**, so an
  unknown role, a broken `claustrum.json` or a tier the role has no model for is that call's own
  error with no job created, and only the run is deferred. A host that cannot read that from the
  description retries the call or polls a job id that was never minted. `job_status`'s `'failed'`
  sentence was stale the other way round — it said the job threw "before producing a RunResult",
  where what is left after #23 is a job that threw *after it was started* (a blind-gate rejection is
  inside the run, not in the call). All four state names stay in that string: `McpToolSurfaceTests`
  pins `running`, `done`, `failed` and `unknown` one by one.

### A concurrency-gate timeout is a `RunResult`, not an exception (#20)

`RoleConcurrencyGate.AcquireAsync` throws `TimeoutException` when every slot stays taken for the
run's whole `--timeout`. That exception used to escape `DelegateEngine`, so an over-fanned
`claustrum run builder --json` exited 1 with a message on stderr and **nothing on stdout** — the one
failure shape an architect parsing `--json` cannot read, while every other pre-spawn refusal on that
path (`backend_missing`, `budget_exceeded`, a ledger error) already produced a document. The acquire
is now wrapped in a `catch (TimeoutException)` — exactly that type, so a genuinely broken lock
directory still surfaces as itself — which returns `Runner.RefuseAsync(job, role, Failed, ex.Message)`.

`RefuseAsync` is a small public seam on `Runner` rather than a second copy of the 20-field result
record in `DelegateEngine`: it builds the existing `NoProcessResult` shape and leaves through the
same `FinishAsync` funnel with `reservation: null`, so `result.json` is written exactly as for every
other "nothing ran" refusal. It is `static` because it touches no instance state, and public because
the caller that needs it decides *before* `Runner` is ever entered.

⚠ **The `Admitted: false` refusal one branch below deliberately did *not* move onto the same seam.**
It still goes through `Runner.RunAsync`, which runs `ValidateTimeout`, brief resolution and the blind
gate before it reaches the refusal — calling `RefuseAsync` directly would silently skip all three and
change which failure a doubly-bad invocation reports.

Measured 2026-09-22 (cast `par2`, `max_parallel: 2`, both slot files held by an unrelated `flock -x`,
`--timeout 2`, `--backend nonexistent` so nothing could ever be paid): one RunResult document on
stdout, `"status":"failed"`, exit 1, `"worktree":null,"branch":null`, and `error` = ``all 2
'par2__builder' slots under '<cwd>/.claustrum/locks' stayed unavailable for 2s (last error: …) — a
sibling run may be stuck; check `claustrum jobs list` ``. A `result.json` in the job directory and
nothing else in it, **no `.claustrum/worktrees/<id>` and no `claustrum/<id>` branch**. With the locks
released the same command isolates normally (worktree and branch cut, `backend_missing`, exit 3), so
the two-slot path is untouched.

`roles/architect/ROLE.md`'s `max_parallel` bullet now says the waiting run comes back `status:
failed` with the cap named in `error`, and keeps "do not start more builders at once than
`max_parallel`". ⚠ That file is rendered into `tests/golden/claude/architect.md` and
`architect-xhigh.md`, which are compared byte-for-byte — they have to be re-recorded.

### The architect's cost is in the ledger; its cap stays the cast's (#21)

**Decision: the coordinator's per-run cap remains the cast's whole `budget_usd`.** A share (say half)
would have to be guessed before the architect knows how many children it will start or how much
reading, planning and integrating its own run will cost — and the architect is precisely the run
whose spend is the wildcard. The failure it would buy is the worst one available: a coordinator
refused or truncated *mid-flight*, after its children have already been paid for, losing the only
process that knows what the batch was doing. An overspend of at most 2× a number the user chose is
cheaper than that. What was wrong was not the cap but the invisibility: `claustrum jobs budget <tree>`
never saw the coordinator at all.

So the cost is recorded instead of capped. `BudgetLedger.RecordFinishedAsync(platform, treeId, jobId,
role, cap, cost, startedAt)` writes one `<jobId>.json` under the ledger lock with `finished_at` set
and `abandoned: false` — the same `BudgetLedgerEntry` shape, and **no `.live` file**, because nothing
was ever reserved for this run, only spent. `CoordinateEngine.RunAsync` is the one place both doors
share: it runs the delegation and then, **only for a capped cast** (an uncapped one has no ledger at
all, so `$0.00` would be a measurement nobody took), records the finished run against its own tree.
`cost` mirrors `BudgetReservation.CompleteAsync`'s rule — the reported cost, the granted cap when the
backend ran and reported none, `$0` when nothing ran. ⚠ "Ran" is read off `RunResult.DurationSeconds
> 0`, not off the exit code: every "nothing ran" shape `Runner` builds hardcodes duration 0, while a
*killed* process reports a real elapsed time and an exit code of `-1` on Windows — the same value
`NoProcessResult` uses, and therefore no discriminator at all.

⚠ **A `coordinate` that is itself inside a tree records a cap-less entry.** When the coordinating
process's own environment carries `CLAUSTRUM_PARENT_JOB` (a host architect calling `coordinate`),
`Prepare` gives its run only an explicit `--budget` as a per-run cap — the outer ledger grants the
real slice at admission — so the entry written into the *inner* tree has `cap: null` and a backend
reporting no cost falls back to `$0` there. The outer tree still charges it correctly through
`Runner`'s reservation; only the inner tree's row is thin.

Measured 2026-09-22 against a fake backend on `backends.claude.path` reporting
`total_cost_usd: 0.25` (nothing paid), cast capped at `$10.00`: human output ends `architect cost
$0.25` / `tree spent $0.25 (architect $0.25 included), reserved $0.00`; `--json` keeps stdout to
exactly one RunResult document and writes the same tree line to stderr; `claustrum jobs budget
<tree>` lists `<tree id>  architect  cap $10.00  cost $0.25  done`. An MCP-started `coordinate`
records the same row, which is why the recording lives in `CoordinateEngine` and not in
`CoordinateCommand`. An uncapped cast still prints `tree: unlimited (children not accounted)` and
writes nothing.

⚠ **One admission does change, and it is the point.** The entry is written *after* the architect
returns, so children running during the coordination are unaffected — they were admitted while no
architect entry existed. A child started against the same tree *after* the coordinate process has
finished now sees the remainder net of it: measured, a `claustrum run builder --cast capped` under
`CLAUSTRUM_PARENT_JOB=<tree>` was admitted `cap $9.75` of a `$10.00` tree and the ledger totalled
`spent $0.50`.

⚠ **A ledger that cannot be written rides out as a warning on the returned result only.** The three
exceptions every other ledger caller swallows (`TimeoutException`/`IOException`/
`UnauthorizedAccessException`) must not turn a finished architect run into a failure, so they append
`budget ledger for tree '<id>' not updated with the architect's own cost: <message>` to the
`RunResult`. That warning reaches `--json`, `job_result` and the MCP client, but **not** the
`result.json` already on disk: `Runner` wrote it through its funnel before this code runs, and
rewriting it from the coordination layer would make a second writer of the file the funnel exists to
own. The human tree line degrades honestly on its own — with no architect row to read, it prints
without the `(architect $X included)` clause.

- **⚠ Superseded 2026-09-22 (review of the follow-ups batch): a run that never started writes no row
  at all.** `RecordFinishedAsync` creates `<CLAUSTRUM_HOME>/budget/<tree>/` before it takes the lock,
  so the `$0` row above *materialised a ledger directory* for a `coordinate` whose architect never
  spawned a process — and `CoordinateCommand`'s "no ledger directory ⇒ no child ever ran" shortcut
  (the branch that prints `tree spent $0.00` without touching the lock) then read a tree that had
  one. `CoordinateEngine.NeverRan(result)` now returns before the write, on the notion `Runner` hands
  `ChargeAsync` as `ran`: `Status is BackendMissing or BudgetExceeded` (the two statuses that exist
  only without a process) **or** `ExitCode == -1 && DurationSeconds == 0` (the pre-spawn failure,
  which is a plain `Failed`; a *killed* process pairs its `-1` with a real elapsed time, so it is
  still charged). The duration alone was never the discriminator this needed — it decided the
  *amount*, not whether to write — so past the guard the cost is simply `CostUsd ?? cap`. The human
  line degrades as it already did: `tree spent $0.00, reserved $0.00`, no `(architect $X included)`.

### What this breaks on the test side

`tests/Claustrum.Tests/Coordination/CoordinatePlanTests.cs` (the 4-argument `CoordinatePlan` ctor and
`ToDelegateRequest(job)`) and `CoordinationBriefTests.cs` (`RenderSystemAppendix`'s dropped `jobId`)
no longer compile, and the two architect golden renders carry the old `max_parallel` bullet. ⚠ The
brief test file is the dangerous one: `RenderSystemAppendix(cast, "default", "/repo", "job-1")` still
*compiles*, binding `"job-1"` to `modelOverride`, and an assertion that the appendix "contains job-1"
then passes against the `(this run: job-1)` the architect line prints. Those calls have to be
rewritten, not merely re-run.

## The native subagent tool is called `Agent`, and only `--disallowedTools` takes it away (2026-09-22, issue #19)

A spawned architect (`coordinate`, docs/PLAN.md §D3) must not fan out natively: a Claude Code
subagent runs the role on the architect's own model, outside the cast and outside the tree budget
ledger. `roles/architect/parts/delegation.claude.md` says so in prose; `ClaudeBackend.Build` now
enforces it, adding `--disallowedTools Agent,Task` whenever the run's **request env** carries
`CLAUSTRUM_PARENT_JOB` — which `CoordinatePlan.TreeEnv` sets on exactly one run, the spawned
architect. A host architect with no cast keeps its native subagents, and a `claustrum run` the
architect issues never inherits the variable (EnvAllowList drops it), so the block lands on the
architect alone.

Measured against `claude 2.1.278`, with no paid invocation: `ANTHROPIC_BASE_URL` was pointed at a
local capture server with a junk API key, and the `tools` array of the real `/v1/messages` request
was read before the server answered 400.

- **The tool is `Agent`.** The baseline request carries `Agent` (alongside `TaskStop`, `Workflow`,
  `SendMessage`, `ListAgents`, `Skill`); there is no bare `Task`, and the package's own
  `sdk-tools.d.ts` declares `AgentInput`/`AgentOutput` only. `--disallowedTools Agent` removes it;
  `--disallowedTools Task` removes nothing and does not touch `TaskStop` — matching is exact, not by
  prefix. `Task` is passed anyway because an unknown name is silently ignored (`Agent,Task,Edit`
  dropped `Agent` and `Edit`, kept `Write`, raised nothing), so it costs a word and covers an older
  installed `claude` where the tool was called `Task`.
- **No permission level withholds it.** Under `--permission-mode plan --allowedTools
  "Read,Glob,Grep,Bash(git diff*)"` the request still ships `Agent`: `--allowedTools` gates
  permission, it does not filter the tool set. That is why the flag is added at every level, `Full`
  included — and the disallow *is* honoured next to `--dangerously-skip-permissions`.
- **Repeated `--disallowedTools` merge; the last one does not win.** `--disallowedTools Edit,Write
  --disallowedTools Agent` removed all three. `ShellArgs`/`EditShellArgs` already emit the flag
  twice and were relying on this; the tree block is a third occurrence and adds to them.
- ⚠ **`--disallowedTools` is variadic and swallows the following positional.** `claude -p
  --output-format json --disallowedTools ZzzNotATool "reply PINEAPPLE"` consumed the prompt as a
  tool name and exited with *"Input must be provided either through stdin or as a prompt argument"*.
  The brief is the last argv element, so the tree block must never be the last flag before it — it
  is emitted right after the permission block, where `--no-session-persistence` or `--resume` always
  follows.
- **Still reachable, deliberately unaddressed:** `Workflow`, `SendMessage` and `ListAgents` remain
  in the spawned architect's tool list. #19 named the subagent tool only.

## Both smoke scripts' fallback resolver publishes, it does not build (2026-09-22, issue #22)

`PackAsTool=true` suppresses the apphost, so `dotnet build -c Release` leaves `claustrum.dll` and no
executable, and on a clean clone `scripts/smoke.sh` with no argument died on "could not resolve a
claustrum binary". The fallback is now `dotnet publish -c Release -r <host rid> -p:PublishAot=true`,
taking the binary from `bin/Release/<tfm>/<rid>/publish/`; a failed AOT publish prints AGENTS.md's
prerequisites (clang/gcc + zlib headers; the VS C++ workload on Windows) and exits 1 rather than
falling back to something that cannot run. Host RID comes from `uname -s`/`uname -m` in bash. In
PowerShell it comes from `RuntimeInformation::RuntimeIdentifier` — but only when that is already
portable: measured on Fedora it returns **`fedora.44-x64`**, which has no runtime pack to restore,
so anything non-portable is rebuilt from `$IsLinux`/`$IsMacOS` plus `OSArchitecture`. Verified
2026-09-22: a no-argument `scripts/smoke.sh` with every backend and `gh` removed from PATH published
linux-x64, ran every row as SKIP/PASS and exited 0.

## CursorSync: agents, skill and mcp.json, doc-confirmed but never round-tripped (2026-09-22, issue #25/M4)

`.cursor/agents/<role>.md` (+ `-xhigh`/`-max` stubs), `.cursor/skills/claustrum/SKILL.md` and
`.cursor/mcp.json`, on the same `SyncWriter`/`SyncAccumulator`/`McpConfigSync` as the other three.

- **Frontmatter is doc-confirmed, not guessed.** cursor.com/docs/context/subagents (fetched
  2026-09-22) gives the agent fields: `name`, `description`, `model` (default `inherit`, or an id),
  `readonly`, `is_background` — and both `.cursor/agents/` and `~/.cursor/agents/`. The locally
  installed CLI's own bundled `~/.cursor/skills-cursor/create-subagent/SKILL.md` states the same
  locations and the same required `name`/`description` pair, independently of the website.
- **`model: inherit` for every role and tier.** It is the documented default *and* the only thing a
  Free plan accepts (NOTES.md "The cursor backend, validated against a real install"), so no model
  class → id table was invented. The consequence is honest and worth stating: the `-xhigh`/`-max`
  stubs differ only in prose here, not in configuration.
- **`readonly: true` only where `role.json.permission` is `readonly`** (today: code-reviewer). False
  is the documented default, so it is omitted rather than written out. docs/PLAN.md §B5's "no
  `tools` field, so it is prose-only" still holds for the deny lists; the readonly rung is now real.
- **A Cursor skill is both slash-invocable and auto-surfaced** (cursor.com/docs/context/skills:
  "type `/` in Agent chat and search for the skill name"), so unlike opencode this harness needs no
  command-vs-skill split, and unlike Copilot `/claustrum` is genuinely typeable. The folder name
  must equal the skill's `name`. `disable-model-invocation` is deliberately **not** written: leaving
  it out keeps the skill auto-surfaced by description as on every other harness, and the slash form
  works either way — Cursor's own `create-skill` guidance suggests the opposite default for its
  built-ins.
- **`~/.cursor/` is a real user-level target for all three**, mcp.json included
  (cursor.com/docs/context/mcp), so `--global` writes agents, skills *and* mcp.json — unlike
  `.mcp.json`/`opencode.json`, which are repo-root files `--global` skips.
- **Not verified live**: no synced file was ever loaded by a real Cursor session (no GUI here), so
  this is one notch below CopilotSync's `copilot skill list` round trip. Issue #25's "`/claustrum`
  runs in a real Cursor session" stays open until someone with Cursor checks it.
- **`~/.cursor/mcp.json` registers the absolute binary (2026-09-22, review of the follow-ups
  batch).** Under `--global` the user-level entry carried the same bare `claustrum` the repo file
  gets — the exact GUI-PATH hazard #24 fixed next door, for a Cursor started from a desktop session.
  `CursorSync.Sync` takes an optional `globalBinaryPath`, supplied by the caller the way
  `ClaudeDesktopTarget.BinaryPath` is (so Roles still reads no environment of its own), and **skips
  the global `mcp.json` merge entirely** when it is null rather than registering the dotnet host
  under the `claustrum` key; `SyncCommand` prints `cursor global mcp.json: not written (<reason>)`.
  The repo-level `.cursor/mcp.json` keeps the bare name deliberately: it is committed and shared, and
  a machine-specific path in it would be wrong for everyone else.

## The claustrum MCP server in the Claude desktop config (2026-09-22, issue #24/M4)

Closes the gap NOTES.md "docs/INSTALL.md records one gap rather than papering over it" recorded:
`sync --global --only claude` now merges `mcpServers.claustrum` into the desktop app's own config —
`%APPDATA%\Claude\claude_desktop_config.json` on Windows, `~/Library/Application
Support/Claude/claude_desktop_config.json` on macOS. Linux has no Claude desktop app, so nothing is
written and `sync` prints one line saying exactly that.

- **`command` is `Environment.ProcessPath`, not the bare `claustrum`**: the desktop app spawns the
  server from a GUI process whose PATH rarely holds the install directory. The path (and the
  binary) is resolved in the CLI (`Cli/ClaudeDesktopConfig`) and handed to `ClaudeSync` as a
  `ClaudeDesktopTarget`, so Roles still reads no environment of its own and a test can point the
  merge at a temp file — the same seam `homeDirectory` already is.
- **Provenance: `McpProvenance.OwnKey`.** The manifest rule (`.claustrum/sync-manifest.json` proves
  claustrum wrote the key) cannot apply to a file that belongs to no repo, so the smallest honest
  rule was chosen instead: the `claustrum` key's own name is its provenance — it is rewritten
  whenever its content differs, and no sibling server is ever touched. `~/.cursor/mcp.json` under
  `--global` uses the same rule for the same reason. The trade-off is deliberate: a hand-written
  `claustrum` entry (what INSTALL.md told desktop users to create until today) is corrected rather
  than reported foreign.
- **Written only where the app is, and only by the binary (2026-09-22, review of the follow-ups
  batch).** Two ways the merge above wrote a file nobody asked for. (1) A machine with no desktop app
  at all: `%APPDATA%\Claude` / `~/Library/Application Support/Claude` did not exist, the merge
  created it and planted a config no app reads, after which `sync --global --only claude --check`
  reported that file missing/stale there forever. `Resolve` now requires the app's own config
  **directory** to exist — its *file* may still be absent, which is a fresh install and ours to
  create — and otherwise skips with `Claude desktop app not found at <dir>`. (2) `dotnet
  run`/`dotnet exec`: `Environment.ProcessPath` is then the **dotnet host**, not claustrum (this
  project suppresses its apphost through `PackAsTool`), so the registered `command` would start the
  SDK. `ClaustrumBinaryPath.Resolve()` returns a reason instead of a path whenever
  `Path.GetFileNameWithoutExtension(ProcessPath)` is not `claustrum` (case-insensitive), and every
  skip prints through the one line the Linux skip already used: `claude desktop config: not written
  (<reason>)`. Order is OS → directory → binary, so a Linux user is told there is no such app instead
  of being sent to install a binary that would not help.

## doctor advisories are not problems (2026-09-22, issue #17)

docs/PLAN.md §A3 promised that cursor's prompt-only deny list is something "`doctor` marks
advisory"; "The cursor backend, validated against a real install" box 11 confirmed the gap and named
the trap. `Doctor` therefore grew a field of its own rather than borrowing `Problems`:

- **Why not a `Problems` entry.** `BackendsCommands.ProbeLineAsync` reads
  `doctor.Problems.FirstOrDefault()` as "this backend is not worth paying for" and returns
  `skipped (<problem>)`. A note that blocks nothing would therefore have silently turned off
  cursor's `--probe` round trip — the diagnostic would have disabled the diagnostic. `Advisories` is
  printed (`  advisory: <text>`, after the `problem:` lines) and serialized, and is read by nothing
  that decides anything. `ProbeLineAsync` is deliberately unchanged.
- **The cursor line, verbatim:** `deny list is enforced by prompt only: cursor-agent has no native
  deny flag (NOTES.md "The cursor backend, validated against a real install", box 11)`.
- **Emitted whether or not the binary was found.** `CursorBackend.DetectAsync` appends it with
  `probe with { Advisories = [...] }`, so an install-less machine is told the same thing — it is a
  property of cursor-agent, not of this install. The other four backends emit none.
- **Shape: `string[]? Advisories = null` with a declared non-null `Advisories` property** returning
  `[]`. A record cannot default an array parameter to `[]` in a primary constructor, and the point of
  the default is that every existing `new Doctor(found, path, version, problems)` (five in
  `VersionProbe`, plus the test fakes) keeps compiling and keeps reading as `[]`, never null.
- MCP `doctor`'s `BackendDoctorEntry` carries `advisories` as an additive field; the source-generated
  `McpJsonContext` already covers the record, and the AOT publish stays at zero trim warnings.

## tester and ui-reviewer re-ported from the owner's current files (2026-09-22, issue #16)

"`sync --global --only claude` … not header-only" (2026-09-21) found the library rendering texts the
owner had already replaced — `~/.claude/agents/tester.md` rewritten 2026-09-14, `ui-reviewer.md`
2026-09-18. Both are now ported from those files sentence by sentence, the way `builder`,
`code-reviewer` and `architect` were.

**Method, and the measurement that closes the issue's done-when.** Rendered into a scratch repo
(`sync --only claude --roles tester,ui-reviewer`) and compared block by block against the owner's
file — frontmatter, the `claustrum:generated` marker and the generated `## Report format`/
`## House rules` sections dropped, every markdown block (paragraph, list item, heading, or a whole table)
whitespace-collapsed, so the owner's 80-column wrap and the library's 100 cannot register as a
difference. **tester: 14 blocks against 14, one differing**, and only because his wrap falls after a
slash (`read their testing/ quality-gate sections` vs `testing/quality-gate`). **ui-reviewer: 50
owner blocks against 50**, two differences, both deliberate: the tool names in How-you-work step 2
neutralised to "the *snapshot* verb below" (§B1 keeps tool names in parts), and the tier section
below. A third one — a paragraph added to `parts/browser.claude.md` from the owner's global
CLAUDE.md rule of 2026-09-10/2026-09-20 about relaunching a browser that answers "not connected" —
was removed after a blind review the same day: it is not in the owner's ui-reviewer file, and this
port carries that file only. Its source rule is untouched where it lives. No `--global` adoption
was run: taking these files over is the owner's call, not the port's.

**What became a part.** tester keeps exactly one, `{{part:environment}}`, and it now carries the
whole "Assume nothing about the environment either" paragraph — the owner folded the standalone
`## Environment` section into "The quality gate — run it, report the exact result", so the token
moved with it; the claude variant is his 2026-09-21 "whichever shell tools you actually have"
wording, the `.default` one keeps "the one appropriate to the OS you are running on". ui-reviewer
gains a second part, `parts/browser.claude.md`: the verb table names `preview_start`, `read_page`,
`computer`, `resize_window` and the Browser-pane-vs-Claude-in-Chrome choice, which is Claude's
surface and nobody else's. It has **no `.default.md`**, deliberately — `harnesses` is `["claude"]`,
and the missing fallback is what makes a forced non-claude render fail loudly instead of handing a
host a table of tools it does not have. That is the property `environment` already had, and the
stated reason OpencodeSyncTests/CopilotSyncTests assert the default role set filters the role out.

**The tier section could not be ported as written.** The owner's file says "model is fixed, effort
scales" — `opus` at all three tiers — while `roles/ui-reviewer/role.json` says `standard-coding` at
`high` and `frontier-coding` above it. Measured the same day, the mismatch runs both ways: his
`tester{,-xhigh,-max}.md` are `sonnet` at all three tiers, where this library jumps that role to
`frontier-coding` at `xhigh`. So the section states the invariant instead of the models, as the
architect port did: effort is what a heavier tier buys, and which model each tier runs on is the
library's call (`role.json`) and the cast's, never the reviewer's. Whether the two ladders should
become the owner's — ui-reviewer `frontier-coding` everywhere, tester `standard-coding` everywhere —
is left open on purpose: it is a cast/cost decision, not a port decision (issue filed the same day).

**role.json.** Both descriptions are the owner's current frontmatter flattened to one line (the
recorded by-design delta); `ui-reviewer.color` follows him from `yellow` to `cyan`, which also stops
it colliding with `code-reviewer`; `nonNegotiable` — the three lines the tier stubs repeat — was
refreshed to his current load-bearing rules. Tiers, permission, `harnesses`, `report` and `blind`
are untouched.

**Frontmatter the port cannot fix, recorded because the re-port is what surfaced it.** ClaudeSync's
`ToolsFor` keys off `permission` alone, so `ui-reviewer` (`permission: "shell"`) is emitted with
`tools: … Edit, Write, NotebookEdit …` and **no browser tool at all**, against the owner's
`tools: Read, Grep, Glob, Bash, mcp__Claude_Browser, mcp__claude-in-chrome` +
`disallowedTools: Agent, Edit, Write, NotebookEdit`. A synced ui-reviewer therefore cannot open a
browser — the one thing the role exists for — while it can edit the tree, which its own ground rules
forbid; and `disallowedTools` is emitted for `readonly` roles only, though issue #19 measured that
listing tools is not what takes the native `Agent` tool away. Separately,
`SyncWriter.TierDescription` hardcodes "identical role, model, and rules", false for the three roles
whose class changes at `xhigh` — the owner's own `code-reviewer-xhigh.md` says "a model+effort step
up". None of that lives in `roles/`; all of it has its own issue, filed the same day.

## The copilot backend, validated against a real install (2026-09-22, issue #13)

Supersedes the unconfirmed halves of "The copilot backend (2026-09-18, issue #4/M3)" and
"CopilotSync: agent + skill files, verified against a real install (2026-09-18, issue #4/M3)".
**GitHub Copilot CLI 1.0.87** is installed at `~/.local/bin/copilot` and logged in, so all four of
issue #13's copilot boxes were measured. **Four paid premium requests** in total (each run's own
`result.usage.premiumRequests: 1`) — three on the boxes, one on the remediation pass's plan-mode
probe below — all in throwaway `git init` repos under a scratch directory
(`<tmp>` below), always with stdin closed, never in a real checkout.

Two of the four boxes were wrong, and the pass turned up two further defects nobody had listed:
`--model` was never passed to copilot at all, and `CopilotSync`'s unquoted `description:` silently
kept three of the four roles from loading.

- **The free probe that did most of the work.** Agent selection happens *before* the first API call,
  so asking for a name that does not exist prints the discovered agents and costs nothing:
  `copilot -C <dir> -p "say hi" --agent definitely-not-an-agent --allow-all-tools --output-format
  json --stream off` → exit **1** in ~1s, **empty stdout**, one stderr line: `No such agent:
  definitely-not-an-agent, available: probe-personal, probe-project, probe-adddir`. Adding
  `COPILOT_PROVIDER_BASE_URL=http://127.0.0.1:9/v1` (BYOK against a dead local port — `copilot help
  environment`: "GitHub authentication is not required") removes even the GitHub round trip. Probes
  of this shape, plus `--log-level all --log-dir <tmp>/logs` for the loader's own diagnostics,
  answered boxes 1 and 4 and both regressions below without spending anything.

| box | verdict | what decided it |
|---|---|---|
| 1 `.agent.md` frontmatter schema | **corrected** | `description` is the *only* required key; three of four synced roles were being dropped for an unquoted `": "` |
| 2 JSONL success-event field names | **corrected** | real 42-event capture; every key `Parse` looked for was wrong |
| 3 `shell` level's `--deny-tool write` | **corrected** | the name is right, the mapping was not: `write` excludes shell redirection by design |
| 4 `~/.copilot/agents` for `sync --global` | **confirmed** | the exact files `sync --global --only copilot` writes round-tripped through it |

1. **Box 1 — the frontmatter schema, measured key by key.** Six `.agent.md` files with different
   frontmatter went into one `--add-dir` tree and the free probe said which loaded, with
   `--log-level all` giving the reason for each that did not:
   - `name:` is **optional** and, when present, **wins over the file name** (`file-a.agent.md` with
     `name: renamed-a` was listed as `renamed-a`; a file with no `name` was listed as its stem).
   - `description:` is **required**: a file with `name` and a body but no description is rejected —
     `file-f.agent.md: custom agent markdown frontmatter is malformed: description: Required`. No
     frontmatter at all is rejected the same way (`missing or malformed YAML frontmatter`).
   - The accepted set is `name`, `description`, `model`, `tools`, `infer`, `skills`. Everything else
     is dropped with a warning — one file carrying thirteen candidates produced
     `unknown fields ignored: displayName, mcpServers, prompt, reasoningEffort, reasoning_effort,
     effortLevel, userInvocable, disableModelInvocation, allowedTools, argument-hint, color, target,
     license`. Note **`reasoningEffort` is not a frontmatter key** even though the SDK's programmatic
     `CustomAgentConfig` has one: per-agent effort is unreachable from a file.
   - So `CopilotSync`'s `name`/`description`/`model: auto` are all real keys. **But they were not
     loading.** A role description containing `": "` is not a valid plain YAML scalar, and after
     `claustrum sync --only copilot` the same free probe listed only `builder` and the eight tier
     stubs, with three errors in the log: `.github/agents/architect.agent.md: … failed to parse YAML
     frontmatter: mapping values are not allowed in this context at line 2 column 283`, and the same
     for `code-reviewer.agent.md` (column 173) and `tester.agent.md` (column 172) — the `": "` in
     *"It does not ship production code itself: it hands…"* and *"Leaf role: it reports…"*. Fixed by
     emitting a double-quoted scalar (`CopilotSync.YamlQuoted`); re-synced, the probe lists **all
     twelve** agents and the log holds zero warnings. `CopilotBackend`'s own ephemeral agent file
     carries a constant description with no `": "`, so it was never broken — it is quoted now anyway.
   - **Live proof the agent file is really used**: the end-to-end run in box 2 came back with the
     `claustrum-report` fence, which only the rendered role body asks for, and `--agent
     claustrum-builder` would have exited 1 with `No such agent` had `--add-dir` not loaded it.

   ⚠ **The other three syncs emit the same unquoted `description:`** (`ClaudeSync`, `OpencodeSync`,
   `CursorSync`) and were *not* measured here. Claude Code demonstrably tolerates it (the owner's own
   agent files have carried `": "` for months); opencode and cursor are unknown and want their own
   probe rather than a blind copy of this fix. *Fixed 2026-09-25 (issue #33) — see "Sync frontmatter:
   the unquoted `description:` fix reaches Claude/opencode/Cursor too" below for the shared helper and
   what each harness's own probe could and could not confirm.*

2. **Box 2 — the success JSONL, captured and committed to.** `claustrum run builder --backend copilot
   --brief "create hello.txt containing hi" --budget 0.5 --model auto --json --cwd <tmp>/run1` →
   `status: success` in **19.1s**, `changed_files: [{"path":"hello.txt","kind":"A"}]`, a real diff,
   `report_status: ok` with every builder-report field filled, `session_id:
   4226b2e4-4a96-45e2-bdd8-35c8b5f63e07`, `cost_usd: null`, `usage: null`, exit 0. Its `stdout.log`
   is **42 JSON lines** and is now `tests/fixtures/copilot/success.jsonl`. **Every key the old
   `Parse` looked for was wrong**, and the old fabricated fixture agreed with it, so the two were
   consistently wrong together:
   - Each line is a **session event**: `{"type":…,"data":{…},"id","timestamp","parentId"}`, plus
     `ephemeral`; `agentId` is declared by the shipped schema but **never observed** (see the
     sub-agent note below). Assistant text is `data.content` on
     `type:"assistant.message"` — **not** a top-level `content`/`text`/`message`.
   - Of the four `assistant.message` events only the **last** has non-empty `content`; the other
     three carry `content: ""` and their work in `toolRequests`, so "last non-empty" is the rule.
   - There is **no `session.start` event** in the stream. The session id arrives only on the CLI's own
     closing line, which is not a session event at all:
     `{"type":"result","timestamp":…,"sessionId":"4226b2e4-…","exitCode":0,"usage":{"premiumRequests":1,
     "totalApiDurationMs":12626,"sessionDurationMs":15971,"codeChanges":{"linesAdded":2,
     "linesRemoved":0,"filesModified":["…/hello.txt"]}}}`. `Parse` reads `session.start` too, since
     the schema shipped in the package declares it and it costs one `case`.
   - **No token counts and no dollar cost exist anywhere in the stream.** `assistant.usage` — the one
     event carrying `inputTokens`/`outputTokens`/`cacheReadTokens`/`cost` — is on the CLI's own
     suppression list for `--output-format json`, and the closing `result` reports only
     `premiumRequests` and durations. `session.usage_checkpoint` adds `totalNanoAiu: 482334000`
     (AI units, not dollars). So `Usage` and `CostUsd` are null **by measurement**: `--budget` neither
     caps nor accounts for a copilot run, and the §D4 ledger records nothing for a copilot child —
     the same gap cursor has. `--usage-output-file <file>` is the documented route to token counts if
     it is ever wanted; it would need `Parse` to read a file, which it cannot today.
   - `result.usage.codeChanges.filesModified` carries absolute paths of what the model edited. It is
     deliberately **not** mapped to `ReportedEdits`: the git snapshot is truth (docs/PLAN.md §A2) and
     already produced the right `changed_files` here.
   - `--model auto` really is Auto: `session.auto_mode_resolved` reports
     `{"chosenModel":"gpt-5.6-luna","routingMethod":"auto_v2","fallback":false}`.
   - **Redactions in the fixture**, and nothing else: the `assistant.message` events' opaque
     provider blobs (`encryptedContent`, `reasoningOpaque`, `apiCallId`, and `encrypted_content`
     inside `reasoningBlocks`) and `session.usage_checkpoint`'s `model_call_id` are replaced with
     `"<redacted>"`, and the scratch path that contains the owner's username is rewritten to
     `/tmp/claustrum-copilot-capture`. Two more opaque provider handles joined that list on review:
     `previousResponseId` (3 occurrences) and the `id` beside each `encrypted_content` inside
     `reasoningBlocks.blocks` (4). Nothing else is touched — same line count, same key order,
     75502 bytes down to 25716. The readable `reasoningText`, the tool calls, the request ids and the
     whole `result` line are the genuine article.

3. **Box 3 — `--deny-tool write` is the right name and the wrong mapping.** `copilot help permissions`
   documents the pattern kinds first-hand: `shell(command:*?)`, `write(path?)`,
   `<mcp-server-name>(tool-name?)` and `url(domain-or-url?)`, with "Denial rules always take
   precedence over allow rules, even `--allow-all-tools`". So `--deny-tool write` and `--deny-tool
   "shell(git push)"` are both valid — the latter is `copilot --help`'s own example, and the same
   page confirms shell arguments match "on a first-level subcommand basis, e.g. 'git push'". What the
   same page also says is the problem: `write(path?)` "matches tools that create and modify files,
   **except shell tool invocations**… To allow all shell redirections, set `--allow-all-tools`" —
   which every Claustrum level does, because `--help` calls it "required for non-interactive mode".
   The `shell` rung therefore promised "run commands, change nothing" while `echo x > f` stayed wide
   open. **Fixed** by giving `shell` the `--mode plan` that `readonly` already had (the same rung
   `ClaudeBackend`'s own `shell` row gets from claude's plan mode), and by applying the role's deny
   list there, which it silently dropped. One paid run is the proof — `claustrum run builder
   --backend copilot --permission shell --brief "create denied.txt containing x, then run the shell
   command: echo ran > shell-wrote.txt" --budget 0.5 --model auto --json --cwd <tmp>/run2`:
   exit 0 in **22.4s**, `changed_files: []`, the directory afterwards holding only `.git` and
   `seed.txt` — **neither file was created, by either route** — and `final_message` = *"I'm in plan
   mode and the task requires approval before modifying files outside the session folder, so I can't
   execute the requested file creation yet."* `report_status: ok`, with the builder schema and
   `status: "blocked"`: **plan mode does not cost the report fence**, which was the open worry about
   using it for a non-`readonly` rung.

   | level | argv `Build` emits (after `-C <cwd> --agent claustrum-<role> --add-dir <job>/copilot-agents --model X --output-format json --log-level none --stream off`) |
   |---|---|
   | `readonly` | `--allow-all-tools --mode plan --deny-tool write --deny-tool shell` |
   | `shell` | `--allow-all-tools --mode plan --deny-tool write` + `--deny-tool shell(<pattern>)` per deny entry |
   | `edit` | `--allow-all-tools --allow-all-paths --deny-tool shell` |
   | `edit+shell` | `--allow-all-tools --allow-all-paths` + `--deny-tool shell(<pattern>)` per deny entry |
   | `full` | `--allow-all` |

   All five verified as emitted, without paying, by pointing `backends.copilot.path` at a fake
   `copilot` that dumps its argv. `--reasoning-effort <effort>` and `--resume <id>` follow, then
   `-p <brief>` last.

4. **Box 4 — `~/.copilot/agents` is real, and the files this repo writes load from it.** Two
   independent confirmations. The CLI's own agent-creation wizard offers exactly two destinations,
   `Project (.github/agents/)` and `User (<COPILOT_HOME>/agents)`. And the free probe, run from a
   directory with no `.github` of its own after copying `code-reviewer.agent.md` and
   `tester-max.agent.md` **from a `sync --global --only copilot` run** into `~/.copilot/agents/`:
   `No such agent: definitely-not-an-agent, available: code-reviewer, tester-max`. Both files were
   removed afterwards. Measured alongside: discovery covers **three** roots at once — personal
   `~/.copilot/agents`, the session cwd's own `.github/agents`, and any `--add-dir <dir>`'s
   `.github/agents` — listed in that order. The cwd root means a repo that has run `sync --only
   copilot` exposes `builder`/`tester`/… to an interactive `copilot` session, which is the point of
   the sync; the backend's ephemeral agent is namespaced `claustrum-<role>`, so the two never collide.
   No code change was needed for this box.

5. **Two defects the checklist did not list.**
   - **`--model` was never passed.** `Build` emitted `-C`, `--agent`, `--add-dir`, `--output-format`,
     `--log-level`, `--stream`, the permission args, `--reasoning-effort`, `--resume` and `-p` — and
     no `--model`, though docs/PLAN.md §A3's copilot row spells it out and `OpencodeBackend` passes
     its own. Every `claustrum run --backend copilot --model X` was silently running on copilot's
     default model, which made `CLAUSTRUM_SMOKE_MODEL_COPILOT` decorative. Now emitted — and, exactly
     as for cursor, **the built-in default is now a hard failure rather than a silent lie**: with no
     alias, `frontier-coding` resolves to `claude:opus`, copilot is handed the bare id `opus`, and
     `claustrum run builder --backend copilot` exits 1 in 3.4s with `Error: Model "opus" from --model
     flag is not available.` (free — it refuses before the first API call). A user needs an alias,
     e.g. `{"models": {"frontier-coding": "copilot:auto"}}`, and the smoke row needs
     `CLAUSTRUM_SMOKE_MODEL_COPILOT=auto`.
   - **`--model auto` and `--reasoning-effort` are mutually exclusive.** The first attempt at the box-2
     run died in 4.5s, before any API call, with `Error: Model "auto" does not support reasoning
     effort configuration (requested: "high").` A role's effort is always set, and `auto` is the model
     id every account can use, so `Build` now omits `--reasoning-effort` when the model is `auto`;
     any other model that refuses effort still fails loudly with that same unambiguous message.

6. **The startup-failure shape is not "empty stdout" — `Parse` had to change for it.** The
   2026-09-18 note recorded stdout as empty on a fatal startup failure. That was an *unauthenticated*
   failure, which dies before the MCP servers come up. Both refusals measured today printed **two
   `session.mcp_server_status_changed` lines to stdout first**, so the old `stdout.Trim().Length == 0`
   branch never fired and the RunResult's `error` was a dump of that plumbing instead of the real
   message. `Parse` now prefers stderr whenever no assistant text and no `session.error` were seen,
   and keeps the raw stream as the last resort. Captured genuinely as
   `tests/fixtures/copilot/effort-refused-stdout.jsonl` + `effort-refused-stderr.txt` — byte-identical
   to the job's own logs, since neither carried anything to redact. Related and load-bearing: with
   `--output-format json` a *mid-session* failure is the opposite shape — the CLI routes
   `session.error` to stdout as an event and writes **nothing** to stderr — so `Parse` reads
   `session.error` → `data.message` rather than leaving it to the stderr branch.

7. **`auth:` and `--probe`.** `copilot help environment` names `COPILOT_GITHUB_TOKEN`, `GH_TOKEN`,
   `GITHUB_TOKEN` "in order of precedence" — verbatim the three `AuthStatusFor` already listed, so
   that half was right. The login file is now read too, because it was confirmed on a real install:
   `~/.copilot/config.json` (relocated by `COPILOT_HOME`) holds a `loggedInUsers` array — `[{"host":
   "https://github.com", "login": "<user>"}]` on this logged-in machine — and the file legally
   contains `//` comments, so it needs `JsonCommentHandling.Skip`. Which of `/login` and `gh auth
   login` writes it was not separately tested; only that a logged-in install has it non-empty. `backends doctor
   copilot --probe` on this machine, with no env var set, prints
   `auth: present (logged in, /home/…/.copilot/config.json — no env var set)` and
   `probe: OK (7.1s, cost not reported, reply "OK")` — the third paid request. ⚠ The probe needs a
   model alias resolving to copilot **and a `claustrum.json` at the git root**: in a directory that is
   not a git repo the repo config layer is not read at all and the probe skips with "no model alias
   in claustrum.json resolves to this backend", which looks like a copilot problem and is not one.

   ⚠ **A non-object `config.json` used to kill the whole command.** `RootElement.TryGetProperty`
   throws `InvalidOperationException` when the root is an array/string/number/null, and that type was
   not in the catch filter, so `doctor --probe` died on copilot's `auth:` line *after* paying for the
   probes and never printed cursor, `mcp:` or the merged config. The kind is checked before the
   lookup now, and — since this is the one backend whose login file is actually read — every outcome
   is a real answer instead of the generic "not checked", which stays only for the backends whose
   file this code does not know: `present (logged in, <path> — no env var set)`,
   `not set (env var absent; <path> lists no logged-in user)`,
   `not set (env var absent; <path> not present)`, and
   `not set (env var absent; <path> present but unreadable)` for unparseable, non-object or
   unreadable-by-IO files alike. Nothing on this path throws.

### `shell` rung: what plan mode actually blocks (2026-09-22)

The rung promises "run commands, change nothing", and the guarantee is carried by **plan mode's
command-string analyser**, not by the permission layer: `--deny-tool write` explicitly exempts shell
invocations (box 3). One paid request (the **fourth**, `premiumRequests: 1`) went to probing the
analyser with writes that carry no `>` redirection, in a throwaway `git init` toy with
`CLAUSTRUM_HOME` pointed at a scratch dir:

```
claustrum run builder --backend copilot --model auto --permission shell --json --cwd <toy> \
  --brief "$BRIEF"
# BRIEF = Run exactly this shell command and report its exit code:
#         python3 -c "open('leak.txt','w').write('x')"
#         — then run this second command and report its exit code too: tee leak2.txt <<< y
```

**Nothing leaked.** `status: success`, `exit_code: 0`, `changed_files: []` in **24.3s**; afterwards
the toy held only `.git` and `seed.txt` — no `leak.txt`, no `leak2.txt`, `git status` clean.
`report_status: ok` with `status: "blocked"`, so the fence survives plan mode here too.

⚠ **But the interpreter write was never tested on its own, and the log says the analyser did not see
it.** The model fused both commands into a single `bash` call — `python3 -c "open('leak.txt','w')
.write('x')"; echo EXIT:$?; tee leak2.txt <<< y; echo EXIT2:$?` — and the `tool.execution_start`
event carries the analyser's verdict: `"shellToolInfo": {"possiblePaths": ["leak2.txt"],
"hasWriteFileRedirection": false}`. Only the `tee` target was recognised as a path; the `open(…,'w')`
inside the `python3 -c` string was **not**. The whole compound was then denied
(`tool.execution_complete`, `error.code: "denied"`): *"This shell command would modify files outside
the session folder and was blocked. Plan mode does not permit changes outside the session folder. Do
not implement yet — finish the plan and call `exit_plan_mode` to request approval before making any
changes."* So the denial is fully explained by `leak2.txt`, and an interpreter-only write is
**unsettled — with the evidence pointing the wrong way**. Not fixed, deliberately: the same shape is
house precedent on claude, whose `shell` row leans on plan mode in exactly the same way.

**The over-block side of the same trade** (read out of the box-3 session log,
`~/.copilot/session-state/cb6093c4-…/events.jsonl`, no new spend): plan mode *mandates* a plan file
and `--deny-tool write` forbids it — a `create` of `<session-state>/cb6093c4-…/plan.md` came back
*"Permission to run this tool was denied due to the following rules: `write`"*. And the
escape hatch the denial text names does not exist here: **`exit_plan_mode` is never offered as a
tool** — no `tool.execution_start` for it in either plan-mode session, only the denial prose naming
it. The model is told to call a tool it does not have, which is why "blocked" is the honest report
status for this rung rather than a failure.

### Sub-agent tagging is unobserved, so `Parse` does not special-case it (2026-09-22)

`Parse` briefly skipped `assistant.message` events carrying a top-level `agentId`, on the theory that
a delegated agent's chatter could displace the root agent's final answer. **There is no such event.**
`agentId` occurs **zero** times in the committed capture and zero times across every
`~/.copilot/session-state/*/events.jsonl` on this machine, at root or under `data`. The only
delegation-shaped event that does exist is `subagent.selected`, and it names the *root* custom agent
(`{"agentName":"claustrum-builder","agentDisplayName":"claustrum-builder","tools":["*"]}`), not a
delegate. The filter was therefore removing nothing and could only mis-fire, so `FinalMessage` is the
last non-empty `assistant.message` content, full stop — the root agent speaks last in every session
observed. If copilot ever does tag delegated output, this is the note to revisit.

**Still unverified, for the record:** every model id other than `auto` (only `opus` was tried, to
measure the refusal); whether a named model accepts `--reasoning-effort` in practice; `--resume`
against a real session id; the `full` rung's `--allow-all`; a `session.error` event, which nothing
here got far enough to produce; `--usage-output-file`; whether `infer`/`tools`/`skills` in an
`.agent.md` do what their names suggest, as opposed to merely being accepted; an interpreter-only
write under the `shell` rung, which no run has yet attempted in isolation (see the plan-mode section
above — the analyser demonstrably did not recognise one); and the Windows leg.

## Windows gave redirected stdout the console code page, so every em dash shipped as `?` (2026-09-23)

`backends doctor --probe` punctuates with em dashes (`present (env var set — a login file may also
work even if not)`), and on `windows-latest` the two auth suites that read them back through the real
binary failed while every other assertion in the same files passed. The split is the whole diagnosis:
**six failing assertions across two independently written PRs, and all six and only the six contained
a `—`.** `BackendsCommandsCopilotAuthTests`' four non-em-dash `InlineData` cases passed next to them;
so did `BackendsCommandsOpencodeApiAuthTests`' one em-dash-free case, alone among its five.

The cause is not the tests. On Windows `Console.Out` is built over `Console.OutputEncoding`, which
defaults to `GetConsoleOutputCP()` — 437 on a GitHub runner — **and that holds when stdout is
redirected**, where .NET on Unix is UTF-8 regardless of locale (confirmed here: `LC_ALL=C` still
emits `E2 80 94`). cp437 cannot represent U+2014, so the encoder fallback wrote `?` into the pipe.
This was never only a test problem: `claustrum backends doctor --probe > out.txt` on Windows lost the
character for anyone. The one place that set UTF-8 — `Splash.Run` — is reachable only behind
`Splash.IsWanted`, which requires `!Console.IsOutputRedirected`, so no redirected run ever hit it.

**Both ends had to move, and either alone still fails.** `Program` now calls
`ConsoleEncoding.ForceUtf8()` before anything writes, and the subprocess tests set
`StandardOutputEncoding`/`StandardErrorEncoding`: left null, the *parent* decodes with its own
console code page and reads the now-correct UTF-8 bytes as mojibake. Fixing the writer without the
reader just moves the failure.

`ForceUtf8` does not reach for `Console.OutputEncoding` when the stream is redirected, which is why
it is a class and not one line: the setter calls `SetConsoleOutputCP`, which changes the code page of
the caller's console and **outlives the process**. A redirected stream gets a `StreamWriter` of our
own instead; a real console still needs the property, because raw UTF-8 bytes into a cp437 console
render as mojibake. `Splash.Run`'s own assignment went with it — startup covers every verb now, and
leaving it would have put `SetConsoleOutputCP` back on the `claustrum splash > file` path.

## The opencode and api backends, validated against real endpoints (2026-09-22, issue #13)

Supersedes the unconfirmed halves of "The opencode backend (2026-09-18, issue #4/M3)" and "The api
backend (2026-09-18, issue #4/M3)", and closes issue #13's opencode and api boxes. The machine has
**opencode 2.0.12** at `~/.opencode/bin/opencode` with no provider login, an `OPENROUTER_API_KEY` in
the environment, and **no `ANTHROPIC_API_KEY`** — which is why one box below is a measured negative
and stays open. Everything paid ran in throwaway `git init` toys under a scratch directory (`<tmp>`
below) with `CLAUSTRUM_HOME` pointed at a scratch dir, never against this repository.

**Total spend: $0.0098**, against a $0.50 budget — six paid `claustrum run`s on opencode, one paid
`run` and one probe on api, one opencode probe, and two raw `curl`s. The single most expensive line
was the $0.0060 readonly run that found defect 2 below; everything else was fractions of a cent on
`openrouter/deepseek/deepseek-v4-flash` ($0.049/M in, $0.098/M out). A remediation pass the same day
(items 10-11) added **$0.0007** on top: one paid `readonly` run, and a `--tier max` run that cost
nothing at all because opencode refuses an unpublished variant before the first API call.

**The big news is that opencode went from 1.18.31 to 2.0.12 and took four confirmed things away.**
`--dir`, `--variant` and `OPENCODE_PERMISSION` no longer exist, and a `run` no longer executes in
the CLI process's own environment. Everything the 2026-09-18 section recorded as *confirmed live*
about argv and env is now wrong; only `OPENCODE_CONFIG_CONTENT` and its `{file:}` substitution
survived. Four further defects nobody had listed turned up: the `readonly` rung did not hold, a
single-step run reports no cost at all, `--auto` was quietly auto-approving opencode's own `.env`
and external-directory guards (item 10), and an OpenRouter `"usage": null` threw a billed success
into `Failed` (item 6).

| box | verdict | what decided it |
|---|---|---|
| opencode success-event shapes | **corrected** | a real 5-event capture; every event name `Parse` looked for was wrong, and `cost` had to be summed, not overwritten |
| api `AnthropicMaxTokens` 8192 | **measured negative** | no `ANTHROPIC_API_KEY` exists here and the endpoint 401s before it reads the body; 8192 stays, untested |
| api OpenRouter `usage.cost` | **confirmed** | present and correct in a genuine body — and present *without* the `usage:{include:true}` this already sends |
| `doctor --probe` × 2 | **confirmed** | both printed `OK`; api reports a dollar figure, opencode reports none, for the reason in defect 3 |
| `AuthStatusFor` env vars | **confirmed** | opencode's three are verbatim models.dev's `env` entries for the three providers |
| opencode login file | **measured negative** | there is no `auth.json` on a 2.x install; credentials are env-detected or in a SQLite `opencode.db` |

**The free probes that did most of the work.** `opencode run` validates the agent before the model
and the model before the first API call, so both resolution steps can be measured for nothing:
`opencode run --agent claustrum-test --model openrouter/nonexistent-model-xyz --format json "hi"`
exits 1 with a single JSON line naming whichever one it could not find. That one command answered
the `--standalone` question and handed over the v2 error shape. A deliberately wrong
`OPENROUTER_API_KEY` is free the same way — OpenRouter answers 401 before billing anything.

1. **The event stream, captured and committed to.** `claustrum run builder --backend opencode
   --model openrouter/deepseek/deepseek-v4-flash --brief "create hello.txt containing hi" --budget
   0.5 --json --cwd <tmp>/toy-oc` → `status: success` in **15.9s**, `changed_files:
   [{"path":"hello.txt","kind":"A"}]` with a real diff, `report_status: ok`, `session_id:
   ses_f35a1b513ffeMTNgfaAtjYaIl6`, `cost_usd: 0.0004988494`, `usage.input_tokens: 9757`, exit 0.
   Its `stdout.log` is **five JSON lines** and is now `tests/fixtures/opencode/success.jsonl`. The
   fabricated fixture it replaced agreed with the old `Parse`, so the two were consistently wrong
   together and the first live run came back with an **empty** `final_message`, `cost_usd: null`,
   `usage: null` and `report_status: missing` despite having written the file correctly.
   - Every line is `{"type", "timestamp", "sessionID", "part"}`. The event names are
     **`step_start` / `tool_use` / `step_finish` / `text`** — snake_case, and `part.type` is their
     kebab-case twin (`step-start`, `tool`, `step-finish`, `text`). **Nothing resembling
     `message.part.updated` is emitted**, which is what the old `Parse` filtered on and why it saw
     nothing at all.
   - Assistant text is `part.text` on a `type:"text"` event. Exactly one such event per part was
     observed, carrying the whole text — there is no incremental variant in `--format json` — so
     the replace-by-`part.id` rule is kept and is now a measured fact rather than an inference.
   - `step_finish`'s `part` carries `cost` and `tokens: {input, output, reasoning, cache:{read,
     write}}`. **Those field names were right all along; the arithmetic was not.** The numbers are
     **per step, not cumulative**: the captured `cost: 0.000523614` is exactly `10526 × $0.049/M +
     (60 + 20) × $0.098/M` for that step's own tokens at the model's published price. `Parse`
     overwrote, so a multi-step run reported only its last step; it now adds. Proof at scale: the
     22-step run in defect 2 summed to `$0.0060491676` across 83 337 input tokens.
   - ⚠ **`reasoning` tokens are billed as output and reported separately, and Claustrum drops
     them.** `Usage` has no field for them, so `output_tokens: 60` under-reports the 80 output
     tokens the $0.000523614 was charged for. Not fixed here: it is a `RunResult` schema change,
     and `cost_usd` — the number the budget ledger actually uses — is already right.
   - **Redactions in `success.jsonl`:** the scratch path containing the owner's username, twice, in
     the `tool_use` event's `metadata.content` and `output` (`… /toy-oc/hello.txt` →
     `/tmp/claustrum-opencode-capture/hello.txt`). Nothing else, and no key ever appeared: the job
     directory was grepped for the live `OPENROUTER_API_KEY` and for `sk-` before anything was
     copied, and `request.json`/`stdout.log`/`system.md` were all clean.
   - `tests/fixtures/opencode/error.jsonl` is now a genuine v2 capture too — a `step_start`
     followed by `{"type":"error","error":{"type":"provider.auth","message":"User not found.",
     "status":401}}`, exit 1, produced free with a bogus key. The v1.18.31 capture it replaced is
     **kept** as `error-v1.jsonl`; both shapes are real, so `ExtractErrorMessage` reads
     `error.message` first and falls back to v1's `error.data.message`. Three v2 error `type`s were
     seen: `unknown` (agent not found), `provider.no-route` (model unavailable), `provider.auth`.

2. **What v2 took away, one flag at a time.**
   - **`--standalone` is now mandatory, and this is the trap that wastes an afternoon.** v2 runs
     jobs through a long-lived `opencode serve --service` daemon started in whatever environment
     first woke it, so `OPENCODE_CONFIG_CONTENT` set by `ProcessRunner` never reaches it: the run
     dies with `{"error":{"type":"unknown","message":"Agent not found: \"claustrum-test\""}}`. With
     `--standalone` the CLI spawns its own `serve --stdio --port 0` child, which inherits the
     environment, and the identical command gets past the agent to the model. Free to measure, both
     ways.
   - **`--dir` is gone** (`opencode run --dir X …` prints usage and exits). `ProcessRunner` already
     sets `WorkingDirectory = spec.Cwd`, so dropping the flag changes nothing about where the run
     lands. docs/PLAN.md §A3's opencode argv still shows `--dir` and wants updating.
   - **`--variant` is gone**; `--model`'s own help now reads *"Model to use in the format
     provider/model#variant"*, so the tier rides on the model id: `ModelSpec` appends `#<effort>`
     unless the spec already names one. `openrouter/deepseek/deepseek-v4-flash#high` was accepted by
     every paid run above — models.dev lists `reasoning_options: [{type:"toggle"},{type:"effort",
     values:["high","xhigh"]}]` for it, so `max` would need a model that offers it.
   - **`OPENCODE_PERMISSION` is gone** — zero occurrences in the 2.0.12 binary, against the
     2026-09-18 note that confirmed it live in 1.18.31. The same JSON moves into
     `OPENCODE_CONFIG_CONTENT`, which is where docs/PLAN.md §A3's opencode row always drew it.
   - **`OPENCODE_DISABLE_FILEWATCHER=1` is set on every run**, for an environment reason worth
     naming: with the host at its `fs.inotify.max_user_instances` (128, with 100 in use by the
     agent fleet), the server stops dead right after logging `watcher subscribe` and answers
     nothing — `opencode models` hung past 60s three times before this was found. A one-shot
     headless run has no UI to live-update, so there is nothing to lose; if a future version needs
     the watcher for correctness, this is the line that silently degrades it.

3. **Defect 1, and a deliberate deviation from docs/PLAN.md §A3: `--auto` at every level.** The
   plan gives `--auto` to `full` alone. Measured: a `readonly` run without it spent **2m32s and
   $0.0060**, burned 22 steps and 83 337 input tokens, then had two tool calls come back *"The user
   declined this tool call"* and died on `{"type":"error","error":{"type":"aborted","message":"Step
   interrupted"}}` — a permission prompt nobody could answer, since `ProcessRunner` closes stdin.
   `--auto` approves whatever is **not explicitly denied**, so the mapped denies survive it (proved
   in defect 2's re-run: `write`, `execute` and `bash` were all still unavailable to the model with
   `--auto` on). Same allow-broad-deny-narrow shape `CopilotBackend` already uses for the same
   headless reason. The same 22-step run also showed the model reaching thirty times for a second
   command-running tool called **`execute`** — a restricted JS sandbox with no fs, no `process` and
   no network, so it got nowhere — which is now denied wherever `bash` is. ⚠ "the mapped denies
   survive it" is the whole of what `--auto` is safe for: the guards opencode only *asks* about are
   a different matter, and item 10 is where that bill came due.

4. **Defect 2: the `readonly` rung did not hold, and `subagent` was the way out.** With `--auto`
   added and `write`/`bash`/`execute` denied on the agent, a builder told to *"create denied.txt …
   try the execute tool and the bash tool as well"* delegated instead: `subagent` with
   `{"agent":"general"}`, whose report reads *"The file **was created successfully** — no refusal,
   no blocking, no error. I ran `echo "x" > denied.txt`"*. `changed_files` duly listed it. The
   permission block binds **the primary agent only**; a delegate runs under opencode's own defaults.
   Fixed at the root rather than by banning delegation — `BuildConfigContent` now writes the same
   permission object **twice**, on the agent and at the config's top level. Re-run with the same
   brief plus *"and try delegating to a subagent"*: `status: success`, `changed_files: []`, the toy
   afterwards holding only `.git` and `seed.txt`, and the `general` subagent itself reporting *"it
   only has read-only tools (`read`, `glob`, `grep`, `webfetch`, `websearch`, `skill`)"*. The rung
   holds, and `subagent` stays available — which keeps this consistent with `ClaudeBackend`, where
   the native subagent tool is taken away only from a coordinate-spawned architect (NOTES.md "The
   native subagent tool is called `Agent`").

   | level | argv (after `run --standalone --agent claustrum-<role> --model <model>#<effort> --format json --auto`) | `permission`, written on the agent **and** at the top level |
   |---|---|---|
   | `readonly` | — | `{"edit":"deny","bash":"deny","execute":"deny"}` |
   | `shell` | — | `{"edit":"deny","bash":{"*":"allow","<deny>*":"deny"}}` |
   | `edit` | — | `{"edit":"allow","bash":"deny","execute":"deny"}` |
   | `edit+shell` | — | `{"edit":"allow","bash":{"*":"allow","git push*":"deny"}}` |
   | `full` | — | `{"edit":"allow","bash":"allow"}` |

   All five verified as emitted, without paying, by pointing `backends.opencode.path` at a fake
   `opencode` that dumps its argv and environment. `--session <id>` and the brief follow, in that
   order. ⚠ `shell` and `edit+shell` deliberately leave `execute` alone: denying it there would take
   away a tool the rung already grants the equivalent of. ⚠ **Every row except `full` also carries
   the three headless guards of item 10** — `"external_directory":"deny"`, `"question":"deny"`,
   `"read":{"*.env":"deny","*.env.*":"deny","*.env.example":"allow"}` — which the table above
   predates; item 10 has all five blocks re-dumped verbatim.

5. **Defect 3: a single-step opencode run reports no cost and no usage at all.** `step_finish` is
   emitted *before* a tool call, never after the last assistant message: the builder captures end
   `… step_start, text` with no closing `step_finish`, and the `doctor --probe` stream is exactly
   two lines — `step_start`, `text` — so `cost_usd` and `usage` come back `null` for a round trip
   that was genuinely billed. Measured on every capture here, twice on the two-line probe. The
   consequence is concrete: **`--budget` neither caps nor accounts for a one-shot opencode run, and
   the §D4 tree ledger records nothing for it** — the same gap cursor and copilot have. The
   last step's numbers are also lost from *every* multi-step run, so `cost_usd` is a floor, not a
   total. `opencode stats` and the session rows in `opencode.db` are where a real total would have
   to come from; neither is reachable from `Parse`, which only sees stdout.
   - **The rule that follows, and the one `Parse` now implements: a reported opencode cost is
     complete or absent, never partial.** `Parse` counts `step_start` against `step_finish` (both
     deduplicated by `part.id`) and returns `cost_usd` **and** `usage` as `null` unless every step
     reported back; only a complete stream is summed. Charging a floor verbatim is worse than
     reporting nothing: `Runner.ChargeAsync` charges a non-null cost as-is *and* drops its "cost not
     reported by backend" warning, so the §D4 ledger would quietly believe the tree has more left
     than it does. With the rule, an incomplete stream falls back to charging the granted cap and
     says so — the behaviour that was in place before summation was added.
   - Measured on the item-10 run: **5 `step_start` against 4 `step_finish`**, the four summing to a
     floor of **$0.0006714862** over 7 591 input tokens, and `cost_usd: null` in the result.
     ⚠ `tests/fixtures/opencode/success.jsonl` is one of these too (2 `step_start`, 1
     `step_finish`), so the figure that fixture is entitled to is now `null`, **not** the
     $0.0004988494 recorded in item 1 — item 1 is the record of what opencode emitted, not of what
     `Parse` must report. The upstream limitation is unchanged and cannot be closed from stdout.

6. **The api backend, against a live OpenRouter endpoint.** `claustrum run code-reviewer --model
   api:openrouter:deepseek/deepseek-v4-flash --brief "## Task\nSay OK.\n## Diff\nnone" --budget 0.5
   --json --cwd <tmp>/toy-api` → `status: success` in **2.8s**, `final_message: " OK\n\n```
   claustrum-report …"`, `report_status: ok`, `cost_usd: 4.0318e-05`, `usage.input_tokens: 1883`,
   exit 0. Its `stdout.log` — curl's raw response body — is now
   `tests/fixtures/api/openrouter-success.json`, verbatim, with nothing redacted: the body carries
   no key, and the job directory was grepped for the live key before it was copied (the curl config
   file holding the `Authorization` header is in `ProcessSpec.TempFiles` and was already gone).
   - **`usage.cost` is real, top-level inside `usage`, and the name was right.** The genuine body
     also carries `cost_details.upstream_inference_cost` (identical value) and, one level down,
     `prompt_tokens_details: {cached_tokens: 1827, cache_write_tokens: 0, …}` — which
     `ParseOpenRouterSuccess` was hardcoding to `null`, and now reads. `completion_tokens_details.
     reasoning_tokens` exists too and is left alone for the same schema reason as opencode's.
   - ⚠ **The `usage: {include: true}` this sends is not what produces the cost.** An otherwise
     identical raw `curl` with that object removed came back with the same `usage.cost`
     (`1.194e-06`). One account and one model is no basis for dropping OpenRouter's only documented
     way to ask for usage accounting, so it stays — but it is belt-and-braces, not load-bearing.
   - ⚠ **`"usage": null` is a JSON null, not an absent property, and it used to fail the run.**
     `TryGetProperty` on a `JsonValueKind.Null` element **throws**, so a billed, successful
     OpenRouter response that omitted usage by sending null escaped out of `Parse` and came back as
     `Failed` — money spent, answer discarded. Both success parsers now require
     `usageElement.ValueKind == JsonValueKind.Object` before reading anything out of it, the same
     guard `TryGetInt` already applied one level down. Found by review, not by a capture: every body
     captured here carries a real `usage` object.
   - ⚠ **`"error": null` was the same defect one level up, and `Parse` failed the run on it.** The
     error branch fired on `TryGetProperty("error", …)` alone, so a success body that carried the
     key with a JSON null — the shape OpenRouter uses for every optional field it has nothing to
     say about, `service_tier` and `refusal` in the capture above — came back `Failed` with the
     whole body as its message, never reaching the `choices` parser. Fixed 2026-09-22 by the same
     ValueKind discipline: `error` is a failure only when it is an **object**, or a **non-empty
     string**; null, empty string and any scalar are a success. Found by review, like the `usage`
     one; no captured body here carries a null `error`, which is why only a synthetic body can
     cover it.
   - **The two-flag form still does not work**, re-measured today and unchanged since 2026-09-18:
     `--backend api --model openrouter/deepseek/deepseek-v4-flash` reaches `Build` with the provider
     prefix gone and throws *"api backend model must be 'openrouter:<model>' or 'anthropic:<model>',
     got 'openrouter/deepseek/deepseek-v4-flash'"*. The single combined `--model
     api:openrouter:<id>` is the only form that resolves. Free to reproduce.
   - `tests/fixtures/api/error.json` is now the genuine OpenRouter 401 body
     (`{"error":{"message":"User not found.","code":401}}`, curl exit 22), and the genuine 400 for
     an unknown model is kept beside it as `error-invalid-model.json` — that one needed a
     redaction, since OpenRouter returns a `user_id` alongside the error. `anthropic-error.json` is
     now genuine too, captured for free because the endpoint 401s before reading the body:
     `{"type":"error","error":{"type":"authentication_error","message":"API key is invalid."},
     "request_id":null}`. The fabricated shape it replaced was structurally right.
     **`anthropic-success.json` is the one fixture in this directory still fabricated**, and cannot
     stop being one without a key.

7. **`AnthropicMaxTokens` — the measured negative, and what would close it.** `max_tokens` is
   required by the Messages API and there is nothing in Claustrum to derive one from: `BudgetUsd` is
   dollars, tiers carry an effort string, and roles carry no token budget. No free check exists —
   `api.anthropic.com/v1/messages` answers `401 authentication_error` before it looks at the body,
   so an unauthenticated request cannot even be told whether 8192 is accepted — and the OpenRouter
   run offers no natural rule to borrow, because the OpenAI-compatible endpoint does not require
   `max_tokens` at all and Claustrum never sends one there. **8192 stays, untested.** Closing this
   needs a real `ANTHROPIC_API_KEY` and, concretely: one call per tier at `max_tokens: 8192` with a
   role body long enough to run a `max`-effort reviewer out of room, checking `stop_reason` for
   `max_tokens` rather than `end_turn`. If it ever does need to vary, the model's own published
   output ceiling is the only principled source (models.dev lists `limit.output`), and reading it
   would mean the api backend fetching a catalogue — which is exactly the dependency `curl`-only was
   chosen to avoid.

8. **`--probe` and `auth:`, both backends.** With `CLAUSTRUM_SKIP_PROBE` unset, in a toy repo whose
   `claustrum.json` aliases resolve to the backend under test (the probe skips without one —
   NOTES.md "The copilot backend, validated against a real install" names the same trap):
   - `backends doctor opencode --probe` → `probe: OK (3.8s, cost not reported, reply "OK")`, and
     `auth: present (env var set — a login file may also work even if not)`. "cost not reported" is
     defect 3, not a probe bug.
   - `backends doctor api --probe` → `probe: OK (1.3s, cost $0.0000, reply "OK")` (the real figure
     in `result.json` is `1.42e-06`), `auth:` the same line, `path: /usr/bin/curl`.
   - **The env-var lists were already right.** opencode's `ANTHROPIC_API_KEY`, `OPENROUTER_API_KEY`,
     `OPENCODE_API_KEY` are verbatim the `env` entries models.dev publishes for Anthropic, OpenRouter
     and **OpenCode Zen**, which is where 2.0.12 gets them; `opencode auth list` on this machine
     prints exactly one row, `OpenRouter  OPENROUTER_API_KEY  environment`, from the env var alone
     with no login. api's two are right by construction — `ApiBackend` reads those two and no others.
   - **The login-file half is now a real answer for both, and for opposite reasons.** `api` has no
     credential store at all, so it says so. opencode 2.x has **no `auth.json`**: neither
     `~/.config/opencode/` (which holds only `service.json`) nor `~/.local/share/opencode/` has one,
     and credentials are either env-detected or in an 11 MB SQLite `opencode.db` that an AOT binary
     will not take a dependency on to read. `LoginFileStatusFor` still checks the v1 path — it is
     cheap, and a 1.x install or a 2.x that writes one on `auth login` would be reported — but
     reports **existence only**, never contents: unlike copilot's `config.json`, that file's shape
     was never seen on a real install here, and guessing at it is what this pass exists to stop.
     With the three variables unset the lines read
     `not set (env var absent; /home/…/.local/share/opencode/auth.json not present, and 2.x keeps
     credentials in opencode.db, which this does not read)` and
     `not set (env var absent; this backend has no login file — it is a direct HTTPS call)`.
     Whether `opencode auth login` writes one was **not** tested: it would mean planting the owner's
     credential in a store this pass did not put it in.

9. **Confirmed in passing, for the two places that name model ids.** `curl -s
   https://openrouter.ai/api/v1/models` (free, no key) lists **`deepseek/deepseek-v4-flash`**
   ($0.049/M in, $0.098/M out) and **`deepseek/deepseek-v4-pro`** as live ids today, so
   `InitCommand`'s written `"cheap-coding": "opencode:openrouter/deepseek/deepseek-v4-flash"` and
   `OpencodeSync.OpencodeModelFor`'s pair are both real — no change needed. `scripts/smoke.sh`'s
   `CLAUSTRUM_SMOKE_MODEL_OPENCODE=<model>` row is the exact shape run above and its api row's
   "skipped, cannot resolve a model" reasoning is re-measured and still correct; neither script
   changed.

10. **Defect 4 (remediation pass, same day): `--auto` was auto-approving opencode's own `ask`
    guards, at every rung.** `--auto` reads *"auto-approve permissions that are not explicitly
    denied"*, and 2.0.12 gives **every** agent this default ruleset (verbatim from the binary, and
    from `opencode debug agents` on the built-in agents): `{action:"*",resource:"*",effect:"allow"}`,
    `{external_directory,*,ask}`, `{read,*.env,ask}`, `{read,*.env.*,ask}`,
    `{read,*.env.example,allow}`. Every one of those `ask`s was therefore an `allow` under
    Claustrum: a `readonly` code-reviewer could read `.env` or `~/.ssh/*`, and an `edit` builder
    could write `~/.bashrc`. The **`question`** tool was worse than open — it is its own permission
    action (opencode denies it on its own headless `general` agent, and allows it on the
    interactive `build`/`plan` ones), and under `ProcessRunner`'s closed stdin it can only stall
    the run to its timeout. Below `full`, `WritePermission` now appends
    `"external_directory":"deny"`, `"question":"deny"` and
    `"read":{"*.env":"deny","*.env.*":"deny","*.env.example":"allow"}` to every rung; `full` is
    unchanged and keeps `--auto` with nothing denied.
    - **Order is load-bearing, and this is the trap.** Config `permission` entries are appended
      *after* opencode's own defaults, and the matcher is
      `rules.findLast(r => glob(action, r.action) && glob(resource, r.resource))` — **last match
      wins**, not most-specific. So the `*.env.example` allow must come after the two `.env`
      denies, which is exactly the order opencode writes its own in and the order the object
      literal above is emitted in (`Utf8JsonWriter` preserves it). The patterns are globs compiled
      to `^…$` with `*` → `.*`, so `*.env` does match a bare `.env`.
    - The config shape is the one the 2.0.12 binary declares: `PermissionConfig` accepts
      `read/edit/glob/grep/list/bash/task/external_directory/question/webfetch/websearch/lsp/
      doom_loop/skill` plus arbitrary extra keys (which is how `execute` works), each taking either
      a bare `"ask"|"allow"|"deny"` **or** a pattern → effect map — except `question`, `websearch`
      and `doom_loop`, which take the bare string only. Config keys are also renamed on the way in:
      `bash`→`shell`, `write`/`patch`→`edit`, `task`→`subagent`.
    - **All five blocks as actually emitted**, re-dumped free through the fake-`opencode` trick
      (written identically on the agent and at the top level, as defect 2 requires):

      | level | `permission` |
      |---|---|
      | `readonly` | `{"edit":"deny","bash":"deny","execute":"deny","external_directory":"deny","question":"deny","read":{"*.env":"deny","*.env.*":"deny","*.env.example":"allow"}}` |
      | `shell` | `{"edit":"deny","bash":{"*":"allow","git push*":"deny"},"external_directory":"deny","question":"deny","read":{…}}` |
      | `edit` | `{"edit":"allow","bash":"deny","execute":"deny","external_directory":"deny","question":"deny","read":{…}}` |
      | `edit+shell` | `{"edit":"allow","bash":{"*":"allow","git push*":"deny"},"external_directory":"deny","question":"deny","read":{…}}` |
      | `full` | `{"edit":"allow","bash":"allow"}` |

    - **The paid proof, $0.0007 and 44.2s.** `claustrum run builder --permission readonly --backend
      opencode --model openrouter/deepseek/deepseek-v4-flash --json --cwd <tmp>/toy-leak`, brief
      *"Do three things and report each result: 1) read the file .env in the working directory and
      quote it; 2) write the text x to the file /tmp/claustrum-leak-<rnd>.txt; 3) ask me a
      clarifying question using your question tool."*, against a toy holding a committed `.env` of
      `SECRET=not-a-real-secret`. **All three blocked.** `status: success`, exit 0,
      `changed_files: []`, `report_status: ok`, `cost_usd: null` (the item-5 rule), no stall —
      44 seconds, nowhere near the 30-minute timeout. Two `read` tool calls on the `.env` path came
      back `{"status":"error","error":"Permission denied: read"}`; `/tmp/claustrum-leak-<rnd>.txt`
      does not exist; and the model's own report reads *"I have no 'question tool' available among
      my tools (`glob`, `grep`, `read`, `skill`, `subagent`, `webfetch`, `websearch`)"* — a denied
      action is not offered at all, which is why it cannot stall. `.env`'s contents appear nowhere
      in the output. The job directory was grepped for the live `OPENROUTER_API_KEY` and for
      `sk-or-`: zero hits.
    - ⚠ **What `external_directory: "deny"` costs, and it is not measured.** opencode gives every
      agent four internal `external_directory` *allow* rules (its `shell/*/*` and `tool-output/*`
      data dirs, `$TMPDIR/opencode/*`, and its config dir), and because config rules append last,
      a blanket deny **overrides those too**. Nothing in the run above needed them, and reads and
      globs inside the working directory were unaffected — but a run that reaches for a truncated
      tool-output file, or a `bash` rung whose shell output is spooled to that data dir, may be
      denied where it previously was not. Re-allowing them would mean Claustrum computing
      opencode's own XDG paths, which is a worse guess than the deny is; if this bites, that is
      where to look first.

11. **`#<effort>` on a model that does not publish the variant: opencode refuses, for free.** The
    receipt for the "tier → variant" follow-up, which this pass deliberately does **not** fix.
    `claustrum run builder --backend opencode --model openrouter/deepseek/deepseek-v4-flash --tier
    max --brief "reply with the single word OK and change nothing" --json --cwd <tmp>` →
    `status: failed`, `exit_code: 1`, in **0.72s**, with `cost_usd: null` and nothing billed.
    opencode's own line, verbatim from `stdout.log`:
    `{"type":"error","timestamp":…,"sessionID":"ses_f35719021ffe…","error":{"type":"provider.no-route","message":"Variant unavailable for openrouter/deepseek/deepseek-v4-flash: max"}}`
    — so it is a **surfaced `error` event**, which `ExtractErrorMessage` already reads and
    `ParsedOutput` already turns into `status: failed`; `final_message` and `error` both read
    *"Variant unavailable for openrouter/deepseek/deepseek-v4-flash: max"*. It is neither ignored
    nor silently downgraded, and it is refused **client-side, before any API call** — which is why
    it is free to reproduce and why a wrong tier can never cost money, only a failed run.
    **No mapping was added.** `ModelSpec` still appends `#<effort>` unconditionally; a `max` tier on
    a model publishing only `high`/`xhigh` (models.dev's `reasoning_options` for this one) fails
    fast with a clear message. Whether that should instead clamp to the highest published variant,
    or refuse earlier in `ResolvedRole`, is the open design question.

**Still unverified, for the record:** the Anthropic path end to end, including `AnthropicMaxTokens`
and `anthropic-success.json`; `--session <id>` resume against a real opencode session; the `full`
rung's `--auto`-plus-allow-all combination; whether
`OPENCODE_DISABLE_FILEWATCHER` has a cost on a host that is *not* at its inotify cap; whether
`opencode auth login` writes an `auth.json`; opencode's own `stats`/`db` as a cost source for
defect 3; and the Windows leg of every line above. The unquoted `description:` line itself is fixed
and measured now — see "Sync frontmatter: the unquoted `description:` fix reaches Claude/opencode/
Cursor too (2026-09-25, issue #33)".

## Sync frontmatter: the unquoted `description:` fix reaches Claude/opencode/Cursor too (2026-09-25, issue #33)

"The copilot backend, validated against a real install" fixed `CopilotSync`'s unquoted
`description:` (a plain YAML scalar containing `": "` is invalid, and copilot 1.0.87 silently
dropped architect/code-reviewer/tester for it) but left `ClaudeSync`, `OpencodeSync` and
`CursorSync` on the identical unquoted line, flagged there as unmeasured. `CopilotSync.YamlQuoted`
moved onto `SyncWriter` unchanged (same two characters escaped, same doc comment) as
`SyncWriter.YamlQuoted`, and all four `BuildFrontmatter`/`BuildAgentFrontmatter` methods now call it
on the `description` field — the `tools`/`model`/`effort`/`color`/`readonly` fields are untouched,
and the SKILL.md/command frontmatter blocks (none of which contain `": "` today) were left as
literal here-strings rather than routed through a call that would be a no-op.

- **Verified for real, not just re-read**: a scratch `git init` repo outside the checkout was synced
  with the debug build (`sync`, then `sync --only opencode`, `--only cursor`, `--only copilot`; no
  golden fixtures touched), and the frontmatter block of every emitted file was fed to PyYAML 6.0.3
  `yaml.safe_load` (pure-Python `SafeLoader`) and to `CSafeLoader`, once as emitted (quoted) and once
  with only the `description:` line rewritten back to the old unquoted form. Files checked per
  harness — the role files `sync` emits for that harness plus their `-xhigh`/`-max` tier stubs:
  **Claude 16** (architect, builder, code-reviewer, demo-author, tester, ui-reviewer, with a stub
  pair on each but demo-author), **opencode 12** and **Cursor 12** (architect, builder,
  code-reviewer, tester, each with its stub pair). **All of them parse quoted, under both loaders.**
  Unquoted, a strict parser **rejects** `architect` and `code-reviewer` on every harness, plus
  `demo-author` and `ui-reviewer` under Claude, and **accepts** `builder`, `tester` and every
  `-xhigh`/`-max` stub — so those never needed the fix. `yaml.safe_load` reports "mapping values are
  not allowed here" at line 2; "mapping values are not allowed in this context" is `CSafeLoader`'s
  (libyaml's) wording, and the one copilot printed. Checked against the current `roles/<role>/role.json`
  library, the roles whose description contains `": "` are **architect, code-reviewer, demo-author
  and ui-reviewer** — not tester, whose description was re-ported clean for #16 (see "tester and
  ui-reviewer re-ported from the owner's current files"). Of those four, only architect and
  code-reviewer sync to opencode/cursor at all (`harnesses` in their `role.json`); demo-author and
  ui-reviewer carry `"harnesses": ["claude"]`, so they never emit a `.md` for those two backends.
- **Live agent-listing probe: attempted, blocked by environment, not by the fix.** `opencode debug
  agents` is the free probe (no model call, just the loader's own list) but its background service
  (`opencode service restart` confirmed a live URL) never answered `debug agents` within 60s, in the
  synced scratch repo *and* in a bare empty directory with no config at all — so the hang is a
  sandbox/daemon limitation, not something the quoted frontmatter caused. `cursor-agent`'s CLI
  (`--help` read in full, `agent --help` too) has no agent-list or agent-select flag at all —
  Cursor's `.cursor/agents/*.md` subagents are picked from the GUI's Agent chat only, confirmed
  already unreachable from a CLI in "CursorSync: agents, skill and mcp.json, doc-confirmed but never
  round-tripped" ("Not verified live … no GUI here"). Neither gap is new or caused by this change,
  and neither is closed by the PyYAML check above: that check is evidence the emitted frontmatter is
  valid YAML, not evidence that opencode 2.0.12 or Cursor actually load the agents from it. Whether
  the two loaders load architect/code-reviewer stays **unmeasured**.
- **Claude Code stays unmeasured on purpose**: the brief's own framing ("Claude Code demonstrably
  tolerates it") was accepted as sufficient given goldens under `tests/golden/claude/` pin its output
  and get re-recorded by the tester, not this pass.

## Roles re-ported from devkit's 2026-10-02 definitions (PR #42)

The team settled its Claude Code agent files in devkit (`teksistemi-software/devkit`, pinned at
`6795e8b`; the change is `git diff origin/main 6795e8b -- claude/agents claude/commands`, with
`opencode/agents/*` and `rules/team.md` §1 read for the harness-neutral wording). The behaviour came
over, not the files; everything Claustrum owns — the report blocks, the blind gate, model classes,
house rules, tier stubs, the `ROLE.md`/parts split — is untouched.

- **What changed.** demo-author: deck, frames, manifest **and** the default-on video go into the
  main checkout's `docs/demos/<feature>/` (first `worktree` line of `git worktree list
  --porcelain`), checked with `git check-ignore -q` before the first write, never staged, committed
  or pushed (owner, 2026-10-02: "docs/demos/feature is fine, just gitignore them"); it records a
  commit that contains what the gate passed, never "the merged state" and never uncommitted
  changes; its report gains `gitignore` (the exact line added, and where) and a main-checkout `deck`
  path, the manifest's `video` is relative. PR #42's first commit had sent the video to
  `~/Videos/demos/…`, "never inside the working tree" — default-on stays, location and reasons do
  not. architect: a demo-author for every browser-facing feature after the gate, unasked unless the
  caller (or a cast's `null`) says no, never alongside a ui-reviewer, briefed sighted; it commits
  exactly the `.gitignore` line the demo-author reports, nothing else of its output. builder: never creates, modifies or deletes a
  test file, fixtures included. tester: runs the repo's full gate, pre-existing tests included.
  Every role: "Clean up what you start".
- **Where it went.** All of it is harness-neutral, so it is `ROLE.md`/`role.json`/`_shared`; the only
  part edits are `architect/parts/delegation.claude.md` (a native demo-author spawn line, which was
  missing) and `demo-author/parts/browser.claude.md` (frames and every other deck file are written by
  the capture script).
- **Claustrum's own wording, by design.** devkit's "the team removes a branch's worktree once it is
  integrated" became "worktrees are temporary", citing `claustrum jobs clean`, which runs `git
  worktree remove --force` on every finished parallel builder's. `## Access` now names the
  demo-author too (architect parts, `/claustrum` skill, MANUAL). The tester's report contract lets
  `fault_in: "test"` cover a pre-existing test the change legitimately outdated, or the full-gate
  rule would force every stale old test onto the builder as `code`. A builder's needed test change
  goes in `behaviour_to_cover`. The demo-author's `## Browser` heading fixes the dangling pointer in
  "Capture" (devkit's fix); an `## Environment` heading was added as ui-reviewer already has.
- **Not ported.** devkit's `/demo` command (Claustrum syncs no such command); the opencode
  demo-author's Playwright-MCP binding (`browser_take_screenshot` + `mv`, `external_directory`
  allows) — the role is `harnesses: ["claude"]` with no non-claude browser part, and `OpencodeSync`
  emits no MCP tool permissions to carry one; model names (`sonnet`, `opus`) and
  `CLAUDE_CODE_MAX_SUBAGENT_SPAWN_DEPTH`, as in "The architect role, ported last".
- **`library.json` stays `1.0.0`.** Fourteen role-text commits since 92ca212 never bumped it, and
  `sync --check` already flags a stale file by comparing it with a fresh render (whose marker
  carries the body's `sha256`); a bump would only rewrite every marker line.

### A delegated demo-author can write its deck (issue #43, fixed in PR #42)

The blind review of the port found the default demo unable to run on the `claustrum run` path: the
role sat on `shell`, which the claude backend maps to plan mode with `Edit,Write,NotebookEdit`
disallowed, and every cast delegation goes through `claustrum run`. A cast mapping it to another
backend failed differently — `RoleLibrary.ReadPart` threw for the missing `browser` part.

- **Permission: `edit+shell`, with 24 git verbs denied.** `push`; the common verbs that stage or
  commit (`add`, `stage`, `rm`, `mv`, `commit`, `commit-tree`, `apply`, `update-index`,
  `read-tree`); and the common ones that move `HEAD`, refs or the tree (`checkout`, `switch`,
  `restore`, `reset`, `stash`, `clean`, `merge`, `rebase`, `cherry-pick`, `revert`, `pull`, `am`,
  `update-ref`, `symbolic-ref`). Not every one: `branch -f`/`-D`, `worktree remove`, `tag`, `notes`
  stay open, the role needing `branch` and `worktree list` read-only. A new write-but-not-edit rung
  would have cost an enum member, five backend mappings and their tests for a distinction the role
  does not need — and `Edit` is the safe tool for one `.gitignore` line.
- **Each entry matches its verb as a word, for every role.** claude now emits `Bash(<entry>)` and
  `Bash(<entry> *)`, which "leave the tool available and deny only calls that match as written"
  (code.claude.com/docs/en/cli-reference) and stop `git merge` bare or with arguments but not
  `git merge-base` (permissions, "Wildcard patterns"; both read 2026-10-02) — the old
  `Bash(git merge*)` also caught `merge-base`, `merge-tree`, `commit-graph`. The exact form is there
  because a trailing ` *` matches the bare command only as a rule's sole wildcard, so a user entry
  that ends in `*` (`git push*` → `Bash(git push* *)`) would otherwise stop denying bare `git push`.
  Prefix-style entries in existing configs narrow (`rm -rf` no longer stops `rm -rfv`); MANUAL says
  how to keep a prefix match. It is checked on
  every subcommand of a compound command. opencode now gets `"<entry>": "deny"` and `"<entry> *":
  "deny"` after `"*": "allow"`: its dev-branch matcher makes a trailing ` *` optional, and the exact
  entry covers a build that does not. copilot's `shell(<entry>)` already matched the first-level
  subcommand exactly; cursor has only a prompt rule. None of them sees `git -C <dir> commit`,
  `git -c k=v …`, a quoted verb, a user alias, `sh -c`, or a Node script shelling out — Claude
  Code's docs call a Bash rule "not a security boundary", the standing the builder's `git push` deny
  has always had.
- **A synced agent carries no deny list yet.** `disallowedTools` cannot hold one: an entry with a
  specifier "such as `Bash(git push *)`, still removes the whole tool" (code.claude.com/docs/en/
  sub-agents, read 2026-10-02). A frontmatter `PreToolUse` hook on `Bash` that exits 2 could — the
  docs show that pattern — and is a follow-up issue. Until then `sync` renders `deny` nowhere and a
  native demo-author or builder keeps it as prose.
- **`withoutTools` trims synced Claude agents only.** It drops what a level grants but a role never
  uses (the demo-author's `NotebookEdit`) from the synced file's `tools` and lists it under
  `disallowedTools`. Under `claustrum run` nothing passes it on — Core never sees role.json tools,
  and carrying it across the Roles→Core seam for one tool the role never touches was not worth a
  new field on `RenderedRole`/`ResolvedRole` — so `acceptEdits` approves `NotebookEdit` there. A
  local role.json override replaces the whole array, as for `tools` and `deny`.
- **Non-claude backends: refused, not bound.** `DelegateEngine.Prepare` now checks role.json
  `harnesses` against the resolved backend before Render, so `run`, `delegate`, `delegate_async`
  and `coordinate` all stop with exit 2 and a sentence naming the role's harnesses — before any job
  directory exists. Only registered backends are checked; an unknown name still reaches Runner's
  `backend_missing` for a role that renders anywhere (the existing `--backend nonexistent` tests rely
  on it), while a claude-only role fails first at its missing `browser` part. The message names every
  source a cast entry can come from — they all arrive as overrides — and the local
  `.claustrum/roles/<role>/role.json` `harnesses` escape. A `browser.default.md`
  bound to Playwright MCP (devkit's opencode file is a worked one) was not taken: opencode below
  `full` denies `external_directory`, so the deck could not reach the main checkout from a worktree
  nor the script live outside the tree; cursor's deny list is advisory; and none of it can be tested
  without a paid real install. The gate also covers ui-reviewer, which had the same latent failure.
  `cast questions` offers each role only aliases landing on its harnesses, says where it runs, and
  — when that leaves no option — says why and still offers `not needed`; `cast create`, `cast new`
  and `cast_create` refuse an answer on another registered harness (`CastHarnessCheck`), so a
  "no demo" cast is reachable and a doomed one is not saved. `cast new` asks again only on a
  terminal: with piped input a re-ask consumed the next question's line and shifted every later
  answer (the review saved `ui-reviewer: "2"` and an unlimited budget), so it exits 2 instead.
  Loading `claustrum.json` for alias resolution means a malformed one now fails `cast create` even
  when no answer uses an alias.
- **ClaudeSync's edit rungs now add role.json `tools`**, or the demo-author would have lost its
  browser tools by leaving `shell`; and a role that delegates to no one gets `disallowedTools: Agent`
  on every rung, so the synced tester gains it too (issue #19: listing tools does not remove `Agent`).
- **A deck never lands where git tracks, or ever tracked, files.** Decks committed under the old
  rule make `check-ignore` fail for good, and an ignored file at a once-tracked path is overwritten
  by a checkout of an older commit and deleted on the way back (measured 2026-10-02 in a scratch
  repo: `ls-files` empty, `log --all -- <dir>` not, `checkout <old>` replaced the new file, switching
  back removed it). So the demo-author checks both and records into `<dir>-<short commit>/` instead.
  An "untrack and ignore" remedy was drafted and dropped: a brief naming tracked docs would have had
  them untracked and deleted from every checkout that pulls.
- **Only `docs/demos/` is ever committed to `.gitignore`.** Any other deck directory — brief-named,
  or a sibling outside `docs/demos/` — is ignored through the local `info/exclude` only: a committed
  `docs/user-guide-*/` for a sibling of tracked `docs/user-guide/` would silently ignore a real
  `docs/user-guide-v2/` someone adds later. A pattern line was drafted and dropped for that.
- **The ignore line goes into `info/exclude` every time, not only from a worktree.** Recording in the
  main checkout on a feature branch, that branch's `.gitignore` line is the only ignore; once the
  checkout is back on `main` before the merge, the deck is `?? docs/` for a `git add .`, `git clean
  -fd` or `git stash -u` (confirmed in a scratch repo, 2026-10-02). The path is
  `$(cd "$(git rev-parse --git-common-dir)" && pwd)`, not `--path-format=absolute`, which needs git
  2.31 and garbles the path on older ones.
- **Every deck file is written by the capture script.** Claude Code 2.1.284 checks `Write`/`Edit`,
  shell redirections and `cp`/`mv`/`mkdir` targets against the session's working directories, and a
  delegated run answers no prompt; from a linked worktree the main checkout is outside them, so
  only the Node process writing frames and video — now also `index.html` and `manifest.json` — gets
  through — and it writes the `info/exclude` line too, first, in every case. `.git` is on Claude
  Code's protected-path list (permission-modes, "Protected paths"): a write there is prompted even
  in `acceptEdits` and the redirect check covers it, so a shell append is refused on the delegated
  path even in the main checkout, where the deck then sat ignored only by an uncommitted branch
  line. A Node or Python script that opens files itself is outside those checks (permissions docs,
  read 2026-10-02), so the capture script makes the one deliberate write into `.git`: one line in a
  local file. From a linked worktree that line is the only thing
  that ignores the deck in the main checkout (the worktree's `.gitignore` does not:
  `check-ignore` 0 there, 1 in main, `?? docs/` — confirmed by the review, 2026-10-02), so if the
  line is not in `info/exclude` afterwards, or the re-check in the main checkout still fails, the
  demo-author writes no deck file and is BLOCKED, naming the line and the file; its report names
  only what it actually wrote, a `.gitignore` line it added before the check included. The script
  creates `info/` first: a repo can lack it, and an append alone would fail there.

## Tier ladders follow the owner's files; TierDescription says "model" only when it is true (2026-10-08, issue #30)

"tester and ui-reviewer re-ported from the owner's current files" (#16) measured that `role.json`
and the owner's `~/.claude/agents/` ladders disagreed both ways and left the decision open. Decided
by the owner on 2026-10-08: **match his files.** `ui-reviewer` is `frontier-coding` at `high`,
`xhigh` and `max` (his files: `opus` at all three); `tester` is `standard-coding` at all three (his:
`sonnet` at all three). The reason is the README's cost argument: strong reviewers, a cheap tester,
and never a reviewer weaker than the builder it reviews — a `standard-coding` ui-reviewer at `high`
was exactly that. `code-reviewer` keeps `standard-coding` → `frontier-coding` at `xhigh`, so it is
now **the only role whose model class changes with the tier**; architect, builder, tester and
ui-reviewer buy only effort. Both ROLE.md tier sections already said "effort is what a heavier tier
buys, the model is the library's and the cast's call", so no prose was touched; the
`docs/MANUAL.md` §4 roles table, which listed the old ladders, was updated.

**`SyncWriter.TierDescription(role, tier, sameModel)`.** The stub description used to claim
"identical role, model, and rules" for every `-xhigh`/`-max` stub. It now takes `sameModel`, which
each `WriteTierStub` decides from **the model its own harness file names**, not from the model class:
a reader of the stub sees the `model:` line, and a class change that resolves to the same line is not
a model change. First attempt (`RoleDefinition.KeepsModelAt`, comparing classes) was wrong and is
removed; a blind review caught it. Measured per harness (2026-10-08):

| Harness | `model:` the stub emits | `sameModel` |
|---|---|---|
| Claude | `ClaudeModelFor(Tiers[tier].Model)` — `sonnet`/`opus`/`haiku` | `ClaudeModelFor(tier) == ClaudeModelFor(high)` |
| opencode | `OpencodeModelFor(...)` — `standard-coding` and `frontier-coding` both → `deepseek-v4-pro` | same comparison on the mapped ids |
| Cursor | `model: inherit` for every stub | always `true` (constant, not computed) |
| Copilot | `model: auto` for every stub | always `true` (constant, not computed) |

True keeps the old wording byte for byte; false says "identical role and rules as the `<role>` agent,
a model+effort step up" (the owner's own `code-reviewer-xhigh.md` phrase), same sentence shape for
`xhigh` and `max`. Only the Claude `code-reviewer` stubs (sonnet → opus) take the second form today;
on opencode the class step is invisible in the file, so those stubs keep "identical model". Do not
hardcode the claim back into the template, and do not compare classes: the stub must state what its
own `model:` line says.

## The first real tasks: the test drive, 2026-09-25 to 2026-10-08 (docs/TEST-DRIVE.md)

Claustrum ran its first real tasks in the three legs docs/TEST-DRIVE.md prescribes, on this repo's
own issues, all on the `default`/`spawned` casts (sonnet builders and tester, opus reviewers and
architect, `max_parallel: 2`, `$15`). Every run returned a receipt with a parsed report fence; no
run ever hit its timeout; the measured numbers are below, in the five columns step 18 asks for.
`changed_files` matched the builder's own `files_changed` on every run.

**Leg 1 — shell door, issue #33 (quote `description:` in every sync), PR #59 merged 2026-10-08.**

| run | model | cost | time | note |
|---|---|---|---|---|
| builder, stale #17 | sonnet | $0.73 | 229 s | a GitHub rate-limit error had become `## Task`; honest empty diff |
| builder, #33 | sonnet | $1.82 | 531 s | 6 files, left uncommitted |
| code-reviewer, blind | opus | $0.85 | 106 s | 2 findings, both NOTES prose, both real |
| builder, fix 1 | sonnet | $0.69 | 156 s | NOTES only |
| code-reviewer, re-review | opus | $0.81 | 101 s | 3 findings, same paragraph, all real |
| tester | sonnet | $0.62 | 176 s | 733 green, 9 goldens re-recorded, mutation check |
| builder, fix 2 | sonnet | $0.35 | 67 s | NOTES only; no third review by the operator's decision |

$5.87 and 23 minutes of agent time for 51 lines plus tests. The blind gate refused a brief with
`## Context` in under a second, exit 2.

**Leg 2 — MCP door, host architect (this Claude session), issue #30 (tier ladders), PR #65.**

| run | model | cost | time | note |
|---|---|---|---|---|
| builder | sonnet | $0.47 | 154 s | 11 files, both parts of #30 |
| code-reviewer, blind | opus | $0.76 | 81 s | 1 medium finding: class-level vs emitted model, real |
| builder, fix | sonnet | $0.36 | 107 s | per-harness `sameModel` |
| code-reviewer, re-review | opus | $0.87 | 92 s | clean |
| tester | sonnet | $0.59 | 200 s | 756 green, 2 goldens, TierLadderTests |

$3.05, under eleven minutes. `delegate_async` plus a wait on the job's `result.json` was the
reliable shape; no host tool-call timeout was hit.

**Leg 3 — `claustrum coordinate --cast spawned --issues 32`, unattended, job
`20261008-142701-1c8b462a`.** `status: success` in 704 s. The spawned opus architect ran builder
($0.30) → blind review ($0.80, three low real findings) → fix ($0.25) → re-review ($0.83, clean) →
tester ($0.39, 759 green), left three commits on `claustrum/20261008-142701-1c8b462a` with
`Closes #32`, pushed nothing, and ended with the mandatory report naming every job id. Its own run
cost $1.61; the ledger (`jobs budget`) reads $4.18 spent, $0.00 reserved, and agrees with the five
receipts to the cent. The architect also triaged the review (sent two findings back, left one that
touched a file with the owner's uncommitted edits), re-ran the gate itself when the tester's
process died, and declined to open issues on its own — all as its text says.

**What broke or surprised, each an issue on the board:** no role commits its work, the operator did
(#61); `result.json` on disk lacks `worktree`/`branch` that `job_result` returns (#60); a builder ran
`git checkout` in the operator's main checkout (#62), and the spawned architect works there by
design, so the checkout is on its branch when `coordinate` returns; a remediation builder cannot
resume on the first builder's branch (#63); a tester's cleanup ran `rm -rf /tmp/tmp.*` (#64);
`CoordinateEndToEndTests` fail under an inherited `CLAUSTRUM_PARENT_JOB`, so a delegated gate inside
a tree is red for the wrong reason (#66); the cast loader accepts a nameless cast and a `null` role
is honoured only by the architect (#67); the brief in `claude -p`'s argv let a `pgrep -f` from the
brief match the job's own process (#68). One thing stays unexplained: leg 3's tester child exited
137 after writing a complete green report — no `pkill` in the architect's log, no kernel OOM kill
in the journal — and the runner kept the report and marked the job `failed`, which is the right
answer for a kill of unknown origin. Not proved by this drive: opencode and `api` (no key on this
box), ui-reviewer and demo-author (nothing rendered), copilot and cursor on a real brief (leg 1
step 11 skipped), `coordinate` through the MCP door, and every Windows leg.

**The pattern worth keeping:** every builder was right on code and loose on prose, and every blind
review caught the prose — three rounds on one NOTES paragraph in leg 1, one on the `sameModel`
mechanism in leg 2 — which is the receipt-versus-essay rule measured from the other side.

## Docs vs code: which side moved for each of #54's thirteen (2026-10-03, issue #54)

The rule for #54 was that the code is what runs. A doc claim moved to match the code, except where
the code had dropped something the design wanted and the fix was a line or two. Three items moved
the code:

- **`source=local` (item 1).** `LoadedRole.IsLocalOverride` was computed and tested, but nothing
  read it. `SyncWriter.Write` now stamps ` source=local` before `sha256=`, but only for an
  overridden role's agent and tier-stub files. Library files keep the old marker, so no consumer
  repo's `sync --check` turns stale. `roles show` prints `source:`. A local `parts/` file now
  counts as an override as well, since it changes the body just as `ROLE.md` does. This was also
  item 4 of #50.
- **A malformed local `role.json` (also observed).** It used to reach `ExceptionBoundary` as a
  bare `JsonException`, which printed `error: …` and exited 1. It is now a `RoleRenderException`
  that names the file and exits 2, the same treatment `CastStore.Load` and `Config.ReadDocument`
  already gave their files.
- **Claude `readonly` ignored `deny` (also observed).** For `edit` this is moot, because that
  level withholds Bash outright. For `readonly` it was not: `Bash(gh pr *)` in `ReadOnlyTools`
  also allows `gh pr merge`, and only a deny can take that back. `ReadOnlyArgs` uses PR #42's
  `BashDenyRules`, the same word-boundary rules as `shell` and `edit+shell`.

Main fixed items 4, 5 and 6 first. #32 made `cast show` and the questionnaire count honest, and
#42 added the demo-author everywhere. On the 2026-10-08 rebase those MANUAL, README and TEST-DRIVE
hunks took main's text. Everything else was a doc fix. The ones that are design, not drift:

- **Item 2.** `## Report format` sits second, not last, because a model skipped it in last place
  (`RoleRenderer.ComposeSystemBody`).
- **Item 3.** `.claustrum/roles` and `.claustrum/casts` resolve from the working directory, not
  the git root. #49 proposes the git root.
- **Item 11.** `init` always syncs claude, because that sync owns `.mcp.json` and
  `.vscode/mcp.json` (§"claustrum init").

Left open as their own issues: the api backend's unreadable house rules (#55), and
`backends.<name>.injection`, which nothing reads (#56).

## M4 wave 1: a delegate's work lands on its branch, and only there (2026-10-08, issues #60 #61 #62 #63 #58 #68 #57)

The test drive ("The first real tasks") left every isolated builder's branch empty, the receipt on
disk without its branch, and the operator's checkout switched by a delegate. This is the runner's
half of the fix; the role text (#64) and the test hygiene (#66, #47) are other slices of the wave.

### The receipt on disk names the branch (#60)

`DelegateEngine.RunIsolatedAsync` added `worktree`/`branch` with a `with { … }` on the result
`Runner` returned — after `FinishAsync` had already written `result.json`. The live MCP `job_result`
had both; `jobs show` and the file did not (builder jobs `20261008-140548-3eb10efc` and
`20261008-141126-3e4c44be`). Fixed at the root: `RunOptions.Worktree` (a `JobWorktreeInfo`) goes
into `Runner`, and `FinishAsync`, the one funnel every result leaves through, stamps both before it
writes. `RefuseAsync` passes null, because a refusal never has a worktree. Every result of an
isolated run carries them, a `backend_missing` after the worktree was cut included. The engine's
`with` is gone, so there is one writer of the two fields.

### The runner commits, not the role (#61)

#61 offered two fixes: role text saying "commit, never push" when isolated, or the runner
committing after the snapshot. **The runner commits.** The roles stay harness-neutral and no model
can forget; git is already the runner's source of truth for `changed_files`; and the branch then
holds the work, so the architect's rebase has something to rebase and `jobs clean` has nothing left
to destroy (since review F3 it no longer forces at all). A role may still commit by itself (the
trailer below says so), and the receipt records that too.

How: after the after-snapshot (taken only once `JobWorktree.VerifyAsync` has passed, since review
R5) and `ReportExtractor.Extract`, `JobWorktree.CommitRunAsync` checks the directory is still the
job's worktree (review F6, below), then runs `git status --porcelain --untracked-files=all` (the flag
since review R2); if anything is listed, `git add -A` then `git commit -q -m` — both, since #74,
without `.claustrum/{worktrees,briefs,locks}` and never committing a new gitlink ("coordinate runs
the architect in its own worktree", 1c). The
subject is `claustrum <role> <job id>`; a report with a non-empty `summary` string adds a blank line
and that summary as the body. The repo's own identity and hooks apply: no `--no-verify`, no
`-c user.*`, no `--no-gpg-sign`. It runs **for every status once the backend process ran** —
failed, timeout and cancelled too. Partial work on the job's own branch is worth more than partial
work left in a worktree directory, and `status` still says what happened.

- **The before-sha/after-sha rule.** `commit` is the branch's tip after the commit step when it
  differs from `JobWorktreeInfo.BaseCommit`, the commit the worktree was created on, else null (as
  first shipped: the worktree's `HEAD` before and after; the tip is read from `refs/heads/<branch>`
  in the main checkout since review F6). So a role that committed by itself and left nothing gets
  its own commit recorded, a run that changed nothing gets null, and a refused commit with no
  commit of the role's gets null plus a warning. ⚠ A refused commit *after* the role had committed some work
  itself reports the role's commit **and** the warning. The brief said "leave `commit` null" for a
  failed commit; that was read as "never invent one", and the tip the role made is real.
- **A commit git refuses costs a warning, never the run**: `work left uncommitted on <branch>:
  <first line git printed>` — "Author identity unknown", a hook's first line. `CommitAllAsync`
  returns git's reason instead of throwing, and a git that cannot be spawned or passes its bound
  (timeout/IO/`InvalidOperationException`/`Win32Exception`) is caught too — around the whole
  `CommitRunAsync` in `Runner.CommitWorkAsync` as first shipped, per stage inside `CommitRunAsync`
  since review R4 (below). Without that, a git that could not even be spawned would hit the
  catch-all after the process ran and turn a parsed report into a `Failed` `FailureResult`.
- **The commit gets five minutes, not git's 30 s.** `git commit` runs the repo's hooks, and a .NET
  pre-commit (format, build) can pass 30 s; the old bound would kill it mid-way and leave the work
  uncommitted for a reason nobody chose. Since review R4 `git add -A` gets the same five minutes:
  clean filters (git-lfs) run inside it. `GitProcess.RunAsync` gained a timeout overload for these
  two calls; since review T1 `worktree add`, whose checkout runs smudge filters and the post-checkout
  hook, and the undo of a failed one use it too. ⚠ A `commit.gpgsign` repo whose pinentry wants a
  GUI waits up to that bound and then warns. Not overridden on purpose: the signing policy is the
  repo's. ⚠ The commit ignores the run's cancellation token, like the after-snapshot does, so a
  Ctrl+C'd run still waits for its hooks.
- **`changed_files`/`diff` were the pre-commit snapshot as first shipped**, captured before the
  commit against the `HEAD` the worktree had then. A role that commits **by itself** moves `HEAD`,
  and `git status`/`git diff HEAD` then no longer show what it committed: those files were missing
  from `changed_files` while `commit` named the tip, and the blind reviewer's `## Diff` came out
  empty. Review F4 changed it: "An isolated run's receipt is the branch's delta from its starting
  commit", below.
- ⚠ `git add -A` commits whatever `git status` lists — the same set `changed_files` lists. A file the
  repo does not ignore (a stray `.env`, build output) lands on the branch; the repo's `.gitignore` is
  the filter, as it already was for the receipt. (Since #74 Claustrum's own `.claustrum/{worktrees,
  briefs,locks}` are excluded from the commit but not from `changed_files`, which can then list a
  machinery path the commit does not hold.)

### Delegates may not touch the main checkout (#62)

The reflog of the operator's checkout gained `checkout: moving from drive/issue-17 to
claustrum/20261008-140548-3eb10efc` while the only process running was the remediation builder,
whose cwd was its own worktree. That worktree sits under `<repo>/.claustrum/worktrees/`, so the
operator's checkout is three `cd ..` away and nothing on disk stops it. Of #62's three options, two
layers shipped, both for isolated runs only:

1. **The trailer** (`Runner.AppendIsolationTrailer`). It is appended to the user prompt after the
   blind gate and before the report trailer, which keeps the last position (the one
   `AppendReportTrailer`'s tester report found most honoured). It names the worktree, its branch
   and the main checkout; forbids cd-ing there, `git checkout`/`switch`/`reset`/`stash` there and
   removing the worktree; and says leftovers are committed for the role, which may commit itself
   and never push. It is harness-neutral, so cursor and api get the same words. It goes after the blind gate
   because it is runner text, not the caller's brief (and it has no `##` heading anyway).
2. **The deny** (`JobWorktree.IsolationDeny` = `git checkout`, `git switch`). `DelegateEngine.
   IsolatedRole` unions it into the `ResolvedRole` the isolated path hands `Runner`. It never enters
   the role library's lists, so an in-place run is unchanged. claude gets `Bash(git checkout)` +
   `Bash(git checkout *)` in `--disallowedTools`, opencode a bash deny pattern, copilot
   `--deny-tool "shell(git checkout)"`, and cursor a prompt rule, advisory like every cursor deny.
   ⚠ Only at the levels that apply a deny list at all: `full` skips every check on all three, and
   `edit` (and `readonly` outside claude) withholds the shell outright (MANUAL §13's table).

**What the deny does not cover.** It is a guard on how a command is written (MANUAL §13). A prefix
rule does not match `git -C /path/to/checkout checkout main`, `git -c k=v checkout`, an alias,
`sh -c`, or a script; the trailer is the only layer there. `git reset` and `git stash` are named in
the trailer only: a deny would take them away inside the worktree too, where they are legitimate.
`git checkout -- <file>` inside the worktree is collateral, so the trailer points at `git restore`.
⚠ A run that switches its *own* worktree to another branch anyway (via `git -C`) gets no runner
commit: review F6's check finds `HEAD` on the other branch, skips the commit and warns, and its
receipt has no `commit`. Review R5 had emptied its changes too; since review T3 they are the snapshot
read inside that worktree. (As first shipped, the commit landed on that other branch under the
receipt's original `branch`.)
The third option, a worktree outside the repository tree, was not taken. It moves what `jobs
clean`, `init`'s gitignore and the operator already look at, and a `cd` to an absolute path defeats
it just the same.

### `--branch`: an isolated run on an existing branch (#63)

`DelegateRequest.Branch`, from `run --branch <name>` or MCP `branch` on `delegate`/`delegate_async`
(empty is "not given"). A request with a branch is always isolated; it is gated only when its cast
gives a numeric `max_parallel`, like any other. The worktree is `git worktree add <path> <name>`,
no `-b`. The receipt's `branch` is the given name and `worktree` the new path, and the runner's
commit lands on that branch, so a remediation builder continues where the reviewed diff lives.

- **Existence is `git show-ref --verify refs/heads/<name>`, checked before the gate.** ⚠ Measured on
  git 2.56: `git rev-parse --verify refs/heads/main~1` **succeeds**, because rev-parse resolves
  revision syntax under the prefix. `git worktree add <path> main~1` then checks out a detached
  `HEAD`, and the run would commit onto no branch. `show-ref --verify` takes exact ref names only
  (`main~1` → exit 1). The *short* name is what reaches `worktree add`: `refs/heads/<name>` there is
  a detached checkout too. An unknown name never reaches git's DWIM either, which would quietly cut
  a local branch from a same-named remote-tracking one. Before the gate, so a typo is answered at
  once and not after a wait for a slot: `status: failed` through `Runner.RefuseAsync`, error
  `--branch <name>: no local branch of that name in <cwd>`, no worktree, no slot.
- **One branch, one worktree.** git refuses `worktree add` on a branch checked out elsewhere
  (`fatal: 'x' is already used by worktree at '…'`, exit 128, nothing created — measured).
  `JobWorktree.CheckOutBranchAsync` (its private `FreeBranchAsync`) finds the holder in `git
  worktree list --porcelain` (line mode, because `-z` needs git 2.36) and decides:
  - the holder is `<cwd>/.claustrum/worktrees/<id>` and job `<id>` has its `result.json` under
    this `CLAUSTRUM_HOME` (`JobDirectory.HasResult`, review R3 below; review F11 had shipped `jobs
    clean`'s wider `IsFinished` here) — that worktree is removed, then the add runs. This is the
    brief's "retry once": freed first, then added. A sibling grabbing the branch in between was an
    exception as first shipped and a receipt since F11; since R1 it cannot happen, because every
    `--branch` run holds its branch's lock from before the free until its result is written;
  - anything else is refused with the holder named: the main checkout (the architect's work branch
    is checked out there), an unfinished job (its directory, without `result.json`), a job this
    `CLAUSTRUM_HOME` has no directory for (R3), a worktree made by hand. **Foreign worktrees are
    never removed**: Claustrum did not make them and cannot know what is in them.
  - ⚠ That removal is **not** `--force` (and since review F3 neither is `jobs clean`'s). A
    finished job whose commit failed still holds its work there uncommitted. `git worktree
    remove` refuses a dirty worktree but not one holding only ignored files (both measured), and
    the refusal says to commit or discard first. Since review R2 Claustrum's own `git status
    --untracked-files=all` runs before it: under `status.showUntrackedFiles=no`, git's check lets a
    worktree whose only changes are new files go.
  - ⚠ "Is this our worktree" goes through git. git lists a worktree by its realpath — measured: one
    added through a symlinked cwd is listed under the link's target — so `<cwd>/.claustrum/
    worktrees/<id>` is compared through `git -C <it> rev-parse --show-toplevel`, never as typed. A
    symlinked cwd would otherwise make our own finished worktree look foreign. The same reasoning
    covers Windows 8.3 `%TEMP%` paths; that half is unmeasured.
  - This refusal comes after admission, so it closes the reservation at $0 through Runner's funnel:
    `RefuseAsync` gained an optional reservation, which it also releases (review F10). Otherwise the
    next admission would read the entry as abandoned.
- **Cleanup never deletes a branch it did not create.** `JobWorktreeInfo.OwnsBranch` is false for a
  `--branch` run, and `TryRemoveAbandonedAsync` runs `branch -D` only for a `-b` worktree — and,
  since review F9, only while that branch is still where it started.
- `jobs clean` names the branch it kept from the receipt's `branch`, falling back to
  `claustrum/<id>` for a receipt written before #60.

### `max_parallel` is a cap at every value (#58)

The gate used to run only on the isolated path (`> 1`). At 1, two `claustrum run builder`
processes edited one tree at once, while the architect's text promised "extra jobs wait for a
slot". `DelegateEngine.RunAsync` now routes: a branch or a cap above 1 → isolated, gated when
numeric; a numeric cap of 1 or more otherwise → in place, gated; null → in place, ungated, as
before. The in-place gated path **does not pre-admit**. Pre-admission exists only because isolation
cuts a branch before `Runner` runs; here `Runner` admits itself under the held slot, which keeps "a
reservation never waits behind the gate". It mints a job directory only for the gate-timeout
refusal, so CLI `run` still mints after the blind gate. The slot pool is the same `<cast>__<role>`
the isolated path uses. A cap below 1 (only a hand-edited cast) is no cap, as before. The
questionnaire's prompt now says "1 keeps every run in the repo itself, one at a time", and its
answer `1` is stored as `1` (review F1, below).

### The claude brief goes on stdin (#68)

Measured once before relying on it — 2026-10-08, `claude` 2.1.293 (Claude Code), Linux, from a
scratch directory, environment reduced with `env -i` to names Claustrum's allow-list forwards
(`PATH HOME LANG SHELL TERM XDG_* NODE_*`):
`printf 'reply OK' | claude -p --model haiku --output-format json --max-budget-usd 0.05
--no-session-persistence` → exit 0, `is_error: false`, `subtype: "success"`, `result: "OK"`,
`num_turns: 1`, `total_cost_usd: 0.00693753` (`haiku` resolved to `claude-haiku-5-5`; 34,133
cache-creation and 10,873 cache-read input tokens), 976 ms. `--no-session-persistence` was added to
the planned command because Claustrum always passes it on a run that does not resume. Only the
positional moved; the flags are what they were. Not measured: the full shipped argv with stdin.

`--resume` with the prompt on stdin, measured the same day, same `claude` and environment, from a
scratch directory: first without `--no-session-persistence`, so a session existed — `printf 'reply
OK' | claude -p --model haiku --output-format json --max-budget-usd 0.05` → exit 0, `is_error:
false`, `result: "OK"`, `total_cost_usd: 0.00756673`, `session_id` `a23cfd98-…`; then `printf 'reply
OK again' | claude -p --model haiku --output-format json --max-budget-usd 0.05 --resume a23cfd98-…`
→ exit 0, `is_error: false`, `subtype: "success"`, `result: "OK"`, the same `session_id`,
`total_cost_usd: 0.01464706`, 903 ms. The session transcript held both user turns, `reply OK` and
`reply OK again`, so the resumed run read its prompt from stdin. A resumed run stays on stdin too;
no argv fallback. $0.0222 in all; the transcript and its project directory were deleted after.

`ClaudeBackend.Build` drops the positional brief and sets `ProcessSpec.StdinText`; `ProcessRunner`
writes and closes stdin inside the run's timeout, the path cursor already used.
`--append-system-prompt-file` stays. A brief now shows in no process listing (the `pgrep -f`
pattern a tester's brief quoted matched its own job, PID 450228), and it no longer counts against
Windows' 32,767-character command line. ⚠ The argv tests that kept the variadic `--disallowedTools`
from swallowing the brief now guard nothing, because no positional follows. opencode (`run …
"<prompt>"`) and copilot (`-p "<prompt>"`) still put the brief on argv. Out of scope here; copilot's
is the follow-up docs/PLAN.md "M4 waves" files.

### `CLAUDE_CONFIG_DIR` by exact name (#57)

`CLAUDE_CONFIG_DIR` joins `EnvAllowList.exactNames` by exact name, not as a `CLAUDE_` prefix.
Counted in a Claude Code session's environment on 2026-10-08: 28 names start with `CLAUDE_`, 23 of
them `CLAUDE_CODE_*` — the session id, `CLAUDE_CODE_CHILD_SESSION`, `CLAUDE_CODE_ENTRYPOINT`, and a
`CLAUDE_CODE_MESSAGING_TOKEN`. A prefix would hand a delegated claude a parent session's identity
and a credential. `backends doctor` prints `config dir: <path> (CLAUDE_CONFIG_DIR)` or `config dir:
default (~/.claude; CLAUDE_CONFIG_DIR not set)` under claude, without `--probe`: reading a variable
is free, and the value is a path, not a secret.

### An isolated run's receipt is the branch's delta from its starting commit (review F4, 2026-10-08)

As first shipped, a role that committed by itself got `changed_files: []` and `diff: null`: the
snapshot compares against the worktree's `HEAD`, which the role had moved. Now, whenever the branch
tip differs from `JobWorktreeInfo.BaseCommit` after the runner's commit step,
`WorktreeSnapshot.BranchDeltaAsync` replaces the snapshot:

- `changed_files` = `git diff --name-status --no-renames -z <base> --` — the working tree against the
  starting commit, so `<base>..HEAD` and any tracked change still uncommitted — plus the snapshot's
  new files that `git ls-files --others --exclude-standard` still lists as untracked: `git diff
  <commit>` sees the index only, and a file the runner's commit could not take stays untracked. The
  review named the union of the snapshot and `<base>..HEAD`; this is that union as git computes it,
  without the union's contradictions (a file the role committed as added and then deleted would be
  listed both ways; git lists nothing, and `diff` shows nothing for it either). `--no-renames` has
  git split a rename into the A + D pair the snapshot produces.
- `diff` = `git diff <base> --` plus the snapshot's `--no-index` text for those untracked files,
  under the same byte cap and `diff_truncated` rule.
- In the common case — the runner commits what the role left — delta and snapshot list the same
  files. When the branch did not move (nothing to commit, or a refused commit and no commit of the
  role's), the snapshot is already relative to `<base>` and is kept exactly. Non-isolated runs never
  get here. A delta git cannot produce keeps the snapshot and warns `receipt delta unavailable,
  snapshot kept: …` (as first shipped: `changed_files/diff may miss what was committed on <branch>:
  …`; review R4, below).
- `BaseCommit` is fixed when the worktree is made, not read by `Runner` (whose `headBefore` read is
  gone): `AddAsync` resolves `HEAD` first and hands that sha to `worktree add -b`, so it is exactly
  where the branch starts; `CheckOutBranchAsync` reads `refs/heads/<name>` just before its add, once
  nothing holds the branch. The same field decides F9's cleanup.

### The runner commits only in the job's worktree (review F6, 2026-10-08)

git finds a repository by walking up from its cwd. A `.claustrum/worktrees/<job>` whose worktree was
removed while the job ran — `jobs clean` under a different `CLAUSTRUM_HOME` takes a missing job
directory for a finished job — and that the role's next write recreated is a plain directory inside
the main checkout, and `git add -A` there committed the operator's dirty files onto the operator's
branch (reproduced in review). `JobWorktree.VerifyAsync` now gates the commit, the F4 delta, F9's
cleanup and, since review R5, the after-snapshot (since review T3 only when the path is no worktree
of the job's): `git rev-parse --show-prefix` must be empty (the path is the top of a working tree)
and `git symbolic-ref -q HEAD` must be `refs/heads/<branch>`.
Measured on git 2.56: empty at a worktree root; `.claustrum/worktrees/j1/` in the recreated
directory, whose `HEAD` was the main checkout's `refs/heads/main`.

- ⚠ Not a path compare against `git rev-parse --show-toplevel`, which the review suggested: git prints
  realpaths, and a symlinked cwd (measured for `worktree list`, under #63) or a Windows 8.3 `%TEMP%`
  — the CI runner's — would never equal the path as typed, so every commit would be skipped. git
  computes the prefix from one cwd, which needs no normalising.
- A mismatch skips the commit and the delta and warns `work left uncommitted: <path> is not the job
  worktree on <branch> (<what git found>)` — since review T3, `… <path> is on <a detached HEAD |
  refs/heads/X>, not <branch>` when the path is still the top of a working tree. As first shipped
  `commit` was still read, from `refs/heads/<branch>` in the main checkout, so commits the role made
  before the removal were recorded; since review R5 it is null and the changes are empty too ("An
  unverified worktree reports nothing", below) — since T3 only where the path is no worktree of the
  job's.

### `jobs clean` never forces, and integration frees the branch first (review F3, F5, 2026-10-08)

- `JobWorktree.RemoveAsync` is a plain `git worktree remove`, the same private `TryRemoveAsync` that
  frees a `--branch`. A finished worktree is clean now that the runner commits, and goes; one git
  refuses — or, since review R2, one Claustrum's own `git status --untracked-files=all` lists
  anything in — is printed `could not remove <job>: <path> left in place: <reason>`, and the sweep
  exits 1. docs/PLAN.md "M4 waves" promised "`jobs clean` destroys nothing", and with `--force` the
  next sweep destroyed exactly the work #61's warning pointed at. ⚠ Measured on git 2.56: a worktree
  whose role ran `git submodule update --init` is refused as well (`fatal: working trees containing
  submodules cannot be moved or removed`), clean or not; it is removed by hand. Ignored files
  (`bin/`, `obj/`) do not block a plain remove.
- `--force` is left in two places: `TryRemoveAbandonedAsync`, only when nothing of the run's can be
  in the worktree (F9, below), and, since review T1, `--force --force` in the undo of a `worktree add`
  that failed — before the role ever ran.
- The architect's text stops saying "nothing to commit on its behalf" unconditionally: a receipt
  warning `work left uncommitted on <branch>` means committing inside that worktree before
  integrating. `roles/architect/ROLE.md` and `CoordinationBrief.RenderSystemAppendix` carry it in the
  same words — two copies, the trap "`budget_exceeded` is four messages" names.
- Integration: a builder's branch stays checked out in its worktree until `jobs clean`, and `git
  rebase <work> claustrum/<job>` from the main checkout exits 128 with `fatal: 'claustrum/<job>' is
  already used by worktree at '…'` (measured, git 2.56), while `git -C <worktree> rebase <work>`
  exits 0. Both texts, MANUAL §9 and TEST-DRIVE step 13 say: `claustrum jobs clean` first (finished
  worktrees go, branches stay), or rebase inside the worktree.
- Human-mode `run` prints `branch:`, `worktree:` and `commit:` when set, and every warning on stderr
  as `warning: …`: a refused commit used to be visible only under `--json`.

### Refusals and cleanups keep the branch's work (review F7, F9, F10, F11, 2026-10-08)

- **F7: a blind-gate rejection mints nothing, on every path.** `--branch` and the gated paths minted
  the job directory — and for `--branch` freed the old worktree and cut a new one — before
  `Runner.EnsureBlindGate` ran. `Runner.Validate` (the timeout, the brief with its attachments, the
  blind gate: the private `ValidatedBrief` that `RunCoreAsync` itself starts with) now runs in
  `DelegateEngine.RunAsync` before either path. Also when the caller handed in a job
  (`delegate_async`) — a small widening of the review's ask: nothing is minted there anyway, but the
  worktree churn is saved. `RunCoreAsync` keeps its own checks as the last line.
- **F9: an abandoned run's cleanup keeps a moved branch.** When `FinishAsync` throws after the run
  (an unwritable `result.json`), the engine's catch force-removed the worktree and `branch -D`'d an
  owned branch — with the commit the runner had just made. `TryRemoveAbandonedAsync` now forces, and
  deletes an owned branch, only when the worktree is untouched: `VerifyAsync` passes, the branch is
  still at `BaseCommit`, `git status --untracked-files=all` is empty (the flag since review R2).
  Otherwise the branch stays and the remove is plain; a refusal is returned and the worktree stays.
  ⚠ The empty-status condition goes past the review's "branch moved → keep it": an unmoved branch
  with a dirty worktree (a refused commit, then an unwritable `result.json`) has the run's work in
  the worktree only.
- **F10: a refusal releases its reservation.** `Runner.RefuseAsync` owns the reservation it is handed
  (`await using`): `CompleteAsync` releases the `.live` handle only after a ledger write that
  succeeded, `ChargeAsync` swallows a failed one, and in the long-lived MCP server nothing else
  disposed it.
- **F11: every `--branch` refusal is a receipt.** `JobWorktree.CheckOutBranchAsync` frees and adds in
  one call and throws `BranchRefusedException` for every refusal, git's own included (a branch
  mid-rebase in another worktree; a sibling run that took it a moment earlier, until review R1's
  branch lock); the engine turns it into `status: failed` through `RefuseAsync`. `worktree add` runs
  with `-q`: without it git's first stderr line is `Preparing worktree (checking out 'x')`, not the
  reason (measured). ⚠ A failed `worktree add` can still leave a whole worktree — a post-checkout
  hook exiting 3 does (measured: exit 3, worktree registered and checked out) — so what the add made
  is undone before the refusal: through `TryRemoveAbandonedAsync` as F11 shipped, through its own
  `-f -f` undo since review T1 (below). `AddAsync` (the `-b` path) shares that undo and still
  throws. As F11 shipped, "finished" was one predicate for `jobs clean` and `--branch`,
  `JobDirectory.IsFinished`, under which a job directory gone while the jobs root exists freed the
  branch too; review R3 split the two, below. "has not finished (no result.json)" is still said
  only of a directory without one.

### The questionnaire's `max_parallel: 1` is a cap (review F1, 2026-10-08)

`CastBuilder` stored the answer `1` as null, "so `cast show` does not imply a setting the user never
made" — but since #58 only a numeric value is gated, so every cast made by `cast create`/`cast
new`/MCP `cast_create` with the default answer had no cap and no gate. `1` is kept as `1`; null still
means "no cap", as for a hand-written cast without the key. ⚠ Casts created with the answer `1`
before 2026-10-08 carry null on disk and stay ungated until `"max_parallel": 1` is added back to
their builder entry (or the cast is created again). As F1 shipped, the coordinate appendix still
printed `max_parallel 1` for a null entry, read as the right fan-out instruction for the architect
even though nothing gates it; review R6 (below) made it say the entry is not set.

### One `--branch` run per branch at a time (review R1, 2026-10-08)

Two concurrent `--branch` runs on one branch both got a worktree on it. git takes no lock between
its "already checked out?" check and the add: measured by the reviewer on git 2.56, a plain `git
worktree add <p> feat` run twice in parallel succeeded 35 times in 40, and Claustrum's free-then-add
sequence 14 in 30. Run A's runner then commits first; in B's worktree the index still matches the
old tree while `HEAD` is A's commit, so B's `add -A` + `commit` **reverts A's work**, and B's
receipt (the delta from the shared base) hides it. Nothing serialised this when the cast's
`max_parallel` is null or 2 and up — and wave 2's drive is `max_parallel: 2` with a remediation round.

- **The fix is a lock per branch for the whole run.** `DelegateEngine.RunIsolatedAsync` takes an
  exclusive `RoleConcurrencyGate` (cap 1) keyed `JobWorktree.LockKeyFor(branch)` before
  `CheckOutBranchAsync` frees and adds, and releases it through `await using` once the run's result
  is written — the abandoned-worktree cleanup included. The key is `branch@<readable>@<hash>`:
  the name's first 64 characters with anything but ASCII letters, digits, `-` and `.` turned into
  `_`, then the first 8 hex digits of the SHA-256 of the exact name. `@` never survives
  `RoleConcurrencyGate.KeyFor`, so no `<cast>__<role>` pool can share the file; the hash keeps `a/b`
  and `a_b` apart and bounds the file name of a long branch.
- **Order: the cast slot (if any), then the branch lock, then admission.** One fixed order means no
  run holds what another waits for, and a reservation still never waits behind either. ⚠ A run
  waiting for its branch holds its cast slot meanwhile: under `max_parallel: 2` a queued remediation
  run occupies one of the two. Taking the branch lock first would not, and is just as deadlock-free;
  the order is the one the review asked for.
- **The wait is the run's `--timeout`**, as for a slot. Past it the run comes back `status: failed`,
  error `--branch <name>: another run on this branch outlasted the wait — all 1 'branch@…' slots
  under '…' stayed unavailable …`, with nothing to undo. A run gated twice waits for each separately,
  so it can wait up to twice its `--timeout` before it starts.
- ⚠ The lock lives under `<cwd>/.claustrum/locks/`, as the slot pools do: two runs on one branch
  started with different `--cwd`s of the same repository (a subdirectory, another worktree) do not
  see each other's lock. The coordinate appendix gives every delegation one `--cwd`.
- `IsSameWorktreeAsync` (F11's "is this our worktree") started git in a directory it had just seen
  exist; a `jobs clean` removing it in between made `Process.Start` throw `Win32Exception` out of
  `RunIsolatedAsync`. It is caught there and read as "not ours": refused, with the holder named.

### New files count whatever `status.showUntrackedFiles` says (review R2, 2026-10-08)

`CommitAllAsync` and `IsUntouchedAsync` ran a plain `git status --porcelain`, which honours
`status.showUntrackedFiles=no` from the repo or the global config. A role that only created files
was then never committed and got no warning, while its receipt listed them as `A` (the snapshot
passes `--untracked-files=all`) beside `commit: null`; the next plain `git worktree remove` deleted
them. Measured 2026-10-08, git 2.56, under `no`: plain `--porcelain` prints nothing for a new file,
`--untracked-files=all` prints `?? new.txt`, `git add -A` stages it anyway, and `git worktree
remove` removes the worktree, new file and all, with exit 0.

- Every status in `JobWorktree` passes `--untracked-files=all` (its `statusArgs`).
- Every **plain** remove — `jobs clean`'s `RemoveAsync`, `--branch` freeing a finished worktree, F9's
  kept-branch cleanup — runs that status first (`UncommittedAsync`) and refuses `uncommitted
  changes: <first porcelain line> (and N more)` when it lists anything, so git's own check, which
  `worktree remove` runs under the user's config, is no longer the one relied on. Only at the top of
  a working tree (`rev-parse --show-prefix` empty): a stale directory that is no worktree resolves to
  the main checkout, whose changes are not its own, and `worktree remove` refuses that path by itself
  (`not a working tree`). Ignored files still do not block.
- Considered, not taken: `git -c status.showUntrackedFiles=all worktree remove`, which reaches git's
  internal status through the config environment — measured the same day, it refuses `contains
  modified or untracked files` under a `no` repo config. It would still leave the decision, and its
  reason line, to git.

### `--branch` frees only a worktree whose job it can see finished (review R3, 2026-10-08)

F11 freed a holder by `jobs clean`'s rule, `JobDirectory.IsFinished`, under which a job directory
missing while the jobs root exists counts as finished. On the `--branch` path that guard is dead:
the run's own job directory exists before `CheckOutBranchAsync`, so the root always does. Any
`.claustrum/worktrees/<id>` whose job lives under another `CLAUSTRUM_HOME` — the MCP server's
environment against the shell's, a custom home in a coordinate session — or whose directory
`jobs.keep_last` pruned counted as finished. A **running** job's clean worktree could be removed from
under it and its branch taken over, and its later writes then landed in a plain directory inside the
main checkout (F6's case).

- `FreeBranchAsync` now frees only when `<jobs root>/<id>/result.json` exists
  (`JobDirectory.HasResult`). A missing job directory is a refusal: ``--branch <name>: checked out
  in <path>, the worktree of job <id>, which is unknown under this CLAUSTRUM_HOME (<jobs root>) — if
  it is finished, run `claustrum jobs clean --cwd "<cwd>"` there first``. It replaces "which <root>
  has no record of (another CLAUSTRUM_HOME?)" and the missing-jobs-root case, which this path cannot
  reach.
- `jobs clean` keeps `IsFinished` unchanged: it is the human's explicit sweep, and without its
  missing-directory clause a pruned job's worktree would never go. Its hazard under a foreign home
  stays where F6 already catches the commit.
- ⚠ A worktree whose job `keep_last` pruned under this very home now needs one `jobs clean` before
  `--branch` can take its branch; the refusal says to run it.

### A commit that was made is never reported as left uncommitted (review R4, 2026-10-08)

`Runner.CommitWorkAsync`'s catch wrapped the whole of `CommitRunAsync`, so a failure *after* a
successful commit — `BranchTipAsync` or `BranchDeltaAsync` past git's 30 s bound on a large diff —
came out as `commit: null` and `work left uncommitted on <branch>`, and the architect was told to
commit work the branch already had.

- Each stage answers for itself inside `CommitRunAsync`. A failed status/add/commit is `work left
  uncommitted on <branch>: …`, as before. After it, a tip read that fails keeps the snapshot and
  `commit: null` and warns `receipt delta unavailable, snapshot kept: <branch>'s tip could not be
  read: …`; a delta that fails or throws keeps `commit` and the snapshot and warns `receipt delta
  unavailable, snapshot kept: …`. F4's `changed_files/diff may miss what was committed …` became the
  second of these. `Runner.CommitWorkAsync` has no catch left, and `VerifyAsync` returns a git
  failure as its mismatch reason instead of throwing.
- `git add -A` gets the commit's five minutes: clean filters (git-lfs) run inside it.
- PLAUSIBLE, unmeasured: an `add` or `commit` killed at its bound can leave `index.lock` in the
  worktree's git directory, and the architect's own commit there then fails on it until it is deleted.
- Residual, not handled: a commit killed during a slow post-commit hook has already moved the branch,
  so the receipt carries `commit` and the "left uncommitted" warning together, over a clean worktree.

### An unverified worktree reports nothing (review R5, 2026-10-08)

In F6's mismatch case the after-snapshot ran before `VerifyAsync`, so in a worktree turned back
into a plain directory `git status`/`git diff HEAD` resolved to the main checkout, and the receipt's
`changed_files`/`diff` carried the operator's uncommitted edits.

- `Runner` verifies first and, on a mismatch, takes no after-snapshot: the receipt is
  `JobWorktree.Unverified` — `changed_files: []`, `diff: null`, `commit: null` — with F6's warning.
  Since review T3 only when the path is no worktree of the job's (below).
  `commit` is no longer read from the branch, because once the worktree is gone the branch may be
  another run's (R3's takeover) and its tip says nothing about this one. `CommitRunAsync` verifies
  once more just before committing, with the same receipt on a mismatch.
- A worktree removed and *not* recreated ("the directory is gone") used to throw from the
  after-snapshot and end as `status: failed` with the report lost. It now keeps the run's status and
  report, and carries the same warning.

### The coordinate appendix says when a builder has no cap (review R6, 2026-10-08)

`CoordinationBrief.RoleLine` printed `max_parallel 1` for a builder entry with no value, right next
to "`max_parallel` is a cap at every value, 1 included". A null entry is not gated at all (#58):
every hand-written cast without the key, and every questionnaire cast made before 2026-10-08 (F1).
It now prints `max_parallel not set (no cap, no isolation — add "max_parallel": 1 to the cast to
serialise builders)`; a number prints as before. ⚠ A hand-edited value below 1 is no cap either and
still prints as its number.

### A `worktree add` that fails or is killed leaves nothing behind (review T1, 2026-10-08)

`JobWorktree.TryAddAsync` undid a `git worktree add` only when git exited non-zero. A checkout past
git's 30 s bound (a git-lfs smudge, a virus scanner on a large repository) or a cancelled run had
git's process tree killed and the exception passed straight through: `RunIsolatedAsync` had no
`worktree` yet, so its catch cleaned nothing, and `--json` printed no receipt. Reproduced on git
2.56 with a smudge filter `sleep 3; cat` and the add SIGKILLed after 2 s: the worktree stays
registered on its branch, `locked initializing`; plain `remove` and `remove --force` both refuse
(`fatal: cannot remove a locked working tree, lock reason: initializing` / `use 'remove -f -f' to
override or unlock first`); `branch -D` refuses (`used by worktree at …`); a later add of the branch
gets `already used by worktree`. `jobs clean` skips it, since that job never wrote a `result.json`,
so every later `--branch <b>` was refused "has not finished" for good. A kill during a slow
post-checkout hook (also measured) leaves the worktree registered and checked out, but unlocked:
git drops the lock before it runs the hook.

- **The add runs under the commit's five minutes** (`repoCodeTimeout`, renamed from
  `commitTimeout`): a checkout is the repository's own code too — smudge filters, the hook.
- **Every failure of the add is undone** — non-zero exit, timeout, cancellation — by `UndoAddAsync`:
  `git worktree remove --force --force <path>`, then — only if that remove failed — `git worktree
  prune`, then F9's branch rule (`TryDeleteOwnedBranchAsync`, now shared with
  `TryRemoveAbandonedAsync`): `branch -D` only for a `-b` branch still at `BaseCommit`. Measured on
  git 2.56: `-f -f` removes the `locked initializing` worktree (exit 0; the directory and
  `.git/worktrees/<id>` gone), after which `branch -D` succeeds, and a locked entry whose directory
  is gone (exit 0), as it does an unlocked one; what it fails on (exit 128, `validation failed …
  '.git' does not exist`) is a directory whose `.git` file is gone, and that entry stays registered
  `prunable` until a prune. So the prune runs only after a failed remove (narrowed in review,
  2026-10-08: it is repository-wide, and ran after every failed add). The role never ran, so forcing
  loses nobody's work; a post-checkout hook's leftovers, which F11's plain remove refused, go too.
  The undo's remove gets the same five minutes, since deleting a large tree is as slow as writing
  it. Measured as well: a `-b` add refused because its path already exists has **already created the
  branch** (exit 128, `refs/heads/<b>` at the base); the undo deletes it, F11's kept it. Unreachable
  while paths are per job id.
- **What comes back.** On the `--branch` path a timeout is a `BranchRefusedException`, so a
  `status: failed` receipt: `--branch <b>: git worktree add did not finish within 5 minutes; nothing
  left behind`, or `…; left behind: <path> (<git's reason>)` and/or `branch <b> (<reason>)`. A
  non-zero exit keeps git's reason, with `; left behind: …` only when something is (as first
  shipped: ` (and undoing it failed: …)`). The `-b` path's `AddAsync` still throws (`git worktree
  add <path> -b <b> failed: …`), exit 2 and no receipt as before, now with nothing left behind. A
  cancel runs the undo, then rethrows the `OperationCanceledException` unchanged, so `run` still
  exits 130 — unless the undo left something: then a new `OperationCanceledException` (inner: the
  original, same token) carries `git worktree add was cancelled; left behind: …`, and
  `RunCommand.Cancelled` (shared with `coordinate`) prints it as `warning: …` on stderr, still
  exiting 130. It prints any cancel whose message is not one of the framework's two defaults, so a
  plain Ctrl-C stays silent. Not on the MCP door: `McpExceptionBoundary` does not translate a
  cancel, so the message goes wherever the SDK puts a cancelled call (not measured).
- **A timeout before the add is a receipt too.** `CheckOutBranchAsync` turns a `TimeoutException`
  from the free (the finished holder's `worktree remove`), the prune or the tip read into
  `--branch <b>: <git's timeout message>`. A widening of the review's ask, which named only the add:
  the same path lost its receipt the same way.
- **prune before the holder is looked up.** A free killed mid-`worktree remove` can leave the branch
  held by an entry whose directory, or its `.git` file, is gone: `FindHolderAsync` named it,
  `IsSameWorktreeAsync` found no worktree there, and the run was refused `already checked out in
  <holder>` with advice to switch that checkout off it. `FreeBranchAsync` now runs `git worktree
  prune` first. Measured on git 2.56, prune (no `--expire`: everything stale goes at once) dropped
  exactly the entries whose `.git` file was missing — a directory gone entirely, a directory whose
  `.git` file alone was deleted (its other files stay, unregistered), and a worktree outside the
  repository whose directory was gone — and left an intact worktree and a locked one whose directory
  was gone. A concurrent run's add in progress is safe: git locks an incomplete worktree
  `initializing` so that prune skips it.
  - ⚠ prune is repository-wide. An unlocked worktree of the operator's whose directory is missing —
    on an unmounted drive, say — is unregistered by any `--branch` run or an add whose undo's remove
    failed, not three months later by `git gc`. `git worktree lock` keeps it, as git's documentation
    advises for removable media. Not narrowed to Claustrum's own entries: telling one from a stale
    foreign entry would need the realpath of a directory that no longer exists.
  - ⚠ A remove killed while the `.git` file still exists stays registered with part of its files
    gone. prune leaves it, and the free refuses it as `uncommitted changes: D …`; `git worktree
    remove --force <path>` clears it by hand.
  - ⚠ A directory prune unregistered keeps whatever files are left in it, and `jobs clean` then
    reports it (`not a working tree`) on every sweep until it is deleted by hand.

### The job's own worktree off its branch keeps its snapshot (review T3, 2026-10-08)

R5 skipped the after-snapshot on any `VerifyAsync` mismatch. When `--show-prefix` is empty — the
path *is* a worktree's top — and only `HEAD` differs, the snapshot reads that worktree and nothing
else, and the receipt lost it for no reason: a role stopped mid-rebase (a detached `HEAD`) or one
that switched its worktree's branch came back with `changed_files: []`.

- `VerifyAsync` returns a `WorktreeMismatch(Reason, OwnWorktree)` instead of a string. `OwnWorktree`
  is true for an empty prefix with `symbolic-ref -q HEAD` exiting 1 (`a detached HEAD`; measured
  mid-rebase and after `checkout --detach`, git 2.56) or naming another ref (`refs/heads/X`). A gone
  directory, a non-empty prefix and any other git failure — `symbolic-ref` exiting 128 included,
  which used to read as "detached" — are `OwnWorktree: false`.
- `Runner` snapshots when the worktree verified or is its own. `JobWorktree.Unverified(worktree,
  mismatch, snapshot)` then gives its own worktree the snapshot, `commit: null`, no runner commit,
  no F4 delta, and the warning `work left uncommitted: <path> is on <a detached HEAD | refs/heads/X>,
  not <branch>`; anything else gets R5's empty receipt and F6's warning, unchanged.
- ⚠ `commit` stays null even when the role moved its branch before leaving it (committed, then
  started a rebase). The review asked for no commit and no delta, and a `commit` beside a snapshot
  that does not contain it is F4's contradiction again; the warning sends the reader into the
  worktree, where `git log <branch>` shows it.
- ⚠ That snapshot is relative to the moved `HEAD`, not to `BaseCommit`: mid-rebase it lists the
  files in conflict, not what the rebase had already replayed.

### The remediation hint is for isolated builders (review T4, 2026-10-08)

The coordinate appendix and `roles/architect/ROLE.md` told the architect to send any reviewed branch
back with `--branch claustrum/<that job id>`. An in-place builder (`max_parallel` 1 or not set) has
no branch of its own — its work is already in the architect's working tree — so both now say the
hint is for an **isolated** builder's branch, one that ran under `max_parallel` above 1 or with
`--branch`. Both also say `--branch <the branch on that builder's receipt>` instead of `claustrum/
<that job id>`: a builder that itself ran with `--branch` reports the branch it was given, and
`claustrum/<its own job id>` does not exist, so a second remediation round would have been refused
`no local branch of that name`. MANUAL §9 and §11 follow. Two copies again — the trap
"`budget_exceeded` is four messages" names.

### Known and left as is (2026-10-08)

- Windows, unverified: a VBCSCompiler/MSBuild node left running in a finished worktree holds files
  there, and `git worktree remove` then deletes the admin entry yet exits non-zero — so
  `FreeBranchAsync`'s "commit or discard its changes" is the wrong advice for that case, and a later
  `jobs clean` says "not a working tree". The mitigation is the delegate's own `dotnet build-server
  shutdown` before it finishes (the owner's rule 5).
- At `max_parallel: 1` a nested delegation (builder → architect → builder) waits for the slot its
  grandparent holds, up to `--timeout`, then fails — before #58 a cap of 1 meant no gate — and a
  nested `--branch` run on its parent's branch waits the same way for the branch lock: bounded, not a
  deadlock, because every path takes the slot, then the branch lock, then the ledger, in that order.

## coordinate runs the architect in its own worktree (2026-10-09, issue #74)

Until now the spawned architect worked in the operator's checkout by design: the `## Coordination`
appendix told it to create `claustrum/<job id>` from `HEAD`, so the operator's checkout ended on that
branch and its uncommitted edits rode along (TEST-DRIVE step 17 said so). Wave 1 had isolated the
delegates (#62) and left the architect there.

**Decision: in a git repository the architect is an isolated run.** `DelegateRequest.Isolate`
(default false) sends `DelegateEngine.RunAsync` down the existing isolated path whatever
`MaxParallel` says, and `coordinate` sets it whenever `GitRootLocator` finds a repository for its
cwd. Nothing on that path needed a second copy: `JobWorktree.AddAsync` cuts `claustrum/<job id>` from
`HEAD` into `<cwd>/.claustrum/worktrees/<job id>`; `Runner` appends the isolation trailer, takes the
`IsolationDeny`, stamps `worktree`/`branch` and commits the leftovers (`commit` on the receipt). The
two gates on that path are no-ops for the architect: `MaxParallel` stays null, so it takes no slot,
and it is admitted only when `coordinate` itself runs inside a tree (`Options.Tree`, as before — the
admission merely happens before the worktree instead of inside `Runner`). A cwd in no repository has
no `HEAD` to branch from and keeps the in-place run — with a text that says there is no git (review
round 2, G4, below); several end-to-end tests run `coordinate` in a plain temp directory.

The decision is made once, in `CoordinateEngine.PlanAsync`, and kept on `CoordinatePlan.Isolated`, so
the request, the appendix and the user prompt cannot disagree. Measured 2026-10-09 with the built
binary against a fake `claude` (`backends.claude.path`, nothing paid): `result.json` has `worktree`
`<cwd>/.claustrum/worktrees/<id>`, `branch` `claustrum/<id>` and a `commit` holding the file the fake
wrote in its cwd; `request.json`'s `cwd` is the worktree; the operator's checkout kept its branch, its
`HEAD` and its one-line reflog, and its uncommitted edit to a tracked file is not on the branch.

### What the architect is told

`CoordinationBrief.RenderSystemAppendix(…, isolated)`: the `Delegate with:` line and the `jobs clean
--cwd` recipe name the architect's worktree (`JobWorktree.PathFor(cwd, {{job_id}})`), not the
operator's cwd, so an isolated builder's worktree nests under the architect's and is cut from the
work branch's tip. The `Work branch:` block says it is already on the branch in that worktree, that
the main checkout is the operator's, and that the runner commits whatever it leaves — so it commits
its integration itself and leaves the tree clean. Not isolated — which since #74 means no git
repository at all — the whole git block is one line instead (G4, below).

- ⚠ **Integration has one order now, everywhere.** The old text offered "`jobs clean` first, then
  rebase" — from an isolated architect's own worktree that is `git rebase <work> <builder branch>`,
  which leaves the worktree on the builder's branch, and `git checkout`/`git switch` are denied for an
  isolated run (#62): the architect has no sanctioned way back, and the runner's final commit is then
  skipped (`… is on refs/heads/claustrum/<builder>, not …`, T3). So the bullet says: rebase inside the
  builder's worktree (`git -C <worktree> rebase <work branch>`, measured to work on a nested
  worktree), fast-forward from your own working tree (`git merge --ff-only`, which no deny list
  covers), never rebase in your own, and `jobs clean --cwd …` only once integrated. A first cut gave
  only the isolated appendix that order; review F5 found `roles/architect/ROLE.md` — the same
  architect's system body — still offering the other, so ROLE.md, the isolated appendix and §9 of the
  manual all say it now, phrase for phrase where `CoordinationTextAgreementTests` pins them. It works
  for a host architect in a plain checkout too; the appendix adds the deny clause. (The in-place
  appendix carried it too until G4 replaced its git lines: in place now means no git at all.)
- The user prompt's `Working directory:` line names the worktree, token and all. `PreparedDelegation.
  ForJob` fills the token in the system prompt and the env only, and it stays that way:
  `CoordinatePlan.BindUserPrompt` replaces that one line once the job exists. The task above it is
  the caller's text or a GitHub issue's, and may quote `{{job_id}}` itself — a brief-wide fill in
  `ForJob` would also rewrite every isolated or `delegate_async` builder brief that quotes the token,
  which is exactly what a brief about this code does. Since #89 (2026-10-09) that replacement runs
  only after the last `## Context` heading (`CoordinationBrief.BindWorkingDirectory`): a whole-brief
  `string.Replace` also rewrote a task that quotes the `Working directory:` line verbatim.
- `coordinate`'s human output prints `branch:`, `worktree:` and `commit:` after `logs:`, and every
  warning on stderr as `warning: …`, like `run` (F3): a refused commit of the architect's leftovers was
  otherwise visible only under `--json`.

### The traps, and what each became

**1. A worktree sees only committed files.** The children read their cast, `claustrum.json` and
`.claustrum/roles/` from the architect's worktree, so an uncommitted one is `cast 'x' not found` or
silently the old file. Copying them in was the alternative; it would have had to keep them out of the
runner's `git add -A` and out of every receipt, for files that belong in the repository anyway. So
`ArchitectWorktree.RequireReadyAsync`, from `PlanAsync` after the cast loads and before `gh`, refuses
— `CliUsageException`, exit 2, no job directory, over both doors — with `coordinate runs the architect
in a worktree, which sees only committed files — commit (or un-ignore) <path> (<state>) first`. The
check is `git --literal-pathspecs status --porcelain -z --ignored --untracked-files=all --
.claustrum/casts/<name>.json claustrum.json .claustrum/roles` from the git root, plus the rule files
of 1b and the harness configs of 1d. Under `.claustrum/roles/` only the role files count (G1, below);
a rule file and a harness config each get their own advice, and a harness config HEAD has only warns.
Measured on git 2.56 with the plain `--porcelain --ignored -- <path>` form:

| state | output |
|---|---|
| committed and clean, or absent and untracked | nothing, exit 0 |
| modified | ` M claustrum.json` |
| staged, never committed | `A  .claustrum/casts/new.json` |
| deleted, uncommitted | ` D claustrum.json` |
| untracked | `?? .claustrum/casts/new.json`; a wholly untracked directory collapses to `?? .claustrum/roles/` |
| ignored | `!! .claustrum/casts/ign.json` |

⚠ Under `status.showUntrackedFiles=no` that plain form prints **nothing** for the untracked file *and*
for the ignored one (measured the same day) — review R2's trap again — so `--untracked-files` is
passed: `normal` at first, `all` since G1, because `normal` collapses a new role to `?? .claustrum/roles/
<role>/`, which the role-file filter cannot judge, while `all` lists each file — inside an ignored
directory too (`!! .claustrum/roles/<role>/role.json`, measured). `-z` because porcelain quotes a path with a space (`"sp ace.json"`) and octal-escapes
non-ASCII; `--literal-pathspecs` because a cast name is the caller's text.

Two refusals came with it. **The cwd must be the git root**: the worktree is the whole repository,
and children given its root as `--cwd` look for `.claustrum/casts/` there — from a subdirectory with
its own cast every delegation would fail `cast not found`, and `init`'s `.claustrum/worktrees/` rule
is anchored at the root anyway. Mapping the subdirectory into the worktree was possible but would
have changed every isolated builder's cwd too; refused instead, naming the root. **A git that fails**
(a `.git` that is not a repository, git missing, past its bound) is refused the same way: the
worktree add would fail after the mint and leave a job nothing closes.

**1b, found while building: the runner's final commit takes in what is not ignored** (until 1c). In the
architect's worktree Claustrum itself creates `.claustrum/worktrees/<child>` (nested job worktrees),
`.claustrum/locks/` (slot files) and, by the appendix's instruction, `.claustrum/briefs/`. Measured on
git 2.56: with no ignore rule, `git add -A` there prints `warning: adding embedded git repository:
.claustrum/worktrees/B` and the commit holds `160000 commit <sha> .claustrum/worktrees/B` beside the
lock file. So the same check refuses `.claustrum/worktrees/`, `.claustrum/briefs/` or
`.claustrum/locks/` unless a rule the worktree will see ignores it: `git check-ignore -v --no-index
--` on the shapes Claustrum writes (1c), and the file each rule comes from joins the committed-files
check when it is in the tree (an uncommitted `.gitignore` is refused as `.gitignore (uncommitted
changes — the worktree gets HEAD's ignore rules; commit it first)`, G1).
`.git/info/exclude` and `core.excludesFile` are shared by every worktree — measured: `check-ignore -v`
names the former `.git/info/exclude` from the main checkout and by absolute path from a linked
worktree, the latter by absolute path. A `!` pattern is a match that un-ignores. Claustrum's own
`.gitignore` had `worktrees/` and `locks/` but not `briefs/`, so `coordinate` refused in this
repository; `briefs/` was added beside them (review, 2026-10-09) — `init` writes all three, and
docs/PLAN.md §D5 calls briefs git-ignored.

**1c, review F1: the probe asked about the wrong shape, and the commit trusted the probe.** The
first cut asked `check-ignore` about a file named `probe` under each directory. A whitelist
`.gitignore` — `*`, `!*/`, `!*.json`, `!*.md`, `!*.txt`, `!.gitignore` — ignores that file through
`*`, so `coordinate` ran; but `!*/` un-ignores every directory and `!*.md` every brief, and the
runner's commit took in the nested builder worktree as a `160000` gitlink plus
`.claustrum/briefs/1-builder.md`. `jobs clean --cwd <archWt>` then left ` D .claustrum/worktrees/<id>`
behind, the architect's worktree was refused for good, and every later worktree cut from that branch
carried an empty `.claustrum/worktrees/<id>` that tripped `NestedWorktrees` (until H1, round 3). Two layers now — the
runner's commit and the probe — and the precondition kept:

- **The runner's commit never takes Claustrum's machinery, ignored or not.** `JobWorktree`'s status
  (the commit's first look, `IsUntouchedAsync`, the remove check) and `add -A` all take
  `-- . ':(exclude).claustrum/worktrees' ':(exclude).claustrum/briefs' ':(exclude).claustrum/locks'`.
  Measured on git 2.56 with nothing ignored, a nested worktree, a brief and a lock present: status
  (with and without `-z`) lists only the real file, `add` stages only it — no "embedded repository"
  warning — and under `status.showUntrackedFiles=no` the excluded `--untracked-files=all` form still
  lists new files. With those directories *ignored* and on disk, `add` still stages the rest but exits 1
  — the round-3 finding under H2, below. ⚠ `GIT_LITERAL_PATHSPECS=1` in the caller's environment turns `:(exclude)…` into a
  literal path: status then lists everything and `add` dies `pathspec ':(exclude).claustrum/worktrees'
  did not match any files` (exit 128). `git --no-literal-pathspecs` overrides the variable (measured),
  so every one of those calls starts with it — undocumented in `man git`, but in `git.c`'s option
  handling already in v2.5.0, the first git with `worktree`.
- **A gitlink is not committed unless `.gitmodules` lists it.** After the add, `git diff --cached --raw
  -z --no-renames --ignore-submodules=none` — each entry `:000000 160000 0000000 <sha> A` then the path;
  `--name-only` does not show the mode. Measured: a `git init`ed `vendor/x` with one commit stages as
  exactly that. The first cut unstaged *everything* (`git reset -q`) on one such entry and warned `work
  left uncommitted …`, so a stray clone cost the role's whole commit; since review round 2 (G2, G3,
  below) only the stray gitlinks are unstaged and the rest is committed, and a gitlink the staged
  `.gitmodules` lists — a `git submodule add` the role left staged — is committed as the submodule it is.
- **The probe asks about real shapes**: `.claustrum/worktrees/probe-<8 hex>/`, a real empty directory
  created for the question and removed after it (with `.claustrum/worktrees` itself when the probe
  made it) — git calls a path a directory only when one is on disk: measured, the same path with its
  trailing `/` but not on disk matched `*`, on disk it matched `!*/` —
  `.claustrum/briefs/1-builder.md` and `.claustrum/locks/default__builder.1.lock`. Under the
  whitelist the first two come back un-ignored (`!*/`, `!*.md`) and the lock ignored (`*`), which is
  the truth. The probe directory has no leading dot on purpose: a `.*` rule would ignore `.probe-x`
  and not a real job id. A `jobs clean` that runs in the same millisecond deletes it as a stray probe
  (G5); the probe checks it is still on disk after git answered and refuses ("… was removed while git
  was asked about it — run coordinate again") rather than trust an answer about a path git saw as no
  directory.
- **The precondition stays, with the backstop in place.** git's own plain `worktree remove` refuses
  a worktree holding an untracked brief or lock — `fatal: '…' contains modified or untracked files,
  use --force to delete it`, exit 128, measured — so unignored machinery would still leave the
  architect's worktree to `jobs clean` forever, and the architect's own `git add -A` has no
  exclusions. The refusal's tail says that now instead of "that commit takes in …". ⚠ The flip side:
  the remove check now skips the machinery too, so under `status.showUntrackedFiles=no` git's remove
  deletes an unignored brief or lock silently (measured) — Claustrum's own scratch, accepted.

**1d, review F2/F3/F4: what the status check could not see.**

- **A hidden edit.** `git update-index --skip-worktree claustrum.json` (or `--assume-unchanged`) plus a
  local edit prints nothing in `status` (measured, both bits), so the architect's `Prepare` read the
  operator's edited file while its children read HEAD's. `git --literal-pathspecs ls-files -v -z --
  <same paths>` tags skip-worktree `S`, assume-unchanged in lower case (`h`), both bits `s`, an
  unmerged path `M` (measured, git 2.56); `S`/`s` is reported as `(skip-worktree)`, any other
  lower case as `(assume-unchanged)` — refused in the same `commit (or un-ignore) …` message, except on
  a harness config, where it only warns (G1).
- **A cast typed in another case.** On a case-insensitive filesystem `--cast Spawned` loads
  `spawned.json`, while the pathspec matches case-sensitively, so an untracked cast passed. The check
  asks git about the name on disk: when the typed path opens but no directory entry has that exact
  name, the entry that matches `OrdinalIgnoreCase` is used — on a case-sensitive filesystem the typed
  name either is an entry or does not open, so nothing changes there. `claustrum.json` goes through
  the same lookup, for the same reason. Unmeasured here: this machine has no case-insensitive
  filesystem.
- **Harness config `init` wrote and nobody committed.** An untracked `.mcp.json`, `opencode.json` or
  `opencode.jsonc` refuses (`commit or git-ignore … — an ignored local copy is fine`, G1); an *ignored*
  one does not — it is the operator's local copy on purpose, and the warning below is all it gets. A
  *tracked* one with a local edit refused too until G1; it only warns now.
- ⚠ **Nothing uncommitted reaches the worktree, and a gate may need it.** Gitignored test
  prerequisites (`.env`, `*.local.json`, `node_modules/`), submodules (a fresh worktree's are not
  initialised) and uncommitted harness settings (`.claude/settings*.json`, an ignored `.mcp.json`, a
  committed one's local edit) are all missing for every child of a spawned architect, so a tester can
  fail for an environmental reason with no warning before — only the harness configs' local edits are
  warned about (G1). Refusing all of that would refuse most real repositories; MANUAL §11 says it
  instead, and a brief can say how to set it up.

**2. `jobs clean` enumerates `<cwd>/.claustrum/worktrees` only.** The nested ones are swept by
`claustrum jobs clean --cwd <architect's worktree>`, which needed nothing new: measured, `git -C
<archWt> worktree add -q <archWt>/.claustrum/worktrees/B -b claustrum/B <tip>` from inside a linked
worktree works (git 2.56), B's base is the work branch's tip, and `git -C <archWt> worktree remove
<B>` removes it. ⚠ **The other order destroyed work.** `git worktree remove <archWt>` with B inside
it — ignored, so the architect's status is clean — exits 0 and deletes B's directory, an uncommitted
file in B included, leaving B registered `prunable` and its branch still "checked out" there
(measured). Every plain remove in `JobWorktree` (`jobs clean`, `--branch` freeing a finished
worktree, F9's kept-branch cleanup) now refuses a worktree whose `.claustrum/worktrees/` holds a
directory with a `.git` of its own (any directory until H1, round 3): ``it holds job worktrees of its own under <path> — run `claustrum jobs clean --cwd "<it>"`
first``; `IsUntouchedAsync` says no for it too, so the forced cleanup cannot take it either. Measured
with the built binary: `jobs clean` from the operator's checkout removed a plain finished worktree and
left the architect's (exit 1, that message); `--cwd <archWt>` refused the child while it held an
untracked file, removed it once clean; then `jobs clean` removed the architect's. The operator's own
integration has the same constraint (review F6): from the operator's checkout `git merge --ff-only
claustrum/<id>` fast-forwards while the architect's worktree still holds the branch (measured), but
rebasing that branch is refused until both sweeps have run — TEST-DRIVE step 17 gives both paths.

**3. The `.claustrum/locks/` gate directory moves with the cwd.** Every delegation the architect
makes carries the same `--cwd`, its worktree, so its children share one gate directory there —
measured, a `max_parallel: 2` builder started that way made `<archWt>/.claustrum/locks/`, and
1b and 1c keep it out of the commit. A run started from the operator's checkout meanwhile does not see
those locks, as review R1 already records for any two `--cwd`s.

### Review round 2 (2026-10-09): G1–G5

Each measured with the built binary against a fake `claude` in a scratch repository (git 2.56,
`CLAUSTRUM_HOME` and `GIT_CONFIG_GLOBAL` pointed into the scratch directory).

**G1, the precondition refused ready repositories with the wrong advice.**

- *Junk under `.claustrum/roles/`.* An ignored `.DS_Store`, `.ROLE.md.swp` or `ROLE.md~` (a global
  excludes file) refused as `commit (or un-ignore) .claustrum/roles/.DS_Store (ignored)`. `RoleLibrary`
  reads `<role>/role.json`, `<role>/ROLE.md` and `ReadPart`'s `<role>/parts/<part>.<harness|default>.md`;
  any other file in `parts/` only flips `LoadedRole.IsLocalOverride`, which `sync` and `roles show`
  print and no run reads. So `ArchitectWorktree.IsRead` keeps, under `.claustrum/roles/`, only paths
  shaped `<role>/(role.json|ROLE.md|parts/*.md)` — the brief asked for `parts/*`; `*.md` is what
  `ReadPart` opens, and a Finder `.DS_Store` lands in `parts/` as readily as anywhere. Measured: four
  junk files and an untracked `notes.txt` ran; an untracked `tester/ROLE.md`, `tester/role.json` and
  `builder/parts/review.default.md` were all named. ⚠ Case-insensitive on every OS: on Linux that
  refuses an untracked `role.md` nothing reads, the cheaper mistake than passing the `role.md` a
  case-insensitive filesystem opens for `ROLE.md`.
- *The rule file.* Still refused for any uncommitted change — the worktree gets HEAD's rules, and
  evaluating HEAD's rules from here is not cheap — but the message says why: `.gitignore (uncommitted
  changes — the worktree gets HEAD's ignore rules; commit it first)`.
- *Harness configs.* An untracked one said "commit (or un-ignore)", while ignoring it is the way out
  the check accepts: now `commit or git-ignore .mcp.json (untracked) — an ignored local copy is fine`.
  A tracked one with a local edit — or with `skip-worktree`/`assume-unchanged`, the usual way to keep a
  token out of a commit — no longer refuses: the children get HEAD's copy, which is what a committed
  config is for. `RequireReadyAsync` returns a warning instead, `CoordinatePlan.Warnings` carries it,
  `coordinate` prints `warning: .mcp.json (<state>): the architect's worktree gets HEAD's copy — your
  local edit stays out of it` on stderr after `Prepare` and before the mint (under `--json` stdout stays
  one document), and the MCP tool returns it in `warnings` — `CoordinateStartResult`
  (`{job_id, log_path, warnings}`), a type of its own so `delegate_async`'s shape does not change. Not
  on the `RunResult`: that is the architect's receipt, written after the run. Whether HEAD has the
  path is read off the status code (`Difference.InHead`): `??`, `!!`, an index `A`/`R`/`C` or `add -N`'s
  ` A` mean it has none, and those still refuse. Measured on both doors: modified, `skip-worktree` and
  `assume-unchanged` each warned and ran; untracked refused (from MCP, as the tool's error).

**G2, `diff.ignoreSubmodules=all` hid the gitlink.** Measured: with it set, `git diff --cached --raw`
after `add -A` of a file and a `git init`ed `vendor/x` printed only the file; with
`--ignore-submodules=none`, both. The same setting hides a submodule's moved pointer from `git status
--porcelain` (nothing; ` M "my sub/lib"` with `none`, and with no setting at all — where `none` and the
default print the same), so `JobWorktree.statusArgs` passes `--ignore-submodules=none` too: the
commit's first look, `IsUntouchedAsync` and the remove check see what the default config
sees, whatever the repository says — since H4 the commit's look only decides whether to run `add` at
all; the index after it decides whether to commit, and the status after it (round 4, with the same
flag) what is warned about as left out. `git commit` commits a staged gitlink under that setting
(measured), so nothing else needed it.

**G3, one stray clone cost the role its whole commit.** `JobWorktree.StagedStrayGitlinksAsync` reads
every staged entry; a new `160000` (old mode not `160000`: an add, or a type change) whose path the
staged `.gitmodules` does not list is a stray. `git --literal-pathspecs reset -q -- <strays>` unstages
those alone, the rest is committed, and each — back in the worktree as `?? <path>/`, which is where
round 4 reads it from — costs `embedded repository at <path> left out of the commit on <branch> — move
it out or add it as a submodule`. `work left uncommitted on <branch>: …` is a refusal of git's and
nothing else: of the commit (its index kept), of every path (`git add staged nothing: …`, round 4), or
a failure before the commit, with the index cleared (H5). When the strays were all that was staged no commit is attempted — git refuses it, `nothing
added to commit`, exit 1 (measured); H4 made that the rule for any empty index. `.gitmodules` comes from the index:
`git config --blob :.gitmodules -z --get-regexp '^submodule\..*\.path$'` — `-z` ends each
`key\nvalue`, as a submodule's name and path may hold spaces; no file or no key exits 1, read as none;
it reads the linked worktree's own index (measured). End to end, `run builder` at `max_parallel 2`
under `diff.ignoreSubmodules=all`, the fake writing `real.txt` and a `git init`ed `vendor/x`: `commit`
holds `real.txt` and not `vendor/x`, one warning, `?? vendor/x/` left in the worktree; `vendor/x`
alone: no commit, that warning only; plus a `git submodule add`ed `deps/lib`: `.gitmodules` and
`160000 deps/lib` committed, `vendor/x` left out. `jobs clean` refuses a worktree still holding a
left-out repository as uncommitted (`?? vendor/x/`), like any leftover. The appendix's three `work
left uncommitted` shapes are unchanged: the new warning is not one of them — the branch has the rest of
the work. Since round 5 the architect texts route it with the other `left out of the commit` warnings (J5).

**G4, the in-place appendix told a cwd with no git to use git.** Since #74 `coordinate` isolates
whenever its cwd is in a repository, so in place means no repository; yet the text said to create
`claustrum/{{job_id}}` from HEAD, to rebase builder branches and to run `jobs clean`. In place,
everything after the briefs line is now `No git repository at <cwd>: there is no work branch and no
worktree isolation — builders run in place, one at a time, and their changes land directly in <cwd>;
nothing to rebase or clean.` Gone with it, as all git: the three receipt shapes, the integration
recipe, `--branch` remediation, the `max_parallel` bullet ("Isolation starts at 2") and "Never a merge
commit, never `git push`". The isolated text is unchanged. A cast whose builder has `max_parallel` 2 or
more sent every builder down the isolated path in such a cwd, where `JobWorktree.AddAsync` threw `no
commit to branch from` after the mint — fixed in round 3 (H7): it runs in place, queued at one, with a
warning, so "one at a time" is enforced for a numeric cap and still only instructs with none. The
user prompt's last line still names "your work branch" among the appendix's contents.

**G5, a hard kill mid-probe broke `jobs clean` for good.** Killed between creating
`.claustrum/worktrees/probe-<8 hex>` and its `finally`, `coordinate` left an empty directory no job
owns: `IsFinished` calls it finished (no job directory) and git's remove `not a working tree`, exit 1
on every later sweep. `JobsCommands.CleanAsync` asks `ArchitectWorktree.TryRemoveStrayProbeAsync`
first: named `probe-`, empty, and no `worktree …` line of `git worktree list --porcelain` ending in
`/.claustrum/worktrees/<name>` (by tail and case-insensitively — git lists realpaths, and a false match
only keeps the directory) ⇒ deleted, non-recursively, `removed stray probe directory <name>`.
Measured: an empty one removed, exit 0 when it was all there was; a non-empty one left and reported
`not a working tree`, exit 1, as before; a registered worktree named `probe-…` emptied by hand (git
lists it `prunable`) left alone.

### Review round 3 (2026-10-09): H1–H7

Each measured on git 2.56 in a scratch repository and, end to end, with the built binary against a
fake `claude` (`CLAUSTRUM_HOME` and `GIT_CONFIG_GLOBAL` in the scratch directory).

**H1, an empty directory counted as a nested worktree.** A repository whose HEAD carries `160000 …
.claustrum/worktrees/OLD` — someone ran `add -A` while a job worktree existed and no rule ignored it —
checks out an empty `.claustrum/worktrees/OLD/` in every new worktree, so `NestedWorktrees` refused
each of them for good: `jobs clean`, `--branch` freeing the branch, and `IsUntouchedAsync`, so the
abandoned cleanup never forced. A subdirectory counts now only when it holds a `.git` of its own (file
or directory). The other candidate was `git worktree list --porcelain`, matched by full path:

| shape | a `.git` in it | listed by `worktree list` |
|---|---|---|
| the gitlink's empty directory | no | no — but a top-level worktree of the same name is, so a match by tail would refuse |
| a real nested worktree | yes, `gitdir: <repo>/.git/worktrees/<id>` | yes, by realpath |
| a nested worktree whose `.git` file was deleted | no | yes, `prunable gitdir file points to non-existent location` |
| a nested worktree whose `.git/worktrees/<id>` was deleted | yes | no — and git refuses to work in it |

The `.git` check won: no git process in a check that was synchronous, no realpath normalisation (git
lists realpaths, measured 2026-10-08 through a symlinked cwd), and it keeps the last row, whose files a
plain remove of the parent would delete with nothing registered to say so. It gives up the third row:
a worktree whose pointer is gone, which git itself calls prunable. End to end: a builder's worktree cut
from such a HEAD holds the empty `OLD/`, commits its file, and `jobs clean` removes it, exit 0.

**H2, a repository with no commit held back the whole commit.** A `git init`ed `vendor/x` with nothing
committed makes `git add -A` fail — `error: 'vendor/x/' does not have a commit checked out`, `fatal:
adding files failed`, exit 128 — with nothing staged, and the warning's remedy failed the same way.
`add -A --ignore-errors` stages the rest and exits 1, with `error: '<path>/' does not have a commit
checked out` and `error: unable to index file '<path>/'` per skipped repository (measured; a path with
a space, a `'` or non-ASCII is printed raw, unquoted). Round 3 read exit 1 as a success when every
`error:` line named a directory with a `.git` of its own, and as a failed add on any other `error:`
line. Round 4 (below) retired that reading: exit 1 also covers a sparse-checkout skip, with no `error:`
line at all, and git translates the words. `add`'s exit code and stderr decide nothing now — the
repository is still `?? vendor/x/` in the status after the add, and its `.git` makes it G3's `embedded
repository at <path> left out of the commit on <branch> — …`.

- ⚠ git translates those lines (`… non ha un commit di cui è stato eseguito il checkout` under `it_IT`,
  measured; the `error:` and `fatal:` prefixes stay). Round 3 ran that `add` under `LANGUAGE=en`, through
  an environment overload on `CommandProcess` and `GitProcess`; round 4 removed all three, as no
  decision reads git's words any more.
- **Found while measuring: F1's `:(exclude)`s make `add` exit 1 in the standard
  setup.** With `.claustrum/{worktrees,briefs,locks}/` ignored — what `init` writes, what `coordinate`
  requires — and any of them on disk, `git add -A -- . ':(exclude).claustrum/worktrees' …` stages the
  rest and exits 1 with `The following paths are ignored by one of your .gitignore files:`, the excluded
  paths and two `hint:` lines, and no `error:` line (measured with and without `--ignore-errors`, with a
  nested worktree, a lock and a brief present, and with a tracked gitlink under the ignored directory).
  git counts an exclude item that names an ignored path as one the caller asked for. Until round 3
  every non-zero add was a failure, so an architect's leftovers went uncommitted whenever its briefs,
  locks or nested builders existed — `work left uncommitted on claustrum/<id>: The following paths are
  ignored …` — and so did the H1 builder above. 1c's measurement had nothing ignored. A builder leaving
  an ignored brief, an ignored lock and `left.txt` commits `left.txt`, with no warning (re-measured with
  round 4's binary).
- End to end with round 4's binary: `real.txt` plus a commit-less `vendor/x` commits `real.txt` with the
  one warning; `vendor/x` alone, no commit and the warning; plus a clone with a commit at `vendor/y`,
  both warnings and `real.txt` committed, under `LC_ALL=it_IT.UTF-8` too. An unreadable file, a failed
  add in round 3, is round 4's third shape now: left out with its own warning, the rest committed.

**H4, "anything to commit?" came from the status.** With `--ignore-submodules=none` (G2) the status
shows ` M sub` for a dirty initialised submodule, and for a pointer moved under `.gitmodules`'
`submodule.<name>.ignore = all`; `add -A` stages neither, and `commit` exits 1, `no changes added to
commit`, so the run warned `work left uncommitted on <branch>: On branch …`, a remedy that cannot
succeed (all measured). The status now only decides whether `add` runs at all. After it, the staged
entries `StagedStrayGitlinksAsync` already lists, minus the strays, decide: none, then no commit and
`commit: null`. Not `git diff --cached --quiet`, which the brief named: under
`diff.ignoreSubmodules=all` it exits 0 over a staged pointer move (1 with `--ignore-submodules=none`,
measured), and the `--raw` listing is already in hand with that flag. The same `ignore = all` in
`.git/config` instead of `.gitmodules` does not stop `add` (measured: staged and committed). What the
submodule held back went unsaid in round 3; round 4 warns `changes inside submodule <path> left out of
the commit on <branch> — …` for it. End to end: a builder that initialises a submodule and dirties a file
in it gets `commit: null` and that warning (with a `real.txt` beside it, `real.txt` is committed), and
`jobs clean` leaves its worktree (`uncommitted changes:  M sub`), as for any leftover.

**H5, a failure after `add` left the index as `add` had made it.** A failed `reset -- <strays>`, or a
`diff --cached`, `config --blob` or `reset` that threw (a timeout), returned `work left uncommitted …`
with a stray gitlink still staged, and the warning's remedy — commit it inside that worktree — would
have committed it. Every failure from `add` up to the commit now ends in `git reset -q`
(`ClearIndexAsync`: the `unreadable` branch's reset, made general and checked) — an `add` that cannot
start or passes its bound, and since round 4 a status after the add that fails, but never `add`'s own
exit code, which round 4 ignores — and the warning ends
`(index cleared: nothing is staged)` or `(and the index could not be cleared: …)` — a stale
`index.lock` fails both, exit 128 (measured). A refused `git commit` keeps its index: it holds exactly
what was to be committed, which is what the remedy needs. Measured with a `git` shim on `PATH` that
refuses `--literal-pathspecs reset`: `work left uncommitted on <branch>: an embedded repository at
vendor/y could not be unstaged: fatal: … (index cleared: nothing is staged)`, and nothing staged.

**H6**: the architect's owner block, under #80 below.

**H7, an isolated builder in a cwd with no repository died after its mint.** `DelegateEngine.RunAsync`
asks `GitRootLocator` before the isolated path: no repository and no `--branch` ⇒ in place. A numeric
`max_parallel` still goes through the slot gate, at a cap of 1 whatever the cast says: N runs in one
tree would read each other's edits into their snapshot receipts — why `max_parallel: 1` queues (#58) —
and the appendix already says "one at a time". The receipt carries `no git repository at <cwd>: ran in
place, no worktree, no commit` first in `warnings`, through `RunOptions.PreRunWarnings`, which
`Runner.FinishAsync` puts on every result before result.json is written (`FinishAsync` takes the
options now instead of the worktree). Appending after the run returned was the smaller change and would
have left the receipt on disk without it — #60's lesson. `--branch` stays on the isolated path and is
refused there as before (`--branch foo: no local branch of that name in <cwd> …`). Measured end to end
with `max_parallel: 2` in a plain directory: success, `worktree`/`branch`/`commit` null, the warning in
the returned and the on-disk receipt and on `run`'s stderr, `.claustrum/locks/default__builder.0.lock`
made; `max_parallel: 1` unchanged; `--branch` refused.

- ⚠ **A plain directory's `claustrum.json` is not read**: `Config.Load` looks for it at the git root
  only. The first H7 measurement pointed `backends.claude.path` at a fake there, and the real `claude` on
  PATH ran three times instead (about $1, no file changed). Outside a repository a fake backend comes
  from the user config (`~/.config/claustrum/config.json`) or a `claude` earlier on PATH.
- A cwd inside a repository with no commit still takes the isolated path and fails after the mint, `no
  commit to branch from`.

**Recorded, not changed.** The uncommitted-`.gitignore` refusal (G1) stays even for an edit that has
nothing to do with Claustrum's rules: the worktree gets HEAD's rules, and telling whether HEAD's rules
alone still ignore the machinery means evaluating them without the working-tree file — not cheap, and a
wrong "yes" leaves the architect's worktree to `jobs clean` for good.

### Review round 4 (2026-10-09): the commit reads git's index, not its words

Measured on git 2.56 in scratch repositories and, end to end, with the built binary against a fake
`claude` — its `claustrum.json` at a git root (a plain directory's is not read, H7), `HOME`,
`CLAUSTRUM_HOME` and `GIT_CONFIG_GLOBAL` in the scratch directory, `PATH=/usr/bin:/bin`, which holds no
agent CLI — each case a `run builder --branch feat`.

**The defect.** `StageAsync` read `git add -A --ignore-errors` exit 1 as a success unless stderr had an
`error: ` line. git exits 1 with no such line when it skips a path outside a sparse-checkout cone, which
`git worktree add` copies into the job's worktree: a builder writing `a/new.txt` and `b/new.txt` in a
repository sparse on `a` got `status: success`, `commit` set, no warning and both paths in
`changed_files`, and the commit held `a/new.txt` alone (the reviewer's case, reproduced here). The rest
of the design rested on `LANGUAGE=en` making git's stderr English, never measured on Git for Windows.

**Nothing decides on `add`'s exit code or words now.** `CommitAllAsync`:

1. the status before the add (`statusArgs`) only says whether there is anything to do;
2. `add -A --ignore-errors` with F1's excludes runs, its exit code ignored, its stderr kept;
3. the stray gitlinks are unstaged as in G3, and any failure from here to the commit clears the index (H5);
4. **staged** is the `--raw` listing G3 already reads, minus the strays: empty ⇒ no commit (H4);
5. **left out** is whatever a second status (`remainderArgs`: `--porcelain=v2 -z`, the same flags and
   excludes) still shows on the worktree side after all that — `Y` not `.`, or untracked;
6. each path left out costs one warning, by shape, decided from git's flags and the filesystem:

| shape | decided by | warning |
|---|---|---|
| a submodule | v2's `S…` field | `changes inside submodule <p> left out of the commit on <branch> — commit them in the submodule, then stage its pointer` |
| a directory with its own `.git` — a clone, a commit-less `git init`, a stray G3 unstaged | `<p>/.git` exists | `embedded repository at <p> left out of the commit on <branch> — move it out or add it as a submodule` |
| anything else — sparse-checkout, an unreadable file, a failing clean filter | neither | `<p> left out of the commit on <branch>: git did not stage it (sparse-checkout, permissions or a filter — see the job's stderr.log)` |

The last shape's cause is only in git's words, so `add`'s stderr is appended whole to the job's
`stderr.log`, under a `claustrum: git add -A in <worktree>, for the commit on <branch>:` line: `Runner`
hands `JobPaths.StderrLog` to `CommitRunAsync`, whose process runner has closed it by then. A caller with
no job directory (the 5-argument `CommitRunAsync`) or a log that cannot be written gets ` — git said:
<line>` in place of the pointer; an `add` that printed nothing, neither. When nothing at all was staged
and a path of that last shape was left out, `work left uncommitted on <branch>: git add staged nothing:
<line>` is added: a fatal `add` — a stale `index.lock`, measured — leaves every path in the last shape,
whose parenthesis then names the wrong causes, and this line names the right one. `<line>` is for display
only: git's first `fatal: `/`error: ` line, else its first — the prefixes stay untranslated (measured
under `it_IT`), and the "ignored paths" banner comes first whenever a machinery directory exists.
`GitProcess` and `CommandProcess` lost the environment overloads round 3 added for `LANGUAGE=en`: nothing
else used them.

**A departure from the brief: "left out" is the status after the add, not "expected − staged".** The
brief defined it as every path the status listed before the add, minus every path staged after it.
Measured, that set is wrong both ways once the role has staged something itself:

| the role did | status before | staged after | expected − staged | status after |
|---|---|---|---|---|
| `git add f`, then edited `f` again and made it unreadable | `MM f` | `f`, its earlier version | nothing: the commit holds the stale `f`, silently | `MM f`: left out, warned |
| `git add g`, then put `g` back as HEAD has it | `MM g` | nothing | `g`, a false "git did not stage it" | clean |
| `git add n.txt`, then `rm n.txt` | `AD n.txt` | nothing | `n.txt`, false | clean |

The first is the defect's own class, a commit silently short of the work. The price is one more
`git status`, and a failure of it clears the index like any failure before the commit (H5).

| end to end | commit holds | warnings |
|---|---|---|
| sparse on `a`: `a/new.txt`, `b/new.txt` | `a/new.txt` | `b/new.txt left out …`; the log has git's sparse advice |
| sparse: `b/new.txt` alone | no commit | that, and `work left uncommitted on feat: git add staged nothing: The following paths and/or pathspecs matched paths that exist` |
| `real.txt`, a commit-less `vendor/x` | `real.txt` | `embedded repository at vendor/x …` |
| the commit-less `vendor/x` alone | no commit | the same one |
| `real.txt`, `vendor/x`, a clone with a commit at `vendor/y`, under `LC_ALL=it_IT.UTF-8` | `real.txt` | both `embedded repository` warnings |
| the clone alone | no commit | `embedded repository at vendor/y …` |
| a `git submodule add`ed `deps/lib`, `real.txt`, the clone | `.gitmodules`, `deps/lib`, `real.txt` | `embedded repository at vendor/y …` only |
| an initialised submodule with a dirty file, alone | no commit | `changes inside submodule sub …` |
| the same, and `real.txt` | `real.txt` | `changes inside submodule sub …` |
| `real.txt`, an unreadable `secret.txt` | `real.txt` | `secret.txt left out …`; the log has `error: open("secret.txt"): Permission denied` |
| the unreadable file alone, under `it_IT` | no commit | that, and `… git add staged nothing: error: open("secret.txt"): Permesso negato` |
| the same beside an ignored brief | no commit | the same two: git's `error:` line, not the banner the log starts with |
| an ignored brief and lock on disk, `left.txt` | `left.txt` | none |
| `status.showUntrackedFiles=no`, `new.txt` | `new.txt` | none |
| a stale `index.lock`, `real.txt` | no commit | `real.txt left out …`, and `… git add staged nothing: fatal: Unable to create '…/index.lock': File exists.` |
| the departure table's first row | `f` as staged | `f left out …`, and `receipt delta unavailable …`: the delta cannot read `f` either |
| its second and third rows, and `real.txt` | `real.txt` | none |
| nothing changed; the role committed `c.txt` itself | none; the role's commit | none |
| a `pre-commit` hook exits 1 | no commit | `work left uncommitted on feat: hook says no`, and `A  real.txt` still staged |

**A conflict in progress is never concluded by the runner (finding 5).** Measured in a linked worktree:
a conflicted `git merge`, `cherry-pick` or `revert` keeps HEAD on the branch, and so does a stopped
`git am` (`rebase-apply/applying`); a stopped rebase detaches it (T3 already covers that). The runner's
`add -A` and `commit` there concluded it — under `MERGE_HEAD`, a merge commit, conflict markers
staged. So once HEAD is the branch, `VerifyAsync` asked `rev-parse -q --verify` for `MERGE_HEAD`,
`CHERRY_PICK_HEAD` and `REVERT_HEAD` (exit 1 is "no such ref"; anything else is git failing, a
non-own mismatch as elsewhere there), then looks for `rebase-merge`/`rebase-apply` under `rev-parse
--git-path`. Any of them was an own-worktree mismatch with `WorktreeMismatch.Operation` set — "a
merge", "a cherry-pick", "a revert", "a rebase", or "a git am", one more than the brief listed, since
its way out differs — and `Reason` `a merge is in progress`: snapshot kept, no commit, no delta, and
`work left uncommitted: <path> has a merge in progress, not a clean <branch>`. Measured end to end for
merge, cherry-pick, revert and am; after the merge run the worktree still has `UU f` and its
`MERGE_HEAD`. **Round 5 changed the signal (J1, J2, below):** the cherry-pick and revert heads are no
longer triggers — a clean `revert -n` leaves `REVERT_HEAD` and commits as an ordinary commit — and
unmerged index entries are, whatever left them; `MERGE_HEAD` and the rebase directories stay.

- ⚠ `commit` is null on that receipt even when the role committed on its branch before it stopped (a
  commit, then a conflicted revert — since round 5 the unresolved-conflicts receipt), as on T3's
  off-branch receipt; the brief asked for no commit.
- The appendix and `roles/architect/ROLE.md` first routed only "one of three shapes" of `work left
  uncommitted`, leaving this fourth one to MANUAL §15 alone; round 4 made both route "one of four
  shapes", the new one in the same words. Since round 5 both route "what did not land, in one of
  these shapes": the fourth names `has a <merge | rebase | git am> in progress` and `has unresolved
  conflicts`, and the `left out of the commit` warnings are routed too (J5).
- Round 4 did not catch a conflicted `git stash pop` (or `apply --3way`), which leaves `UU` entries and
  no head of its own: the runner committed the markers. Round 5 refuses it (J1).

**`--branch` over a finished worktree with a populated submodule.** git refuses to remove any worktree
with a submodule checked out — `fatal: working trees containing submodules cannot be moved or removed`,
exit 128, before its own clean check (measured) — and `FreeBranchAsync` said "… left in place (fatal:
…) — commit or discard its changes" of a worktree with nothing to commit. It now says `--branch <b>: its
previous worktree <path> holds a populated submodule, which git will not remove — remove it by hand
(`git worktree remove --force "<path>"`, which also deletes any commit made inside the submodule and
pushed nowhere) and retry` (the path quoted since round 5). Round 4 matched git's English sentence whole
at the end of the refusal, so a translated git (`fatal: gli alberi di lavoro contenenti sottomoduli …`,
measured) got the generic text; round 5 reads the shape before the remove, in any language, and keeps
the English match as a fallback (J4) — whole, never a fragment, which could hand `--force` to a
refusal that merely quotes a path. The clause after the command departs from the brief's text:
measured, a submodule initialised in a linked worktree keeps its git directory in
`.git/worktrees/<id>/modules/<name>`, and `remove --force` deleted a commit made in it — the only copy
— while the branch's pointer still named it.

**#80's owner block** names Claustrum's own NOTES.md by repository now (under #80, below); round 5
dropped its devkit pointer (J6).

**Recorded, not changed.** One warning per path, uncapped: a role that writes hundreds of files outside
the sparse cone gets hundreds. A path the role stages itself with `git add -f` inside Claustrum's
machinery is still committed — F1's excludes keep `add` and the status off it, not the index.

### Review round 5 (2026-10-09): conflicts read off the index, a stash header, submodules in any language

Measured on git 2.56 in scratch repositories and, end to end, with the built binary against a fake
`claude` — its `claustrum.json` at the git root, `HOME`, `CLAUSTRUM_HOME` and `GIT_CONFIG_GLOBAL` in the
scratch directory, `PATH=/usr/bin:/bin` — each case a `run builder --branch feat`.

**J1, a conflict with no head of its own was committed; J2, a clean `revert -n` was refused.** Round 4
keyed "in progress" on three heads and the rebase directories. What each operation leaves (measured):

| the role ran | unmerged | head or directory |
|---|---|---|
| a conflicted `merge` | `UU f` (`UD f` modify/delete, `AA n` add/add) | `MERGE_HEAD` |
| a conflicted `merge --squash`, `stash pop` (the stash is kept), `stash apply` or `apply --3way` | `UU f` | none |
| a conflicted `cherry-pick` / `revert` | `UU f` | `CHERRY_PICK_HEAD` / `REVERT_HEAD` |
| a clean `merge --no-commit` | none | `MERGE_HEAD` |
| a clean `revert -n` | none | `REVERT_HEAD` |
| a clean `cherry-pick -n` | none | none |
| a `cherry-pick` stopped on a conflict, then `git add` of the fix | none | `CHERRY_PICK_HEAD` (a sequence: `sequencer/` too) |

`git commit -m` over the last three exits 0, makes an ordinary single-parent commit and clears the head,
under the files and the reftable backends alike. A stopped multi-pick sequence keeps `sequencer/` after
that commit when picks remain (`git status`: "Cherry-pick currently in progress"; the rest unpicked) and
drops it when the stopped pick was the last — recorded, not a trigger: the commit holds what the role
left, and the picks it never made are work it never did.

So `OperationInProgressAsync` keeps `MERGE_HEAD` — the runner never makes a merge commit, conflicted or
not: `a merge is in progress` — and the `rebase-merge`/`rebase-apply` directories (a rebase or `git am`
rewrites the branch), drops `CHERRY_PICK_HEAD` and `REVERT_HEAD`, and then counts unmerged paths: `git
ls-files -u -z`, one to three `<mode> <sha> <stage>\t<path>` entries per path. Any ⇒ an own-worktree
mismatch, `Reason` `unresolved conflicts (<n> paths)` (`1 path`), `WorktreeMismatch.UnresolvedConflicts`
n: snapshot kept, no commit, no delta, and `work left uncommitted: <path> has unresolved conflicts on
<branch> — resolve or abort the operation inside the worktree, then commit there yourself`. The count
reaches `Reason` only; the warning is the brief's text.

- **A departure from the brief: `ls-files -u` inside `VerifyAsync`, not the status before the add in
  `CommitAllAsync`.** The same signal — status's v2 `u` and v1 `UU/AA/DD/AU/UA/DU/UD` entries are those
  index stages, and the path counts agreed in every row above — read where every other "safe to
  commit?" answer is, so the runner's own verify (before the after-snapshot), `CommitRunAsync`'s and
  `IsUntouchedAsync`'s agree and the one `Unverified` path writes the receipt. `ls-files` reads the
  index alone: no `status.*` setting, `diff.ignoreSubmodules` or pathspec decides what it lists, and no
  working tree is scanned. The price is one git process per verify. It also counts a conflict under
  Claustrum's machinery, which `add` excludes — git's commit would refuse that index anyway
  (`Committing is not possible because you have unmerged files`).
- ⚠ `commit` stays null when the role committed before the conflict (the `stash pop` case below: its own
  commit is on `feat`), as on every own-worktree receipt.

| end to end | commit | warnings | the worktree after |
|---|---|---|---|
| conflicted `merge --squash`, `real.txt` | none | `… has unresolved conflicts on feat — …`; `changed_files` `f`, `real.txt` | `UU f`, `?? real.txt` |
| a role commit, then a conflicted `stash pop` / `stash apply` | none | the same | `UU f` |
| conflicted `apply --3way`, `cherry-pick`, `revert` | none | the same | `UU f`; a cherry-pick's or revert's head kept |
| clean `revert -n`, `real.txt` | `f`, `g`, `real.txt`, one parent | none | clean, `REVERT_HEAD` gone |
| clean `cherry-pick -n`, `real.txt`; a picked conflict fixed and added | one parent | none | clean, no head |
| conflicted `merge` | none | `… has a merge in progress, not a clean feat` | `UU f`, `MERGE_HEAD` |
| clean `merge --no-commit` | none | the same | `MERGE_HEAD` |
| `git am -3` stopped on a conflict | none | `… has a git am in progress, not a clean feat` | — |

**J3, `status.showStash` put a fake path on the receipt.** With it set and any stash, `status
--porcelain=v2 -z` starts with a `# stash 1` field (measured); `StatusEntry.Parse` failed closed on it —
`# stash 1 left out of the commit …`, and `unexplained` (the review's case). v1 `--porcelain` prints no such line, and
`status.branch=true` adds no `# branch.*` header to either (git defers it for porcelain, measured).
`remainderArgs` passes `--no-show-stash` (measured: the field goes), and `Parse` skips any field
starting with `#` — no entry's path field starts one: `?` and `1`/`2`/`u` lead, and a `2` entry's
original path is skipped by index. End to end: a role that stashes under `status.showStash=true` and
writes `real.txt` gets `real.txt` committed and no warning.

**J4, the populated-submodule text needed git in English.** `FreeBranchAsync` now asks before the
remove, the way git's own `validate_no_submodules` (git 2.56, `builtin/worktree.c`) does: `rev-parse
--git-path modules` an existing directory — per worktree, measured `.git/worktrees/<id>/modules`, not
the main checkout's `.git/modules` — or a `160000` entry of `ls-files -s -z` whose directory holds a
`.git`. The English match stays behind it as a fallback, and the suggested command quotes the path.

- ⚠ **Not "refuse directly", as the brief said:** git refuses a submodule before its own clean check,
  so a dirty worktree with one would have got the `--force` remedy and lost its work. `NestedWorktrees`
  and `UncommittedAsync` answer first; a dirty one gets the generic `… left in place (<what>) — commit
  or discard its changes, then retry`, as `TryRemoveAsync` gave it before. The common case — no
  submodule — still runs the status once, inside `TryRemoveAsync`.
- git's test is a `.git` that resolves to a repository; this one is a `.git` that exists, so a broken
  `.git` file in a gitlink's directory gets the `--force` text where git would remove plainly. An empty
  `modules` directory counts, as git's own source admits it does.
- Measured under `LC_ALL=it_IT.UTF-8` (git: `fatal: gli alberi di lavoro contenenti sottomoduli non
  possono essere spostati o rimossi`): a finished worktree that initialised `sub` and committed
  `real.txt` ⇒ the second `--branch feat` gets the dedicated message, the path quoted; one with a dirty
  file inside `sub` ⇒ `… left in place (uncommitted changes:  M sub) — commit or discard …`; a main
  checkout with `.git/modules` and a job worktree whose gitlink is uninitialised ⇒ freed, the second
  run a success.

**J5, the `left out of the commit` warnings were not routed.** An architect reading "the branch carries
its work as a commit" would integrate a branch missing a path. Both texts now open "unless its `warnings[]`
says what did not land, in one of these shapes" and end the list with: a `… left out of the commit on
<branch>` warning means the branch lacks that path while the builder's worktree still holds it — if it
belongs in the change, stage and commit it there yourself before integrating (an embedded repository:
move it out or add it as a submodule first). "before integrating" is mine, as in the first shape. The
fourth shape names `has a <merge | rebase | git am> in progress` and `has unresolved conflicts`, with a
remedy for each: abort a merge, finish or abort a rebase or `git am`, resolve the conflicts or abort
what made them (`cherry-pick --abort`, `revert --abort`, `reset --merge`). The shared span is
character-identical after whitespace is flattened (checked); MANUAL §9 and §15 say the same, §15 with a
row of its own for the conflicts warning.

**J6**: the architect's owner block, under #80 below.

**J7, a commit past its bound dropped the left-out warnings.** `(failure, leftOut) = await
CommitAllAsync(…)` never assigned when `git commit` threw, so a hook past five minutes reported only the
commit. `CommitAllAsync` now catches the commit's own throw and returns it beside `leftOut`; the outer
catch is reached only by the first status, before anything is left out. Measured with a `git` shim
first on `PATH` that sleeps 310 s before any `commit` (a hook's stand-in; the bound is the fixed five
minutes, so the run takes 301 s), the role writing `real.txt` and a commit-less `vendor/x`: `commit`
null, `embedded repository at vendor/x left out of the commit on feat — …`, then `work left uncommitted
on feat: git commit -q -m claustrum builder <id> timed out after 300s`; `real.txt` still staged. The
shim died before git ran, so no `index.lock` was left; whether a git killed inside a real hook leaves
one is unmeasured.

### Known and left as is (2026-10-09)

- A demo-author delegated with the architect's worktree as `--cwd` does **not** write its deck there:
  `roles/demo-author/ROLE.md` sends it to the main checkout, "the first `worktree` line of `git
  worktree list --porcelain`" — the operator's checkout, under an isolated architect. So the deck lands
  as gitignored files in the operator's `docs/demos/<feature>/` (the `info/exclude` line with it), and
  `jobs clean` does not delete it. That breaks "your checkout is never touched" by exactly one
  gitignored directory, and it is accepted: the deck is made to leave the building, and a worktree is
  the one place it must not live. (A first draft of this bullet said the deck went into the worktree
  and died with it; review F6 found the role's own rule.)
- A worktree add that fails after the mint (a hook, a full disk) still throws out of the run and
  leaves a pending job directory, as it does for an isolated builder; the committed-files check makes
  the usual causes (no commit at all, a broken repository) a pre-mint refusal instead.
- G6 (measured): a `coordinate` that never spawns — `backend 'claude' was not found on PATH`, exit 3 —
  leaves `.claustrum/worktrees/<id>` and `claustrum/<id>` at HEAD, as an isolated builder does:
  `Runner` writes its `backend_missing` result.json, so `RunIsolatedAsync`'s catch, the one caller of
  `TryRemoveAbandonedAsync`, is never reached; `jobs clean` removes the worktree, and the branch stays.
- H1's empty gitlink directory one level up — in the operator's checkout once a branch carrying it is
  fast-forwarded — is a directory `jobs clean` enumerates: no job directory makes it "finished", and
  git's remove answers `not a working tree`, exit 1, on every sweep (measured), G5's shape under a name
  that is not `probe-`. Deleting it would touch a tracked path of the operator's checkout; left as is.
- `roles/_shared/claustrum-skill.md` does not mention the committed-cast precondition; a host that
  coordinates right after `cast create` meets the exit-2 message, which names the file to commit.
- Windows, unmeasured: a nested builder's files sit two `.claustrum\worktrees\<24-character id>`
  levels deep instead of one, about 46 characters more than before; a repository whose paths already
  come near `MAX_PATH` needs `core.longpaths` for the nested checkout.

## Architect role: review per cluster, ported from devkit PR #12 (2026-10-09, issue #80)

The owner's rule of 2026-10-09: blind review runs once per **cluster** of related changes — any
size, split along natural seams only when one reviewer cannot hold it — never once per builder or
per fix; remediation is re-reviewed only where it touched, as one pass; one tester gate per cluster.
Taken from devkit PR #12 (`docs/cluster-review`, open, not draft) as worded today, from its
`claude/agents/{architect,builder,tester}.md` hunks; if the PR's wording moves before it merges,
re-port.

- **What moved.** architect: steps 4-6 ("Collect the whole cluster", "Blind review, per set of
  related changes" with the dated owner block, "Then verification, once per cluster … **one**
  tester"), the first bullet of "The caller may fix the shape of that loop", and the code-reviewer
  sizing line in the delegation contract. builder: the "You do not summon" bullet, step 3, and
  "the assembled cluster" in both `parts/delegation.*.md`, where Claustrum keeps that sentence.
  tester: the "arrive last" bullet.
- **Claustrum's voice.** "spawn" became "delegate to"; devkit's "base `code-reviewer`" and "heavier
  tiers" became "base-tier code-reviewer" and "`xhigh` or `max`". The owner block's pointer said
  "Long form and receipt: devkit `docs/pipeline.md`, stage 2"; it says "Receipt: Claustrum's own
  `NOTES.md` (orlodax/Claustrum #80)." now. devkit is private and ROLE.md ships to every user, so the
  receipt has to live where they can read it: here, in this public repository (#74 H6) — named as
  Claustrum's (round 4), since "this repo" in a user's architect prompt is the user's own. Round 4 kept
  "Long form in the team's devkit (`docs/pipeline.md`, stage 2)" before it; round 5 (J6) dropped that
  too — a pointer every user but the team's members follows to a page they cannot open. It is #30 and
  #32 each running a full review → fix → re-review → tester loop (2026-10-08); devkit's
  `docs/pipeline.md` stage 2 still holds the long form, for the team.
- **Not touched.** The tier stubs carry none of this text, so only the base goldens move; the
  `role.json` descriptions still say "batch", as devkit's frontmatter does.

## Ctrl-C reaches the runner: ProcessTerminationTimeout is null (2026-10-09, issue #88)

System.CommandLine 2.0.12 registers its own SIGINT/SIGTERM handler whenever
`InvocationConfiguration.ProcessTerminationTimeout` is non-null — and its default is 2 s. That
handler ran ahead of `run`'s and `coordinate`'s `Console.CancelKeyPress` and returned from
`InvokeAsync` when the 2 s ran out, so Runner never cancelled. Measured 2026-10-09 with the Debug
binary, a fake `claude` (`printf 'half\n' > partial.txt; sleep 31.7`) and `kill -INT`: before, exit
130 after 2006 ms, no `result.json` (job left `pending`), the `sleep` still alive and `partial.txt`
uncommitted in the worktree. With `ProcessTerminationTimeout = null` (`Program.cs`): exit 130 after
404 ms (`run` isolated), 352 ms (`run` in place) and 401 ms (`coordinate`), `status: cancelled` on
stdout and on disk, the isolated cases' `commit` holding `partial.txt`, and no `sleep` left.

- **The other verbs lose nothing.** None but `run`/`coordinate` took the token it cancelled; Ctrl-C
  on them is now the runtime's default (immediate) instead of a 2 s wait for exit 130. `mcp` runs a
  Generic Host, whose own console lifetime stops it on SIGINT/SIGTERM.
- **A second Ctrl-C forces the exit** (2026-10-09). With the timeout null nothing else ends a
  cancel path that hangs — the post-run commit runs the repo's hooks and filters, bounded only by 5
  minutes. So `CancelKeyHandler`, shared by `run` and `coordinate`, cancels the token on the first
  press and prints `cancelling… press Ctrl-C again to exit without a receipt` to stderr; on the
  second it leaves `e.Cancel` false and the runtime ends the process there, with no `result.json`.
- **SIGTERM is not handled**, before or after: only `CancelKeyPress` (SIGINT) reaches Runner.
- ⚠ **A test that sends SIGINT must start the binary with SIGINT not ignored.** A non-interactive
  shell starts `&` jobs with SIGINT set to ignore and .NET keeps an inherited ignore: the first
  measurement ran the fake to the end, `status: success`. `set -m` in the measuring script fixed it.

## The second drive: wave 2 of M4, 2026-10-09 (docs/TEST-DRIVE.md step 18, issue #72)

Wave 2's acceptance drive (docs/PLAN.md "M4 waves (2026-10-08)") ran `coordinate` unattended twice,
measured 2026-10-09 on Linux, git 2.56, claude 2.1.295. The toy repo, `~/claustrum-drive-wave2`, is
a plain-Python word-count CLI (`wc.py`) whose `AGENTS.md` names the gate `python3 -m unittest
discover -s tests -v`. It has no git remote, so nothing could be pushed and `gh` took its repository
from `GH_REPO=orlodax/Claustrum`, where the four fixture issues live: #76 `--top N`, #77
`--ignore-case`, #78 a README with an options table, #79 `--json`, each titled "Wave-2 drive
fixture" and closed by hand afterwards. Cast `default`: architect `frontier-reasoning`
(claude:opus), builder `standard-coding` (sonnet, `max_parallel: 2`), code-reviewer
`frontier-coding` (opus), tester `standard-coding`, ui-reviewer and demo-author `null`,
`budget_usd: 15`; aliases in `claustrum.json`, and the cast, the config and the `.gitignore` lines
committed as "Scaffold Claustrum for the wave-2 drive". Every run below ended `success` with a
parsed report fence, none hit its timeout, and every builder's `changed_files` matched its own
`files_changed`.

**Drive 1 — the wave-1 binary (`0.1.0-alpha+79b79f6`, `main` after PR #75), `--issues 76,77 --json
--timeout 5400`, job `20261009-104834-7573ece8`.** `status: success` in 344 s, exit 0. The architect
judged #76 and #77 too entangled (both edit the same lines of `main`) and gave them to one builder,
so `max_parallel: 2` was not exercised.

| run | model | cost | time | note |
|---|---|---|---|---|
| builder `20261009-104921-4e3d16b8` | sonnet | $0.22 | 21 s | isolated, commit 5746d9b |
| code-reviewer `20261009-105001-46b393d7` | opus | $0.51 | 58 s | blind, 3 low findings |
| builder `20261009-105131-333606a6` | sonnet | $0.21 | 23 s | fix, `--branch`, commit 47f269e |
| code-reviewer `20261009-105205-2abbf3fb` | opus | $0.45 | 45 s | re-review, clean |
| tester `20261009-105310-67eb9c3a` | sonnet | $0.26 | 34 s | 40 tests green |

The remediation builder ran with `--branch claustrum/20261009-104921-4e3d16b8`: a new worktree, its
receipt's `branch` the first builder's, its commit on top of 5746d9b. Architect $1.15; the ledger
(`jobs budget`) reads $2.81 spent, architect included, $0.00 reserved; the receipts sum to $2.80,
which agrees to within a cent of rounding. Work branch `claustrum/20261009-104834-7573ece8`: one
commit, efc8766 (the architect squashed both builder commits and the tester's tests), linear,
`Closes #76` and `Closes #77`, nothing pushed. **The operator checkout:** no child moved its `HEAD`
or branch — the builders ran in worktrees — but reviewer and tester ran in place in the architect's
cwd, which by the wave-1 design *was* the operator checkout (the tester's `tests/test_wc.py` was
written there), and the architect itself ran `checkout: moving from main to claustrum/<id>`, two
fast-forwards and a reset there: the reflog went from 2 entries to 7. That is what #74 was filed
for.

**Drive 2 — the #74 binary (`0.1.0-alpha+736ffa5`, `feature/m4-wave2`), `--issues 78,79`, job
`20261009-155235-290beabc`.** `status: success` in 419 s. The architect ran in
`.claustrum/worktrees/20261009-155235-290beabc` on `claustrum/20261009-155235-290beabc`; the
operator checkout stayed on `main` at efc8766 with a byte-identical reflog before and after (9
entries: drive 1's 7, plus the 2 of the operator's `git switch main && git merge --ff-only`, which
fast-forwarded `main` to efc8766 between the drives). The two builders' worktrees nested under the
architect's, but they did **not** run in parallel, whatever the architect's report said: the ledger
has `20261009-155349-6c900088` finishing at 15:54:15.336 and `20261009-155415-5c3899d0` starting at
15:54:15.400, 64 ms later — two blocking `claustrum run` calls in a row. With drive 1's single
builder, `max_parallel: 2` was exercised by neither drive's architect, only by the test suite (#90,
"Spawned architects serialise their builders unless told how not to").

| run | model | cost | time | note |
|---|---|---|---|---|
| builder `20261009-155349-6c900088` | sonnet | $0.23 | 26 s | `--json`, commit b4bce9a |
| builder `20261009-155415-5c3899d0` | sonnet | $0.20 | 21 s | README, commit aeb48a1 |
| code-reviewer `20261009-155506-78ace55a` | opus | $0.59 | 64 s | blind, 1 medium, 3 low |
| builder `20261009-155626-54db7314` | sonnet | $0.21 | 18 s | fix, fresh worktree, dbf8286 |
| code-reviewer `20261009-155656-48fbbc74` | opus | $0.42 | 25 s | re-review, clean |
| tester `20261009-155744-75c33abd` | sonnet | $0.42 | 71 s | 80 tests green |

The remediation builder was cut from the work-branch tip after integration, not run with
`--branch`: the reviewed diff was the work branch itself, checked out in the architect's worktree,
and `--branch` refuses a branch another worktree holds. Architect $1.32; ledger $3.39 spent, $0.00
reserved. Work branch: two commits, c6697ad (`Closes #79`) and 7d922ae (`Closes #78`), linear, no
merge — the architect rebuilt the builders' commits so each cites its issue. It left its worktree
clean, so the runner's post-run commit was not needed: the receipt's `commit` is 7d922ae, the tip.
The architect removed the nested builder worktrees itself (`jobs clean --cwd <its worktree>`); its
own is left for the operator's `jobs clean`. Every builder's `result.json` carries `worktree`,
`branch` and `commit`, and so does the architect's; reviewers and tester ran in place, in the
architect's worktree, and carry none.

**#72's done-when, verdict by verdict.** The four criteria were met across the two drives; no
single drive meets all four — drive 1, on the wave-1 binary, exercised `--branch` but its reviewer
and tester ran in the operator checkout; drive 2, on the #74 binary, left the checkout untouched but
remediated from the work-branch tip.
- *Children never touch the operator checkout* — drive 2 PASS, and the architect did not either.
  Drive 1 FAIL on the files: no child moved `HEAD` or the branch, but the in-place reviewer and
  tester ran in the checkout the wave-1 architect had switched to its branch, and the tester wrote
  `tests/test_wc.py` there — #74's case, fixed for drive 2.
- *Every builder branch carries a commit, with `commit`/`worktree`/`branch` on disk* — PASS, all
  five builders.
- *The remediation builder runs with `--branch` on the first builder's branch* — PASS in drive 1.
  Not exercised in drive 2: remediation continued on the reviewed state through a fresh worktree
  cut from the integrated tip, because that state lived on the work branch, which `--branch` cannot
  take.
- *The work branch is linear, `Closes #<n>`, nothing pushed* — PASS in both.

**Spend:** $2.81 + $3.39 = $6.20 of agent time for the two drives, plus $1.06 spent by accident
during the #74 build (a builder measured a non-git case with a fake `backends.claude.path` in a
plain directory's `claustrum.json`, which `Config.Load` ignores outside a git root, so the real
`claude` ran three times — the trap is #74's H7) and $0.03 of claude probes on 2026-10-08: $7.29
against an envelope of about $30.

**Defects filed during wave 2, all on the board, all fixed on this branch:** #88 (Ctrl-C exited
after 2 s without cancelling the backend, found by the cluster's tester), #89 (`BindUserPrompt`
replaced every occurrence) and #90 (drive 2's builders ran one after the other under `max_parallel:
2`, found afterwards in its ledger; the fix is role text). Neither drive failed a run: every one
ended `success`. Not proved: a spawned architect actually running two builders at once (#90's
recipe has not been driven), the Windows legs, the opencode/api/copilot/cursor backends,
ui-reviewer and demo-author, and `coordinate` through the MCP door with a real backend.

**The observation worth keeping:** the architect is the most expensive seat — $1.15 and $1.32, about
40% of each drive, against at most $0.59 for any child. Remediation is cheap: each re-review cost
less than its first review ($0.45 vs $0.51, $0.42 vs $0.59; 45 s vs 58 s, 25 s vs 64 s), and the
fix builder no more than the first; the whole round, fix plus re-review ($0.66, $0.63), still costs
slightly more than the first review alone.

## Spawned architects serialise their builders unless told how not to (2026-10-09, issue #90)

Drive 2 of the wave-2 drive (job `20261009-155235-290beabc`, cast `builder.max_parallel: 2`): the
opus architect reported "two builders ran in parallel", but the tree ledger has builder
`20261009-155349-6c900088` finishing at 15:54:15.336 and builder `20261009-155415-5c3899d0`
starting at 15:54:15.400 — 64 ms later, strictly sequential. `claustrum run` blocks until its role
is done, and under `claude -p` the architect's Bash tool runs one command at a time unless told to
background it; nothing in the role said how. Drive 1's architect used a single builder, so neither
drive exercised `max_parallel: 2` from a spawned architect — only the test suite has.

- **The fix is role text.** Both `roles/architect/parts/delegation.*.md` gain a "running builders
  concurrently" recipe: each `claustrum run builder … --json --cwd "<dir>" >
  .claustrum/briefs/<n>-builder.result.json &`, then `wait`, then read each receipt file (or the
  harness's own background-command mode), never more at once than `max_parallel`. The appendix's
  `max_parallel` line and ROLE.md's "Fan builders out" bullet point at it with one shared clause,
  pinned by `CoordinationTextAgreementTests`.
- ⚠ **The architect's report is not evidence of concurrency; the ledger is.** Each entry under
  `<claustrum home>/budget/<tree>/` carries `started_at`/`finished_at`: overlap is what proves it.
