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

## npm shims on Windows (2026-09-12, confirmed 2026-09-13)

`claude` and `copilot` install as `.cmd` shims. `CreateProcess` ignores `PATHEXT`, and `cmd.exe`
mangles `%` and long argument lines. `BinaryLocator`/`NpmShimParser` read the shim body and
recognize two real shapes, confirmed against actual installs on this machine: a compiled-binary
shim (`claude.cmd` -> `"%dp0%\node_modules\@anthropic-ai\claude-code\bin\claude.exe" %*`, run
directly) and the classic pure-JS shim (`yo.cmd` -> `"%_prog%" "%dp0%\node_modules\yo\lib\cli.js"
%*` where `_prog` is `%dp0%\node.exe` if bundled else bare `node`, run as `node <script> args`).
Anything that matches neither falls back to `cmd.exe /d /s /c` with `%` doubled to `%%`.

## Role injection per backend (2026-09-12, corrected 2026-09-13)

- claude: `--append-system-prompt <text>`, inline — **not** `--append-system-prompt-file`, which
  does not exist on the installed CLI (`claude --help`, v2.1.269, checked while building the M1
  `ClaudeBackend`; PLAN.md A3 had flagged this exact flag as "UNCONFIRMED — verify at M1"). The
  `--agents` inline form is kept as a possible opt-in mode later. Passing the prompt inline is safe
  because it goes through `ProcessStartInfo.ArgumentList` (never a shell string) in the normal case;
  the `cmd.exe /d /s /c` fallback path still risks Windows' ~8191-char command-line limit for a very
  long role body — not yet hit in practice, worth a guard if it ever is.
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
same resolver before returning it. Parts are not allowed to reference `{{part:...}}` themselves
(no recursion guard beyond this one extra level) — none of the shipped parts do.

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

## Worktree snapshot: renames fold into Added (2026-09-13)

`git status` reports a rename as one `R` entry with an `OldPath`; `WorktreeSnapshot` records only the
new path as `ChangeKind.Added` and does not separately report the old path as removed. Revisit if a
consumer needs the old path — `GitStatusEntry.OldPath` already carries it, `WorktreeSnapshot` just
doesn't surface it in `ChangedFile` yet.

## Config layer origins for concatenated deny lists (2026-09-13)

`Config`'s per-key `Origins` map (for the future `doctor` command, docs/PLAN.md A7) records a single
winning `ConfigLayer` per key. For `deny`, which concatenates across layers instead of overwriting,
the recorded origin is the *last* layer that added anything to that role's deny list, not the full
set of contributing layers. Good enough to answer "did my config file touch this," not "which layers
built this list" — revisit if `doctor` needs the latter.
