# Installing Claustrum

For a teammate on a fresh machine. Claustrum is one binary; everything it delegates to — Claude
Code, opencode, Copilot CLI, `cursor-agent` — is installed separately, and you only need the ones
you intend to use. Each step below gives the Linux/macOS form and the Windows form.

What Claustrum is and why is in the [README](../README.md); the full design is in
[docs/PLAN.md](PLAN.md); the rules for changing this repo are in [AGENTS.md](../AGENTS.md).

## 1. Get the binary

**Either** a release asset, **or** the .NET global tool, **or** a build from source. Pick one.

### a. Release asset (no .NET needed)

Each release publishes one self-contained binary per runtime identifier, plus `SHA256SUMS.txt`:

| RID | Asset |
|---|---|
| `linux-x64` | `claustrum-linux-x64` |
| `linux-arm64` | `claustrum-linux-arm64` |
| `osx-x64` | `claustrum-osx-x64` |
| `osx-arm64` | `claustrum-osx-arm64` |
| `win-x64` | `claustrum-win-x64.exe` |

Download the one for your machine from the [Releases
page](https://github.com/orlodax/Claustrum/releases), check it against `SHA256SUMS.txt`, and put it
on `PATH` under the name `claustrum`:

```bash
# Linux / macOS
sha256sum --ignore-missing -c SHA256SUMS.txt        # macOS: shasum -a 256 --ignore-missing -c
install -m 0755 claustrum-linux-x64 ~/.local/bin/claustrum
claustrum --version
```

```powershell
# Windows (PowerShell)
(Get-FileHash .\claustrum-win-x64.exe -Algorithm SHA256).Hash.ToLower()   # compare with SHA256SUMS.txt
New-Item -ItemType Directory -Force "$env:LOCALAPPDATA\Programs\claustrum" | Out-Null
Move-Item .\claustrum-win-x64.exe "$env:LOCALAPPDATA\Programs\claustrum\claustrum.exe"
# then add that folder to PATH (System settings → Environment Variables), reopen the shell:
claustrum --version
```

`~/.local/bin` has to be on your `PATH` for the Linux/macOS form. On macOS a binary downloaded
through a browser is quarantined by Gatekeeper; clear it with
`xattr -d com.apple.quarantine ~/.local/bin/claustrum`.

### b. .NET global tool

Needs the [.NET 10 SDK](https://dotnet.microsoft.com/download) on `PATH`, and `~/.dotnet/tools`
(Windows: `%USERPROFILE%\.dotnet\tools`) on `PATH` too:

```bash
dotnet tool install -g Claustrum
```

NuGet package ids are case-insensitive, so `claustrum` works as well. Every release also attaches
the `.nupkg` itself, for an install from a file: `dotnet tool install -g Claustrum --add-source .`

### c. From source

Needs the .NET 10 SDK plus the native toolchain NativeAOT links against — the same prerequisites
[AGENTS.md](../AGENTS.md) states for this repo:

- **Linux**: `clang` **or** `gcc`, plus the **zlib development headers** (`zlib1g-dev` on
  Debian/Ubuntu, `zlib-devel` on Fedora/RHEL/SUSE — ask your package manager rather than copying a
  name). With gcc and no clang, add `-p:CppCompilerAndLinker=gcc`.
- **Windows**: the Visual Studio **"Desktop development with C++"** workload (MSVC + Windows SDK).
- **macOS**: the Xcode command line tools.

```bash
git clone https://github.com/orlodax/Claustrum.git
cd Claustrum
dotnet publish src/Claustrum/Claustrum.csproj -c Release -r linux-x64 -p:PublishAot=true
# → src/Claustrum/bin/Release/net10.0/linux-x64/publish/claustrum
```

Substitute your own RID from the table above (`win-x64` produces `claustrum.exe`).

## 2. Install the backends you want

A backend is the coding agent Claustrum actually launches. None of them is bundled; install only
what you will use, **on the same OS as the Claustrum binary** (see step 5).

| Backend | Install | Notes |
|---|---|---|
| `claude` | `npm i -g @anthropic-ai/claude-code` | Uses your Claude subscription login or `ANTHROPIC_API_KEY`. |
| `opencode` | `npm i -g opencode-ai` | The usual route to OpenRouter/DeepSeek models. |
| `copilot` | `npm i -g @github/copilot` | Authenticates with your GitHub account. |
| `cursor` | Cursor's own `cursor-agent` installer script | Beware: the `cursor-agent` **npm** package is an unrelated third-party tool, not Cursor's CLI (NOTES.md). On a Free plan only `--model auto` is accepted. |
| `api` | nothing but `curl` on `PATH` | A direct HTTP call, no tools — for reasoning-only roles such as a blind reviewer. |

The npm ones need Node.js. `gh` ([GitHub CLI](https://cli.github.com/)), authenticated, is a
separate prerequisite for `claustrum coordinate --issues`, which reads each issue with
`gh issue view`.

`claustrum backends list` names every backend Claustrum knows; `claustrum backends doctor` says
which ones are actually installed here.

## 3. Keys

Backends authenticate themselves — Claustrum never holds a credential, it only passes the
environment through. Set what the backends you installed need:

| Variable | Used by |
|---|---|
| `ANTHROPIC_API_KEY` | `claude`, `api` (a Claude Code login works instead for `claude`) |
| `OPENROUTER_API_KEY` | `opencode`, `api` |
| `DEEPSEEK_API_KEY` | `opencode`, when pointed straight at DeepSeek instead of through OpenRouter |
| `CURSOR_API_KEY` | `cursor` (a `cursor-agent` login works instead) |
| `GH_TOKEN` / `GITHUB_TOKEN` | `copilot`, and `gh` for `coordinate --issues` (`gh auth login` works instead) |

```bash
# Linux / macOS — in ~/.bashrc, ~/.zshrc, or a secrets manager that exports them
export ANTHROPIC_API_KEY=...
```

```powershell
# Windows — persists for new shells, not the current one
setx ANTHROPIC_API_KEY "..."
```

**Never put a key in the repository**, in `claustrum.json`, or in a cast file — all three are
committable. Claustrum forwards an allow-listed environment to each backend (every `*_API_KEY`
variable, the `ANTHROPIC_`/`OPENROUTER_`/`OPENCODE_`/`CURSOR_`/`COPILOT_` prefixes, and
`GH_TOKEN`/`GITHUB_TOKEN`) and never prints a value: `doctor` reports only whether a variable is
present.

## 4. Set up a repository

From the root of the repo you want to work in:

```bash
claustrum init            # or: claustrum init --all
claustrum backends doctor
```

`init` creates `.claustrum/` (`casts/` committable; `briefs/`, `worktrees/` and `locks/` added to
`.gitignore`), writes `claustrum.json` if there isn't one, syncs the harnesses the repo already
shows signs of using (`--all` syncs every supported one), and appends a short "Claustrum
delegation" pointer to an existing `AGENTS.md`. It never creates `AGENTS.md` or `CLAUDE.md`, and it
never overwrites a file it did not generate.

`backends doctor` is free: binary found, path, version, the merged configuration with the layer
each value came from, and a `gh:` block (found, path, version, and a problem line when `gh` is not
on `PATH`, since `coordinate --issues` needs it). `backends doctor --probe` adds the auth/MCP/OS
checks **and makes one real, paid request per installed backend**, capped at $0.50 each;
`CLAUSTRUM_SKIP_PROBE=1` turns every probe into a named skip (CI sets it, so the suite never spends
money).

## 5. Windows and WSL: one OS per run

**Run Claustrum and its backends on the same OS as your checkout.** A Windows Claustrum drives
Windows backends against a Windows path; a Linux (or WSL) Claustrum drives Linux backends against a
Linux path. Claustrum never translates paths, so a WSL binary pointed at `C:\…` — or a Windows
binary pointed at `/mnt/c/…` — mixes two path conventions in one run. `backends doctor --probe`
warns when the binary it found and the working directory straddle that boundary (docs/PLAN.md §A4).

Under WSL, install the backends inside WSL. A Windows `npm i -g` does not put them on the Linux
`PATH`, and the `.cmd` shim it creates is not runnable there.

## 6. Per-host hookup

`claustrum sync` renders the role library into each harness's own format and registers the MCP
server; `init` runs it for you. `--only <harness,…>` picks harnesses, `--roles <role,…>` picks
roles, `--dry-run` prints a diff instead of writing, `--check` exits 2 on a hand-edited, stale or
missing file (for CI), `--force` adopts a file Claustrum did not generate.

| Host | What sync writes | In chat |
|---|---|---|
| Claude Code, Claude desktop (Code tab) | `.claude/agents/<role>.md` + generated `-xhigh`/`-max` variants, `.claude/skills/claustrum/SKILL.md`, `.mcp.json` (`mcpServers.claustrum`) | `/claustrum`, or `@architect` and the other agents |
| VS Code (agent mode, Copilot) | `.vscode/mcp.json` (`servers.claustrum`, stdio) — written by the `claude` target | the `claustrum` MCP tools |
| opencode | `.opencode/agent/<role>.md`, `.opencode/command/claustrum.md`, `opencode.json` (`mcp.claustrum`) | `/claustrum` |
| Copilot CLI | `.github/agents/<role>.agent.md`, `.github/skills/claustrum/SKILL.md` | `copilot --agent architect`; the skill surfaces by relevance, as Copilot has no slash commands |
| Cursor | `.cursor/agents/<role>.md` + generated `-xhigh`/`-max` variants, `.cursor/skills/claustrum/SKILL.md`, `.cursor/mcp.json` (`mcpServers.claustrum`) | `/claustrum`, or the subagents by name |

```bash
claustrum sync --only claude,opencode,copilot,cursor   # this repo
claustrum sync --global --only claude                 # your user-wide agents, ~/.claude/agents
claustrum sync --check                                # CI: fail on drift
```

`--global` targets `~/.claude/`, `~/.config/opencode/`, `~/.copilot/` and `~/.cursor/` (Cursor's
user-level `mcp.json` included) and leaves repo-root files (`.mcp.json`, `.vscode/mcp.json`,
`opencode.json`) alone. On Windows and macOS, `sync --global --only claude` also registers the
server in the Claude desktop app's own `claude_desktop_config.json`
(`%APPDATA%\Claude\`, `~/Library/Application Support/Claude/`), with the absolute path of the
running binary as its command and every other server in that file left untouched; on Linux there is
no Claude desktop app, and `sync` prints one line saying so.

Then make a cast — who plays which role — once per repo: type `/claustrum` in a chat host and
answer the questions, or run `claustrum cast new` in a terminal. It lands in
`.claustrum/casts/default.json`, which is committable and shared with the team.

## Coordinate

With a cast in place and `gh` authenticated, Claustrum can run the whole pipeline itself:

```bash
claustrum coordinate --cast default --issues 12,13
```

It spawns the architect headlessly on the cast's model; the architect delegates the builders, the
blind review and the test run itself, and leaves its work on a `claustrum/<job-id>` branch.
`claustrum jobs budget <job-id>` shows what the whole job tree spent.

## Where to look next

- [README](../README.md) — what Claustrum is, the two doors, the receipt.
- [docs/PLAN.md](PLAN.md) — the design, milestone by milestone.
- [AGENTS.md](../AGENTS.md) — the rules for changing this repo.
- [NOTES.md](../NOTES.md) — the measured rationale behind the choices above.
