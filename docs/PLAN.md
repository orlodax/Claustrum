# Claustrum — harness-neutral agent delegation (v1 plan)

## Context

The team is split across harnesses: some on Claude (desktop app / Claude Code), some on Cursor, some on opencode only, some on Copilot CLI. Today the owner's agent roster (`~/.claude/agents/`: architect → builder(s) → blind code-reviewer ‖ ui-reviewer → tester) only works inside Claude Code, because delegation is Claude's `Agent` tool and the role prompts are Claude-format files. The goal is one tool that lets an "architect" in **any** host delegate a task to a builder/reviewer/tester running on **any other** harness and model (e.g. DeepSeek via opencode + OpenRouter), get back a normalized result, and keep the existing pipeline discipline (blind review, honest builder reports, fixed order).

Owner decisions already taken:
- Name/repo: **Claustrum** — https://github.com/orlodax/Claustrum.git (created 2026-09-12, empty). Local clone `D:\Claustrum` (WSL `/mnt/d/Claustrum`). Must work from Windows and WSL.
- Stack: **.NET 10**, **NativeAOT single binary** per OS, cross-platform Win/mac/Linux (AOT can't cross-compile across OS → CI matrix).
- Usable from anywhere, not just IDEs: Claude desktop app, VS Code, Cursor, Claude Code CLI, opencode, Copilot CLI. Hence **two front doors over one core**: a CLI (`claustrum run …`) and a stdio **MCP server** (`claustrum mcp`) — the two hooks every host has.
- Backends v1: `claude`, `opencode`, `cursor`, `copilot`, `api` (direct chat completion, no tools). Adding a backend must be trivial.
- Scope v1: runner + role library + per-harness glue. Standalone pipeline orchestrator = phase 2 (sketched, not built).

Machine facts (2026-09-12): .NET SDK 10.0.401 (Win) / 10.0.112 (WSL); `claude` 2.1.269 (Win, WSL sees it via PATH interop); `copilot` 1.0.63 (WSL `~/.local/bin`); **opencode and Cursor `agent` not installed**; no `OPENROUTER_API_KEY`/`DEEPSEEK_API_KEY` set anywhere. One GitHub project board exists (`NeuralVibez` #4, NeuralVibez-only) → Claustrum gets its own board.

## Architecture in one picture

```
host chat (Claude Code / desktop / VS Code / Cursor / opencode / Copilot CLI)
   └─ architect role ──(MCP `delegate` | shell `claustrum run --json`)──▶ claustrum
                                                                            ├─ resolve role + config → system.md, permission policy
                                                                            ├─ git snapshot (before)
                                                                            ├─ IBackend.Build → spawn claude | opencode | agent | copilot | HTTP api
                                                                            ├─ git snapshot (after) → changed_files + diff
                                                                            └─ RunResult JSON  ◀── parsed final report
```

## Part A — Core (`src/Claustrum.Core`, `src/Claustrum`)

### A1. Solution layout
```
D:\Claustrum\
  global.json                  sdk 10.0.100, rollForward latestFeature (works with 10.0.112 and 10.0.401)
  Directory.Build.props        net10.0, Nullable, ImplicitUsings, TreatWarningsAsErrors, IsAotCompatible, InvariantGlobalization,
                               EnableTrimAnalyzer, JsonSerializerIsReflectionEnabledByDefault=false
  Directory.Packages.props     System.CommandLine 2.0.12, ModelContextProtocol 2.2.0, Microsoft.Extensions.Hosting
  Claustrum.slnx
  src/Claustrum.Core/          domain, backends, process runner, git snapshot, config, jobs, JsonSerializerContext. No CLI/MCP deps.
  src/Claustrum.Roles/         embedded role library + renderers (`sync`, `init` templates) — Part B
  src/Claustrum/               THE binary (AssemblyName claustrum, PublishAot, PackAsTool). Cli/ (System.CommandLine) + Mcp/ (`claustrum mcp` subcommand)
  tests/Claustrum.Core.Tests/, tests/Claustrum.Roles.Tests/, tests/fixtures/<backend>/*.{json,jsonl,txt}, tests/golden/<harness>/<role>.md
  scripts/smoke.ps1, scripts/smoke.sh
  .github/workflows/ci.yml, release.yml
  AGENTS.md (rules for agents working on Claustrum itself), NOTES.md (long-form rationale; code comments point here), docs/INSTALL.md, README.md
```
One executable; MCP and CLI are thin adapters over Core. Single `ClaustrumJsonContext : JsonSerializerContext` for every DTO. MCP tools registered with explicit `.WithTools<ClaustrumTools>(jsonOptions)` (exact 2.2.0 overload UNCONFIRMED — verify at M2 by AOT-publishing with zero IL2026/IL3050 warnings); tools return pre-serialized JSON strings.

### A2. Domain model & emitted contract
```csharp
enum Permission { ReadOnly, Edit, EditShell, Full }
record PermissionPolicy(Permission Level, string[] Deny);          // Deny = harness-neutral patterns: "git push", "rm -rf"
record RunRequest(string Role, string? Brief, string? BriefFile, string Cwd, string? Backend, string? Model, string? Effort,
                  PermissionPolicy? Permission, decimal? BudgetUsd, TimeSpan? Timeout, string? ResumeSession,
                  string[] AttachFiles, Dictionary<string,string> Env, bool Stream);
record ResolvedRole(string Name, string SystemPrompt, string Backend, string Model, string Effort, PermissionPolicy Permission); // Roles → Core seam
enum RunStatus { Success, Failed, Timeout, Cancelled, BackendMissing, BudgetExceeded }
record RunResult(string SchemaVersion, string JobId, RunStatus Status, string Backend, string Model, string Role, string FinalMessage,
                 ChangedFile[] ChangedFiles, string? Diff, bool DiffTruncated, string? SessionId, decimal? CostUsd, Usage? Usage,
                 int ExitCode, string LogPath, double DurationSeconds, string? Error, JsonElement? Raw, ClaustrumReport? Report);
```
Emitted JSON (snake_case; `raw` only with `--raw`; additive changes only, else bump `schema_version`):
```json
{"schema_version":"1","job_id":"20260912-140301-a1b2","status":"success","backend":"opencode","model":"openrouter/deepseek/deepseek-v4-flash",
 "role":"builder","final_message":"...","report":{...parsed claustrum-report block, see B3...},
 "changed_files":[{"path":"hello.txt","kind":"A"}],"diff":"diff --git ...","diff_truncated":false,
 "session_id":"...","cost_usd":0.012,"usage":{"input_tokens":1200,"output_tokens":300},
 "exit_code":0,"log_path":"~/.claustrum/jobs/<id>/stdout.log","duration_seconds":41.2,"error":null}
```
**Changed-files capture is harness-neutral** (`Core/Git/WorktreeSnapshot.cs`): `git status --porcelain=v1 -z --untracked-files=all` + `git diff HEAD` before and after the spawn; `changed_files` = entries that differ + new untracked; `diff` = `git diff HEAD -- <paths>` plus `--no-index` for new files, truncated at 200 KB (64 KB in MCP mode). Non-git cwd → mtime/size scan, `diff` null. Backend-reported edits are a hint only; git is truth.

### A3. Backend abstraction
```csharp
interface IBackend {
  string Name { get; }
  Task<Doctor> DetectAsync(BackendConfig cfg, CancellationToken ct);   // Found, Path, Version, Problems[]
  ProcessSpec Build(ResolvedRun run);                                   // Exe, Args[], Cwd, Env, TempFiles[]
  ParsedOutput Parse(string stdout, string stderr, int exitCode);       // FinalMessage, SessionId, Cost, Usage, ReportedEdits, Raw
}
static class BackendRegistry { /* explicit list — no assembly scanning under AOT */ }
```
Adding a backend = one class + one registry line + one fixtures dir. `Runner.RunAsync`: resolve → Build → snapshot → ProcessRunner → snapshot → Parse → extract report block → RunResult → `result.json` → delete temp files.

Role injection & argv per backend (representative: builder, EditShell, deny `git push`, prompt = `# Task\n<brief>`):
| Backend | Injection | argv (abridged) | Parse |
|---|---|---|---|
| `claude` | `--append-system-prompt-file <job>/system.md` (file-based: avoids inline JSON through the npm `.cmd` shim); optional `injection:"agents"` mode uses `--agents '{...}' --agent <role>` | `claude -p --output-format json --model X --append-system-prompt-file … --permission-mode acceptEdits --permission-prompts none --allowedTools "Edit,Write,Read,Glob,Grep,Bash(*)" --disallowedTools "Bash(git push*)" --no-session-persistence --max-budget-usd N --effort E "<prompt>"` | `json` object: `result`, `session_id`, `total_cost_usd`, `usage`, `is_error`; `stream-json` when `--stream` (last `type:result` line; `tool_use` Edit/Write → ReportedEdits) |
| `opencode` | env `OPENCODE_CONFIG_CONTENT={"agent":{"claustrum-builder":{"mode":"primary","prompt":"{file:<job>/system.md}","permission":{…}}},"permission":{"*":"allow"}}` — process-scoped, touches neither repo nor `~/.config` (UNCONFIRMED: `{file:}` with absolute path inside inline config → fallback: inline the escaped prompt string) | `opencode run --model openrouter/deepseek/deepseek-v4-flash --format json --dir <cwd> --agent claustrum-builder "<prompt>"` | JSONL; final assistant text, `session.id`, usage from step/usage events (event names UNCONFIRMED → fixture-driven at M3) |
| `cursor` | no system-prompt hook → role body **prefixed** into the prompt; deny list becomes prompt rules | `agent -p --output-format json --model X -f --workspace <cwd> "<system.md>\n\n# Task\n<brief>"` | `json` result object (fields UNCONFIRMED → capture fixture first), else text |
| `copilot` | per-job `COPILOT_HOME=<job>/copilot-home` containing `agents/claustrum-builder.agent.md` (UNCONFIRMED that COPILOT_HOME relocates agents → fallback: versioned file in `~/.copilot/agents/`, removed after) | `copilot -p "<prompt>" --output-format json --allow-all-tools --deny-tool "shell(git push)" --model X --effort E -C <cwd> --agent claustrum-builder --no-color --stream off --log-level none` | JSONL; last assistant text; session id; usage if present |
| `api` | system message = role body; user = brief; no tools | HTTP POST `https://openrouter.ai/api/v1/chat/completions` or `https://api.anthropic.com/v1/messages` chosen by model prefix `openrouter:` / `anthropic:` | `choices[0].message.content` / `content[0].text`, `usage`; `changed_files` always empty |

Permission → flags:
| Level | claude | opencode (inline `permission`) | cursor | copilot |
|---|---|---|---|---|
| ReadOnly | `--permission-mode plan --permission-prompts none` | `{"edit":"deny","bash":"deny"}` | `-f` + read-only prompt rule (was `--mode ask`; `-p` cannot answer an approval prompt — NOTES.md "The cursor backend") | `--mode plan`, no `--allow-all-tools` (non-interactive plan mode UNCONFIRMED) |
| Shell (ui-reviewer: run it, never edit it) | `plan` + `--disallowedTools Edit,Write,NotebookEdit` (MCP tools stay reachable) | `{"edit":"deny","bash":{"*":"allow","<deny>*":"deny"}}` | `-f` + read-only prompt rule | `--allow-all-tools --deny-tool write` |
| Edit | `acceptEdits` + `--disallowedTools Bash` | `{"edit":"allow","bash":"deny"}` | `-f` + prompt rule | `--allow-all-paths --allow-tool write` (tool names UNCONFIRMED) |
| EditShell (builder/tester default) | `acceptEdits --allowedTools "Edit,Write,Read,Glob,Grep,Bash(*)"` + `--disallowedTools "Bash(<deny>*)"` | `{"edit":"allow","bash":{"*":"allow","git push*":"deny"}}` | `-f` + prompt rule | `--allow-all-tools --deny-tool 'shell(git push)'` |
| Full | `--dangerously-skip-permissions` | `--auto` | `-f --sandbox disabled` | `--allow-all-tools --allow-all-paths` |
Where a backend has no native deny mechanism (cursor), the deny list is appended to the system prompt as a hard rule and `doctor` marks it "advisory".

### A4. Process execution (`Core/Process/`)
- `ProcessStartInfo{UseShellExecute=false, Redirect*, CreateNoWindow}` + `ArgumentList` (never a joined string).
- **`BinaryLocator`**: searches PATH + `PATHEXT`. On Windows `claude`/`copilot` are npm `.cmd` shims (`CreateProcess` ignores PATHEXT, and `cmd.exe` mangles `%` and long lines) → if the shim body matches `"%dp0%\node_modules\<pkg>\bin\*.exe" %*` run that exe directly; else run the `.cmd` with `%`→`%%` escaping. `backends.<name>.path` config override.
- Stdout/stderr pumped line-wise to `<job>/stdout.log`, `stderr.log`; `--stream` echoes to Claustrum's **stderr**. Result JSON only on stdout, only after exit.
- Timeout/Ctrl+C → `process.Kill(entireProcessTree: true)`; status `Timeout`/`Cancelled`. Job Objects deferred (NOTES.md).
- Env allow-list (`PATH HOME USERPROFILE APPDATA LOCALAPPDATA TEMP TMP SystemRoot ComSpec LANG SHELL TERM XDG_* *_API_KEY ANTHROPIC_* OPENROUTER_* OPENCODE_* CURSOR_* COPILOT_* GH_TOKEN GITHUB_TOKEN NODE_*`) + `--env`; `--env-passthrough all` disables filtering.
- Job dir `~/.claustrum/jobs/<yyyyMMdd-HHmmss-4hex>/{request.json,system.md,stdout.log,stderr.log,result.json}` (`CLAUSTRUM_HOME` override).
- **Path policy: Claustrum spawns backends on the OS it runs on.** Windows binary → Windows harnesses, WSL binary → Linux harnesses. Never translate `D:\` ↔ `/mnt/d`. `doctor` warns when a backend resolved from WSL is a `/mnt/c/...` Windows exe.

### A5. CLI surface (System.CommandLine 2.0.12)
```
claustrum run <role> [--brief <text> | --brief-file <path>] [--cwd] [--backend] [--model <alias|id>] [--effort]
              [--permission readonly|shell|edit|edit+shell|full] [--deny <pattern>]* [--budget <usd>] [--timeout <sec>]
              [--resume <session>] [--file <path>]* [--env K=V]* [--json] [--stream] [--raw]
claustrum roles list|show <name>        claustrum backends list|doctor [name]
claustrum jobs list [--last N]|show <id>|logs <id> [--stderr]
claustrum mcp [--cwd <dir>]             claustrum sync … / init … (Part B)
```
`--json` → exactly one RunResult document on stdout; human mode → status line, changed files, final message. Exit codes: 0 ok · 1 backend failure · 2 usage/config · 3 backend missing · 4 timeout · 5 budget · 130 cancelled.

### A6. MCP server (`src/Claustrum/Mcp/ClaustrumTools.cs`)
`Host.CreateEmptyApplicationBuilder` → logging to **stderr** → `AddMcpServer().WithStdioServerTransport().WithTools<ClaustrumTools>(…)`. Config root = launch cwd (hosts launch MCP servers in the workspace); `--cwd` and per-call `cwd` override.
| Tool | Input | Output |
|---|---|---|
| `delegate` | `{role, brief, cwd?, backend?, model?, effort?, permission?, deny?[], budget_usd?, timeout_seconds?=1800, resume_session?, files?[], include_raw?}` | RunResult (diff ≤64 KB) |
| `delegate_async` | same | `{job_id, log_path}` |
| `job_status` / `job_result` | `{job_id}` | `{state: queued|running|done, elapsed_seconds, last_line}` / RunResult |
| `list_roles`, `list_backends`, `doctor` | `{}` | summaries / full report |
Tool descriptions spell out permission semantics so the calling architect chooses deliberately.

### A7. Config (JSON only, AOT-friendly)
Resolution: built-in defaults → `~/.config/claustrum/config.json` (`%APPDATA%\claustrum\config.json`) → `<git-root>/claustrum.json` → `CLAUSTRUM_*` env → flags. Later wins per key; `deny` lists concatenate; `doctor` prints merged config with the winning layer per key.
```json
{ "$schema": ".../schema/config.schema.json",
  "models":   { "cheap-builder": "opencode:openrouter/deepseek/deepseek-v4-flash", "strong": "claude:opus", "reviewer": "api:anthropic/claude-sonnet-5" },
  "roles":    { "builder": { "model": "cheap-builder", "effort": "medium", "permission": "edit+shell", "deny": ["git push"] },
                "code-reviewer": { "model": "reviewer", "permission": "readonly" } },
  "backends": { "claude": { "path": null, "injection": "append-file" }, "api": { "openrouter_base_url": "https://openrouter.ai/api/v1" } },
  "defaults": { "timeout_seconds": 1800, "budget_usd": 5, "env_passthrough": "allowlist" },
  "jobs":     { "keep_last": 200 } }
```
Model grammar `[backend:]<model-id>`; aliases resolve recursively (depth 3). Role files (Part B) reference **model classes** (`frontier-coding`, `cheap-coding`, …) that are themselves aliases in this file, so a Claude user and a DeepSeek-only user share one role file.

### A8. CI / release
- `ci.yml` (push/PR): `dotnet test` on ubuntu + windows; `dotnet publish -r linux-x64 -p:PublishAot=true` on ubuntu (needs `clang zlib1g-dev`) to catch trim warnings early.
- `release.yml` (tag `v*`): matrix ubuntu-latest→linux-x64, ubuntu-24.04-arm→linux-arm64, windows-latest→win-x64, macos-13→osx-x64, macos-latest→osx-arm64; publish AOT, rename `claustrum-<rid>[.exe]`, `SHA256SUMS.txt`, `softprops/action-gh-release`; plus `dotnet pack` tool package → NuGet when `NUGET_API_KEY` is set.

## Part B — Role library & harness glue (`src/Claustrum.Roles`, `roles/`)

### B1. What ports from `~/.claude/agents/` and what doesn't
Load-bearing passages in the seed files (they become invariants, not prose):
- builder.md:21 "This repo's CLAUDE.md and AGENTS.md are law" → shared `{{house_rules}}` part, verbatim.
- architect.md:47-56 "Use the Agent tool with `subagent_type: "builder"` … `run_in_background: false`" → the **only** Claude-specific content besides tool names (`Agent`, `Bash`, `mcp__Claude_Browser`). Becomes a per-harness `delegation` part.
- architect.md:145 fixed order builder(s) → code-reviewer ‖ ui-reviewer → tester → neutral, unchanged.
- architect.md:169-201 blind review ("the task as originally stated, and the diff. Nothing else… withhold your plan, rationale, the builders' reports") → enforced by the runner (B3), not by trust.
- builder.md:54-56 honest report (files+why, behaviour to cover, open decisions, shaky) → the four fields of the builder report block.
- architect.md:251-278 tier ladder → `tiers` in `role.json` expressed as model **classes**.
- The `-xhigh`/`-max` stubs are already thin → generated from a template, never hand-written.

### B2. Library format (single source of truth, embedded in the binary)
```
roles/library.json               {"version":"1.0.0","classes":["frontier-reasoning","frontier-coding","standard-coding","cheap-coding","fast"],"tiers":["high","xhigh","max"]}
roles/_shared/house-rules.md     the "law" paragraph
roles/_shared/report/<role>.md   report-format section + JSON schema per role
roles/_shared/tier-stub.md       template for <role>-xhigh / <role>-max
roles/<role>/ROLE.md             harness-neutral body with {{tokens}}
roles/<role>/role.json           metadata (below)
roles/<role>/parts/<part>.<harness>.md   e.g. delegation.claude.md, delegation.default.md
```
`role.json` (builder):
```json
{"name":"builder","description":"…","color":"blue","blind":false,
 "tiers":{"high":{"model":"frontier-coding","effort":"high"},"xhigh":{"model":"frontier-coding","effort":"xhigh"},"max":{"model":"frontier-coding","effort":"max"}},
 "permission":"edit+shell","deny":["git push"],"mayDelegate":["architect"],
 "report":"builder","harnesses":["claude","opencode","cursor","copilot"],
 "nonNegotiable":["…2-4 lines restated in tier stubs…"]}
```
code-reviewer: `blind:true`, `permission:"readonly"`, tiers high→`standard-coding`, xhigh/max→`frontier-coding`, `harnesses` includes `api`. ui-reviewer: `harnesses:["claude"]` in v1 (needs the Browser MCP), `permission:"shell"` — it starts a dev server and drives a browser but never edits, which is exactly the rung between readonly and edit+shell. Model classes resolve through `claustrum.json.models` (A7), so one role file serves a Claude user and a DeepSeek-only user.

**Templating**: plain `{{token}}` replacement, no engine. Tokens: `{{harness}} {{role}} {{tier}} {{effort}} {{house_rules}} {{report_format}} {{part:<name>}}` (loads `parts/<name>.<harness>.md`, falls back to `.default.md`) and `{{delegate.<role>}}` (one-line invocation for the target harness). Unknown token = render error. `parts/delegation.default.md` carries the new rule: *"If the target backend is your own harness and it has native subagents, use them; otherwise call Claustrum — MCP `delegate` if the `claustrum` server is connected, else shell `claustrum run --json …`."*

Renderer API consumed by Core: `RoleRenderer.Render(role, tier, backend, cwd) → RenderedRole{SystemBody, ModelClass, Effort, Permission, ReportSchema, Blind}` and `ReportExtractor.Extract(text) → ClaustrumReport?`. `RenderedRole` is what `Config.Resolve` turns into A2's `ResolvedRole`.

### B3. Headless injection, brief convention, blind gate
**SystemBody** = (1) ROLE.md rendered for `harness=<backend>` + (2) `## House rules` ("Read `AGENTS.md` and `CLAUDE.md` in the working directory first; they are law") + (3) `## Report format` ("Your final message ends with exactly one fenced block tagged `claustrum-report` containing JSON matching: …"). The **brief goes in the user prompt**, never the system body (api backend: system + user). Injection per backend is the table in A3 (reconciled: Claude = `--append-system-prompt-file` by default; opencode = prompt string **inlined** in `OPENCODE_CONFIG_CONTENT`, no `{file:}` dependency; Copilot = per-job `COPILOT_HOME` if it relocates `agents/`, else jobid-suffixed file in `~/.copilot/agents/` removed afterwards).

Report schemas (`_shared/report/*.md`, extracted into `RunResult.report`):
- builder: `{status: done|partial|blocked, summary, files_changed:[{path,why}], behaviour_to_cover:[], open_decisions:[], shaky:[], departed_from_brief:[]}`
- code-reviewer / ui-reviewer: `{status, findings:[{severity, verdict: CONFIRMED|PLAUSIBLE, file, line, scenario, steps?, evidence?}], would_change_if_broader:[]}`
- tester: `{status, commands_run:[], passed, failed:[{test, root_cause, fault_in: test|code}], skipped:[]}`
Extraction rules: exactly one block expected; none → `report:null`, status stays but `report_status:"missing"`; several → last wins + warning; nested inside another fence → inner found; invalid JSON → `report_status:"unparsed"`, raw kept.

**Brief convention**: markdown with fixed H2s — `## Task` (user's words verbatim), `## Scope` (paths), `## Must still work`, `## Diff` (how to obtain it, e.g. `git diff main...HEAD`), `## Access` (ui-reviewer), `## Context` (non-blind roles only). Passed as `--brief-file` / `--brief` / stdin (`--brief-file -`), or MCP `brief`.
**Blind gate, by construction**: for roles with `blind:true` the runner rejects a brief containing `## Context`, `## Plan`, `## Rationale`, or a pasted `claustrum-report` block → exit 2, `"blind role: brief carries rationale"`. The MCP `delegate` tool description states this. Refusal, not advice, enforces architect.md:197.

### B4. `claustrum sync` — render the library into every harness
| Harness | Roles | Delegation skill/command | MCP snippet (merged, only the `claustrum` key touched) |
|---|---|---|---|
| claude | `.claude/agents/<role>.md` + generated tier stubs | `.claude/skills/delegate/SKILL.md` | `.mcp.json` → `mcpServers.claustrum` |
| opencode | `.opencode/agents/<role>.md` (`mode: subagent`, `permission` block, body inlined) | `.opencode/commands/delegate.md` (`$1` role, `$2` brief path, body runs `` !`claustrum run …` ``) | `opencode.json` → `mcp.claustrum` (`type:"local"`, `command:[…]`) |
| cursor | `.cursor/agents/<role>.md` (`name, description, model, readonly`) | `.cursor/skills/delegate/SKILL.md` | `.cursor/mcp.json` |
| copilot / VS Code | `.github/agents/<role>.agent.md` | `.github/prompts/delegate.prompt.md` | `.vscode/mcp.json` (`servers`, `type:"stdio"`) + `.mcp.json` (Copilot reads it too; key shape UNCONFIRMED) |
`--global` targets `~/.claude/agents`, `~/.config/opencode/agents`, `~/.copilot/agents`, and `%APPDATA%\Claude\claude_desktop_config.json` (absolute binary path).
- Idempotency: first body line `<!-- claustrum:generated role=builder harness=claude library=1.0.0 sha256=… -->`; JSON targets tracked in `.claustrum/sync-manifest.json`. Files without marker/manifest entry are **never overwritten** (`--force` to adopt). The owner's `mc-*.md` are therefore untouched.
- Flags: `--only claude,opencode`, `--roles a,b`, `--dry-run` (unified diff), `--check` (exit 2 on hand-edited/stale/missing; for CI), `--global`, `--force`.
- Local override: `<repo>/.claustrum/roles/<role>/{ROLE.md,role.json,parts/}` wins over the library (role.json deep-merged); header records `source=local`.

### B5. In-chat UX per host (the thing teammates actually do)
Human: "delegate this to a cheap builder". The architect writes `.claustrum/briefs/<n>.md`, then:
- **Claude Code / desktop app, MCP connected** → `delegate({role:"builder", tier:"high", model:"cheap-coding", brief_path})` blocks, returns RunResult; architect triages `report.open_decisions`/`shaky`, then `delegate({role:"code-reviewer", brief_path: <task+scope+must-still-work+diff-ref only>})`.
- **Claude Code, shell only** → `Bash: claustrum run builder --tier high --model cheap-coding --brief-file .claustrum/briefs/1.md --json` (blocking by nature — removes the `run_in_background` hazard). Reviewer: `claustrum run code-reviewer --backend api --brief-file 2.md --json`.
- **opencode** → `/delegate builder .claustrum/briefs/1.md` (command's `` !`claustrum run …` `` runs at expansion time → skill text says "write the brief first, then invoke") or `@architect` with MCP.
- **Cursor** → `/architect` subagent → terminal tool `claustrum run …` or MCP `delegate`. Compromise: no `tools` field, so "architect never edits code" is prose-only.
- **Copilot CLI** → `copilot --agent architect` → MCP `delegate` or shell. Compromise: per-agent `model` UNCONFIRMED in CLI → tier lives in the delegate call.
- **VS Code agent mode** → `architect` custom agent + `/delegate` prompt file + `.vscode/mcp.json`. Long builds may hit the client tool timeout → use `delegate_async` + `job_result`.
In every host the architect's reply quotes status, changed files, `shaky`, and which reviewer tier it chose and why (architect.md:279).

### B6. Bootstrap, doctor, docs, governance
- `claustrum init [--all]`: writes `claustrum.json` (library version, default model aliases incl. `cheap-coding → opencode:openrouter/deepseek/deepseek-v4-flash`, `frontier-coding → claude:opus`, …), runs `sync` for harnesses detected by `.claude/ .cursor/ .github/ opencode.json` presence, merges MCP snippets, appends a 3-line "Claustrum delegation" pointer to `AGENTS.md` if present (never creates `CLAUDE.md`).
- `claustrum doctor [--probe]`: per backend `binary` (path/version) · `auth` (env key or login file present — value never printed) · `probe` (1-token "reply OK" call, cost shown) · `mcp` (which config files register claustrum) · `os` (warns when binary path and cwd mix `C:\` and `/mnt/c`).
- `docs/INSTALL.md`: 1 get binary (Release asset per OS; `dotnet tool install -g claustrum`) · 2 install the backends you want (`npm i -g @anthropic-ai/claude-code`, `npm i -g opencode-ai`, Cursor `agent` installer, `npm i -g @github/copilot`) · 3 keys (`OPENROUTER_API_KEY`, `ANTHROPIC_API_KEY`, `DEEPSEEK_API_KEY`, `CURSOR_API_KEY`, `GH_TOKEN`) · 4 `claustrum init && claustrum doctor` · 5 **Windows/WSL rule**: run Claustrum and its backends on the same OS as your host; never point a WSL Claustrum at `C:\` paths or vice versa · 6 per-host MCP hookup.
- Governance: roles change by PR to `orlodax/Claustrum` (linear history, rebase); `library.json.version` semver; `sync --check` in consumers' CI flags drift. The owner's `~/.claude/agents/` becomes `claustrum sync --global --only claude` output — first adoption once with `--force --roles architect,builder,code-reviewer,tester,ui-reviewer`.

## Part D — Casts and the `/claustrum` entry point (how a human actually uses it)

### D1. The cast: who plays which role, chosen once, stored as a file
```json
// .claustrum/casts/default.json  (committable; teammates share `cheap`, `premium`, `deepseek-only`)
{ "name": "default", "library": "1.0.0",
  "architect":     { "mode": "host" },                                   // or {"mode":"spawned","model":"claude:opus","tier":"xhigh"}
  "builder":       { "model": "opencode:openrouter/deepseek/deepseek-v4-pro", "max_parallel": 3 },
  "code-reviewer": { "model": "claude:opus", "tier": "xhigh" },
  "ui-reviewer":   null,                                                  // "not needed" for this cast
  "tester":        { "model": "opencode:openrouter/deepseek/deepseek-v4-flash" },
  "budget_usd": 10, "permission_overrides": {} }     // "budget_usd": null  ⇒ cap disabled
```
Budget cap is optional: `null` (or `--budget none`, or answering "no cap" in the questionnaire) disables it for the cast; per-call `--budget` still applies if given. When disabled, no `--max-budget-usd` is passed to backends and `BudgetExceeded` can never fire; `doctor`/`cast show` print `budget: unlimited` so the choice is visible.
Any value may be an alias from `claustrum.json.models`. Every `delegate`/`run` accepts `--cast <name>` (default: `.claustrum/casts/default.json` if present); explicit `--model`/`--backend` flags still override per call.

### D2. Claustrum owns the questionnaire — the chat-agent is only the UI
- `claustrum cast questions [--json]` / MCP `cast_questions` → ordered list: architect (host | spawned on …), builder model + `max_parallel`, code-reviewer, ui-reviewer, tester, budget (amount or **"no cap"**), plus "reuse existing cast <x>?" when one exists. Each question carries **live options**: only backends `doctor` finds installed *and* authenticated, the aliases in `claustrum.json`, a free-form `backend:model` escape, and "not needed" for every role but builder.
- The synced per-harness skill is named **`claustrum`** (so `/claustrum` works in Claude Code, Cursor, opencode, Copilot/VS Code; it replaces the `delegate` skill in B4). Its body: "run `claustrum cast questions --json` (or the MCP tool), ask the user each question with your native question mechanism, then `claustrum cast create --answers <file>` (or `cast_create`), then proceed with `--cast`". Identical questions in every host by construction; only the picker fidelity differs (AskUserQuestion in Claude Code vs plain text elsewhere).
- Interactive TTY fallback: `claustrum cast new` asks the same questions itself (System.CommandLine + simple console prompts) for people working outside any chat.
- MCP **elicitation** (server-initiated structured questions) is used where the host supports it (UNCONFIRMED per host) — same question objects, no code duplication; chat-agent path remains the guaranteed route.

### D3. Two architect modes
- **`host`**: the agent the human is chatting with adopts the synced `architect` role and issues `delegate` calls itself (B5). Works in every host, uses the host's subscription.
- **`spawned`**: `claustrum coordinate --cast <name> [--issues 12,13 | --brief-file task.md]` runs the `architect` role headlessly on the chosen backend (`claude -p` uses the Claude subscription OAuth) with the cast injected into its system body and `Bash(claustrum *)` allowed; the architect prompt *is* the pipeline and calls Claustrum recursively for builders → reviewers → tester. Claustrum supplies: the cast, the blind gate, the parallel cap, budget accounting across the whole tree (`CLAUSTRUM_PARENT_JOB` env links child jobs to the coordinate job), and progress via `job_status` so the chat-agent relays it. This is the minimal v1 form of the phase-2 orchestrator: no state machine in C#.
- `--issues`: `gh issue view <n> --json title,body,labels` becomes the brief's `## Task`; `gh` presence is a `doctor` check; the architect's final commit/PR text cites `Closes #<n>` (owner's rule 3).

### D4. Parallel builders need isolation and a real cap
- Each builder job with `max_parallel > 1` gets its own `git worktree add .claustrum/worktrees/<job> -b claustrum/<job>`; `changed_files`/`diff` are computed inside the worktree; result carries `worktree` and `branch`. The architect integrates by **rebase onto the target branch** (owner's rule 2), never merge commits; `claustrum jobs clean` removes worktrees of finished jobs.
- `max_parallel` is enforced by a per-cast semaphore in `JobManager` (both CLI and MCP paths go through it); a spawned architect that over-fans simply waits. `budget_usd` is enforced across the job tree: a child that would exceed the remaining budget is refused with status `BudgetExceeded`.

### D5. Surface additions (delta to A5/A6)
- CLI: `claustrum cast new|questions|create --answers <file>|list|show <name>|use <name>`; `claustrum coordinate --cast <name> [--issues …|--brief-file …] [--json]`; `claustrum run … --cast <name>`; `claustrum jobs clean`.
- MCP: `cast_questions`, `cast_create`, `cast_list`, `coordinate` (async by nature → returns job id; progress via `job_status`), and `cast` accepted on `delegate`.
- `.claustrum/` is created by `init` with `casts/`, `briefs/`, `worktrees/` (the last two git-ignored).

## Part C — GitHub workflow for the repo itself
- Step 0 clones `https://github.com/orlodax/Claustrum.git` to `D:\Claustrum`; the empty repo needs `main` created by the first push (`git push -u origin main`), then branch protection: require linear history.
- **New project board "Claustrum"** (the existing `NeuralVibez` #4 is scoped to NeuralVibez). One epic issue per milestone (M0–M4) + child issues per component; each issue goes on the board with status (Ready / in the current iteration for M0–M1) **and** Start/Target dates so the Roadmap view shows them (owner's rule 3). Field ids get recorded in this repo's memory once the board exists.
- Repo docs: `AGENTS.md` (rules for agents working on Claustrum: comment cap, NOTES.md pointer rule, rebase-only, run `dotnet test` + AOT publish before PR), `NOTES.md` (rationale for shim resolution, permission table, path policy, blind gate, injection choices).

## Milestones (execution order)
| M | Lands | Done when |
|---|---|---|
| **M0 scaffold** | clone, `global.json`, props, `Claustrum.slnx`, 3 projects + 2 test projects, `AGENTS.md`, `NOTES.md`, `ci.yml`, board + issues | `dotnet build` and `dotnet publish -p:PublishAot=true` succeed on Windows **and** WSL with zero trim warnings; `claustrum --version` runs |
| **M1 core + claude** | A2 model, A7 config, ProcessRunner + BinaryLocator, WorktreeSnapshot, `claude` backend, `run`/`jobs`/`backends doctor`; roles: library format, `builder` + `code-reviewer` ported, renderer, report extractor, blind gate, `sync --only claude`, `.claude/skills/delegate` | argv/parser/golden unit tests green; `scripts/smoke.ps1` passes for `claude` on Windows; `claustrum run builder --backend claude` on a toy repo returns a parsed report and `hello.txt` in `changed_files` |
| **M2 MCP + casts** | all MCP tools incl. `cast_questions`/`cast_create`, `.mcp.json` + `.vscode/mcp.json` merge, `tester` role, cast file + `cast` CLI verbs + `/claustrum` skill (claude), `--cast` on `run`/`delegate`, `sync --check/--dry-run` | from Claude Code with the MCP server connected: `/claustrum` interviews → writes `.claustrum/casts/default.json` → `delegate` builder → blind `delegate` code-reviewer completes hands-off using the cast; AOT publish still zero warnings |
| **M3 backends + parallel** | `opencode`, `cursor`, `copilot`, `api` backends with recorded fixtures; their renderers + `/claustrum` skills; `ui-reviewer` (claude only); worktree isolation + `max_parallel` semaphore + tree budget; `init`, `doctor`, `sync --global` | `backends doctor` + `smoke.sh` green in WSL for every installed backend (copilot, opencode after install); 3 parallel builders on a toy repo land on 3 branches with no clobbering; `sync --global --only claude` reproduces the owner's five files with only header diffs; cursor validated by a teammate who has it |
| **M4 coordinate + release** | `architect` role (last — it issues the delegations), `coordinate --cast --issues` (spawned architect, `gh` issue import, `Closes #n`), `INSTALL.md`, `release.yml` (5 RIDs + checksums + tool package), tier stubs generated | `claustrum coordinate --cast default --issues <n>` on a toy repo runs architect(opus) → 2 deepseek builders → opus review → flash tester unattended and leaves a rebased branch; tagged `v0.1.0` installs via binary on both OSes and via `dotnet tool install -g claustrum`; a fresh clone + INSTALL.md lets a teammate `/claustrum` from two different hosts |

Phase 2 (not built now): `claustrum pipeline pipeline.json` — stages architect(api) → builders[] (parallel, git worktrees) → reviewer → tester, each stage a `RunRequest`; the runner feeds the reviewer only task + `RunResult.diff` (blind by construction), gates on `status`/verdict, aggregates one report.

## Verification (end-to-end)
1. **Build gate** on both OSes: `dotnet test` and `dotnet publish -c Release -r <rid> -p:PublishAot=true` → zero IL2026/IL3050 warnings; binary size and startup (<50 ms) sanity.
2. **Unit**: per-backend golden argv on both platforms (injected `IPlatform`); parsers against `tests/fixtures/<backend>/{success.json|jsonl, error.txt, timeout.txt}`; `WorktreeSnapshot` on a temp git repo; config layering; `BinaryLocator` with a fake `.cmd` shim; renderer golden files (5 roles × 4 harnesses + api); sync idempotency (render twice → no diff; hand-edit → `--check` exit 2; foreign keys in `.mcp.json` preserved); blind gate; report extraction edge cases.
3. **Smoke** (`scripts/smoke.ps1` / `smoke.sh`): temp `git init` → for each backend `doctor` finds: `claustrum run builder --brief "create hello.txt containing hi" --json --cwd <tmp>` → assert `status=="success"` and `hello.txt ∈ changed_files`; table output; nonzero exit on any failure.
4. **In-chat**: from this very session (Claude desktop, Code tab) with `claustrum mcp` registered in `.mcp.json`: `/claustrum` → answer the 7 questions → cast written → `delegate` a builder to `claude:sonnet`, then (after opencode install + `OPENROUTER_API_KEY`) to `cheap-coding`, then blind-review via `api`. From WSL: same via `copilot --agent architect`. Then the spoken-sentence test: *"use claustrum to coordinate issues #1 #2 with an opus architect, 2 deepseek-v4-pro builders, opus review, flash tester"* → `coordinate` runs unattended.
4b. **Cast questionnaire parity**: `claustrum cast questions --json` output is byte-identical whether called from CLI, MCP, or the synced skill in each host; option lists shrink correctly when a backend is missing or unauthenticated.
5. **Sync round-trip**: `claustrum sync --global --only claude --dry-run` against the owner's real `~/.claude/agents/` shows header-only diffs for the five roles and does not touch `mc-*.md`.

## UNCONFIRMED items to verify at the milestone that needs them
- M1: `ModelContextProtocol` 2.2.0 `.WithTools<T>(JsonSerializerOptions)` overload name under AOT; `--agent` vs `--append-system-prompt-file` behaviour for the main turn in `claude -p`.
- M3: opencode `run --format json` event names; whether `openrouter/deepseek/<id>` is the exact three-segment form; Cursor `agent --output-format json` fields; Copilot `COPILOT_HOME` relocating `agents/`, non-interactive `--mode plan`, tool names for `--allow-tool`; Copilot's `.mcp.json` key shape.
- Cursor CLI cannot be smoke-tested on this machine (not installed, subscription unknown) — its backend ships behind fixtures + a teammate's validation.

## Not in v1 (explicit)
Pipeline orchestrator; `opencode serve`/ACP/Copilot SDK transports (all shell-out for now); YAML config; ui-reviewer on non-Claude harnesses; automatic Windows↔WSL path translation; `mc-*` metaconcert roles.
