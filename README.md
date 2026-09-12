# Claustrum

Harness-neutral coding-agent delegation. An "architect" in any host — Claude desktop app, Claude
Code, VS Code, Cursor, opencode, GitHub Copilot CLI — delegates builds, reviews and test runs to
agents running on **any other** harness and model (Claude, DeepSeek via opencode + OpenRouter,
Copilot, Cursor, or a bare API call), and gets back one normalized result.

Named after the claustrum, the thin sheet of neurons Crick and Koch proposed as the conductor that
binds the brain's specialised regions into one coherent act.

## Status

Pre-alpha. See `docs/PLAN.md` for the milestones and `AGENTS.md` for contribution rules.

## Two front doors, one core

```
claustrum run builder --brief-file task.md --model cheap-coding --json   # shell
claustrum mcp                                                            # stdio MCP server: delegate, cast_questions, …
```

## Build

```bash
dotnet build
dotnet test
dotnet publish src/Claustrum/Claustrum.csproj -c Release -r win-x64 -p:PublishAot=true
```
