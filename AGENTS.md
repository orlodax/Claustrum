# Working on Claustrum

Rules for any agent or human changing this repository. Design rationale lives in `NOTES.md`; the
approved plan and milestone definitions are summarised in `docs/PLAN.md`.

## Build and gate

- `dotnet build` and `dotnet test` must pass on Windows and Linux (WSL counts).
- `dotnet publish src/Claustrum/Claustrum.csproj -c Release -r <rid> -p:PublishAot=true` must produce
  **zero** IL2026/IL3050 trim warnings. Warnings are errors in this repo.
- Windows AOT needs the VS "Desktop development with C++" workload (MSVC + Windows SDK). WSL AOT
  needs `clang` or `gcc` + `zlib1g-dev`; with gcc only, add `-p:CppCompilerAndLinker=gcc`.
- Building the same checkout from both Windows and WSL: give WSL its own output tree so `obj/` does
  not mix path styles — `-p:UseArtifactsOutput=true -p:ArtifactsPath=$HOME/claustrum-artifacts`.
- All JSON goes through `ClaustrumJsonContext` (source-generated). No reflection-based serialization,
  no assembly scanning: backends and MCP tools are registered explicitly.

## Code style

- Comment blocks: max 8 lines (doc comments max 12). Longer explanations move whole into `NOTES.md`
  under a titled section, and the code keeps a 1-2 line pointer to that section.
- Inline 1-2 line comments explaining *why* a step exists are welcome; comments restating *what* the
  code does are not.
- Prefer records and small interfaces (`IBackend`, `IPlatform`) over inheritance hierarchies.

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
