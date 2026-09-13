# Working on Claustrum

Rules for any agent or human changing this repository. Design rationale lives in `NOTES.md`; the
approved plan and milestone definitions are summarised in `docs/PLAN.md`.

## Build and gate

- `dotnet build` and `dotnet test` must pass on Windows and Linux (WSL counts). Tests are xunit.v3
  on Microsoft.Testing.Platform, opted in through `"test": {"runner": …}` in `global.json` — the
  .NET 10 SDK refuses the legacy VSTest path otherwise (bit CI on the first push, 2026-09-12).
  Never pass `-nologo`/`--nologo` to `dotnet test`: the flag is forwarded to the test app and the run
  reports "Zero tests ran" with exit code 5 (measured 2026-09-13 on SDK 10.0.401).
- `dotnet publish src/Claustrum/Claustrum.csproj -c Release -r <rid> -p:PublishAot=true` must produce
  **zero** IL2026/IL3050 trim warnings. Warnings are errors in this repo.
- Solution-level `dotnet publish -c Release -r <rid> -p:PublishAot=true` also works: the test projects
  declare `PublishAot` as a local property (xunit is not trim-safe), so the CLI flag skips them.
- Windows AOT needs the VS "Desktop development with C++" workload (MSVC + Windows SDK). WSL AOT
  needs `clang` or `gcc` + `zlib1g-dev`; with gcc only, add `-p:CppCompilerAndLinker=gcc`.
- Building the same checkout from both Windows and WSL: give WSL its own output tree so `obj/` does
  not mix path styles — `-p:UseArtifactsOutput=true -p:ArtifactsPath=$HOME/claustrum-artifacts`.
- All JSON goes through `ClaustrumJsonContext` (source-generated). No reflection-based serialization,
  no assembly scanning: backends and MCP tools are registered explicitly.

## Code style

Machine-enforced by `.editorconfig` + `Directory.Build.props` (`EnforceCodeStyleInBuild`,
`AnalysisLevel=latest-recommended`, `TreatWarningsAsErrors`). The principles below decide the
cases the machine cannot; when a rule is ambiguous, resolve it toward them.

### Principles
- **Severe typing.** Nothing implicit that could be explicit and meaningful.
- **Reading speed and information density.** Favor concise forms; don't waste lines.
- **The type appears once, on the line that assigns it.** `RunRequest r = new(...)` — never
  `var r = new RunRequest(...)`. `var` only when the type would otherwise repeat on the line, or is
  long/nested/derived (`Dictionary<string, List<ChangedFile>>`).
- **Latest C# first.** Target the newest language version and use its idioms by default: file-scoped
  namespaces, primary constructors, records, collection expressions `[...]`, pattern matching and
  switch expressions, `is null`/`is not`, raw string literals, target-typed `new()`, ranges/indices.
  When a newer construct expresses the same thing in fewer tokens, it wins.
- **Zero warnings, zero lingering hints.** The gate is 0 warnings, and the IDE should not be showing a
  trail of "consider applying…" either. Every style analyzer is therefore either `warning` (a house
  rule, fixed at once) or `none` (consciously not a rule) — nothing sits at `suggestion`. If a new
  hint appears that we do not want, add it to the `none` block in `.editorconfig` with a reason;
  never leave it as info.
- **Editor token colours carry meaning.** Private fields render distinctly from locals, so a `_`
  prefix and `this.` are redundant noise.
- **Two audiences.** A rule a reviewer cannot cheaply verify in a colour-less GitHub diff is either
  machine-enforced or consciously accepted as drift-prone — never unspoken discipline.

### Rules
- **Declarations:** explicit types + short `new()`; `record` for immutables (DTOs, results,
  config); primary constructors for DI-only classes; `readonly` fields when assigned only in the
  constructor; access modifiers always explicit (interface members excepted); prefer
  `private`/`internal`/`sealed`.
- **Nullability:** `<Nullable>enable</Nullable>` and every warning is an error. No `!`
  null-forgiving as a shortcut — fix the root cause; allowed only with a justifying comment where
  the type system cannot express the invariant.
- **Naming:** `I`-prefixed interfaces; private fields `camelCase` with no `_` (private `const` stay
  `PascalCase`); `Async` suffix on async methods; no `Dto`/`Impl` suffix noise.
- **Control flow:** a body never shares the condition's line — `if (x is null) return;` is out, the
  `return` goes on its own line. Braces are then context-driven: omit for single-line condition +
  single-line body; use them when the condition wraps or the body is more than one line.
- **Expression bodies:** `=>` only when it fits one line; otherwise a block body.
- **LINQ:** method syntax by default.
- **Organization:** one type per file (a small private helper/record may share it); no `#region`
  (split instead); group by feature, not by technical kind. Prefer records and small interfaces
  (`IBackend`, `IPlatform`) over inheritance hierarchies.
- **Comments:** self-documenting names; comments say *why*. Inline 1-2 line why-comments at a step
  are welcome; comments restating *what* the code does are not. A contiguous `//` block is at most
  8 lines (12 for `///`); anything longer moves whole into `NOTES.md` under a titled section, and
  the code keeps a 1-2 line pointer to it. **The ceiling redirects text, it never licenses deleting
  it**: dated decisions, measured numbers and named traps stay in the code.
- **XML docs** on `public` members only where the contract is not obvious from the signature.
- **Formatting:** `dotnet format --verify-no-changes` must report nothing; run `dotnet format`
  rather than hand-formatting. Applying a style rule to existing code is mechanical
  (`dotnet format style`), never a logic edit.

## Git and GitHub

- Linear history: rebase onto `main`, then fast-forward. Never create merge commits.
- Every GitHub issue goes on the project board (https://github.com/users/orlodax/projects/6) with a
  Status and Start/Target dates in the same step that creates it.
- Commits that resolve an issue cite it (`Closes #n`).

## Layout

| Path | Owns |
|---|---|
| `src/Claustrum.Core` | domain model, backends, process runner, git snapshot, config, jobs |
| `src/Claustrum.Roles` | embedded role library (`roles/`), renderers, `sync`/`init` templates |
| `src/Claustrum` | the single binary: CLI verbs and the `claustrum mcp` server |
| `tests/` | xunit projects, `fixtures/<backend>/` recorded outputs, `golden/<harness>/` renders |
| `scripts/` | `smoke.ps1` / `smoke.sh` end-to-end checks |
