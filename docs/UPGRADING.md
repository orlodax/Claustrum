# Upgrading Claustrum

## 0.4.x → 0.5.0

A script that checks `status == "success"`, or exit code 0, now also sees `success_with_issues` and exit 8 for a job whose
environment got in its way. Read the first item below. The role library's text changed: synced repos show drift under
`claustrum sync --check` until you run `claustrum sync` again.

### What changes

- **A new status and exit code: `success_with_issues`, exit 8 (#145).** A job that finished while its environment refused it
  something used to say `success`. Examples: a directory it was not allowed to read, a missing tool, a failed `gh` login,
  plan mode, a red test gate in its own report. It now says `success_with_issues` and `run` exits 8. Every other status
  and exit code is unchanged.
  - Treat such a result as not clean: tell the user what the job could not reach before you use it. The `/claustrum`
    skill tells the main session so.
  - A cast's fallback never retries it, because it did work.
- **A new receipt field: `environment_issues` (#145).** `[{kind, what, where, count, first}]`, or `null` when nothing got in
  the way. It appears on any status, a `failed` one included.
  - `run --json`, `jobs show`, MCP `delegate`, `job_status` and `job_result` carry it, and so does the document
    `jobs wait --json` prints.
  - `run`, `jobs show`, `jobs wait`, `jobs follow` and `coordinate` print one `warning: environment issues (N): …` line on
    stderr.
  - The MANUAL's §6 *Environment issues* lists every kind and what triggers it.
  - Only `claude`, `opencode` and `acp-opencode` runs are read for them; `cursor`, `copilot` and `api` receipts list only
    what was found at dispatch and what the report says.
- **The report shapes gain an optional `blocked_by` (#145).** It holds what in the environment stopped the role: strings,
  or `{what, where}`. Each entry becomes an issue.
- `jobs list` gives the status column three more characters, so `success_with_issues` fits.
- **A claude `shell` job can now write and delete through Bash (#140).** The `shell` level (the ui-reviewer's) no longer runs
  claude in plan mode. Plan mode told the model to change nothing, so a walk that writes app data was refused, and it also
  stopped a command that writes. It runs in `dontAsk` mode now: `Read`, `Glob`, `Grep` and every `Bash` command run, and the
  edit tools stay off. A command can write and delete files, as on opencode's `shell`. `shell` is not a sandbox: a deny
  entry guards how a command is written, it is not a security boundary. The ui-reviewer's deny list now also has
  `git checkout`, `switch`, `reset`, `restore`, `stash`, `commit`, `clean`, `rebase` and `merge`.
- **A ui-reviewer on claude can use its browser, if you name it (#140).** An MCP server's tools run only when its server is
  named: in the role library's `tools`, or in your user config, e.g.
  `"roles": { "ui-reviewer": { "mcp_servers": ["chrome"] } }`. Without it the role's browser calls are denied. A repo's
  `.mcp.json` servers stay attached and are denied too, a name the repo's `.mcp.json` defines is never approved even when
  you name it (the receipt says so), and a repo's `claustrum.json` cannot set `mcp_servers`.
- **opencode's `readonly` gets a narrow read-only git shell (#143).** A code-reviewer on `opencode` or `acp-opencode` can run
  `git diff`, `git log`, `git show`, `git status`, `git rev-parse`, `gh pr view`/`diff`/`list`/`checks` and
  `gh issue view`/`list`, as words. Everything else is denied. Those commands are refused too when they hold a quote, `$`,
  a backtick, a `\` or a `/`, `{`, `<`, `>`, `--output`, `--no-index` or `--ext-diff`. So pipes into other commands,
  `2>&1`, `--format='…'` and any path (`git diff -- src/x.cs`, `origin/main`) do not work there.
  - claude's `readonly` uses the same narrowed list: it adds `git rev-parse`, and it drops `git difftool` and gh's write
    verbs, which `git diff*` and `gh pr *` used to allow. A command claude's own matcher still lets through is #148.
- **Jobs can read directories outside their tree (#144).** There are three new user-config keys: `read_dirs`,
  `projects.<glob>.read_dirs` and `projects.<glob>.worktree_hook` (#146). See the MANUAL §7 "Directories a job may read".
  - A job in a linked worktree, which is every isolated job, can also read the main checkout's `.git`, except on copilot
    and cursor at `edit` and `edit+shell`, which would make it writable.
  - A repo's `claustrum.json` cannot set these keys: they are ignored, with a warning.
  - A relative path in `read_dirs`, a relative `worktree_hook` program or a `null` project stops the dispatch with exit 2.
  - On `copilot` and `cursor` at `edit` and `edit+shell` the dirs are read-write, and the dispatch says so.
  - A dir whose path claude (`(`, `)`, `,`, at the edit levels) or opencode (`*`, `?`, `\`) cannot fence is not given,
    and the dispatch says so.
  - A read dir's own agent config is loaded (claude: `.claude/skills`, `commands`, `agents`; copilot: `.github/skills`,
    `agents`). List directories you trust.

## 0.3.x → 0.4.0

Nothing to do, unless a config file of yours says `"budget_usd": null` or you set `CLAUSTRUM_BUDGET_USD=none`: read the
first item below. The role library stays `1.1.0`, so synced repos show no drift.

### What changes

- **A `null` budget in a config file now turns the default cap off (#137).** `"defaults": {"budget_usd": null}` in
  your user config or a repo's `claustrum.json` used to read as an absent key, so the earlier layer's cap still
  applied: the built-in $5, or the number your user config set. It now means no default cap, so a hand-written `null`
  goes from that cap to none. `claustrum init` only ever writes numbers.
  - `CLAUSTRUM_BUDGET_USD=none` (or `=null`) does the same from the environment. Before, a non-number there was
    ignored, so a variable already set to `none` goes from a cap to none too.
  - `--budget` and a cast's `budget_usd` still win over it.
  - `backends doctor` shows it as `defaults.budget_usd = none  [User]`.

## 0.2.x → 0.3.0

Nothing to do. 0.3.0 adds one backend and fixes one race. Every existing backend, cast and receipt
behaves as it did, and the role library stays `1.1.0`, so synced repos show no drift.

### What's new

- **`acp-opencode`, an opt-in backend.** It drives `opencode acp` over the Agent Client Protocol
  instead of `opencode run`. The role prompt and the deny list are the same as on the native
  `opencode` backend. Nothing resolves to it by default. Pick it with
  `--backend acp-opencode --model opencode/<id>`, or a model alias `acp-opencode:<model>`.
  - Batch runs and live sessions (`jobs say`, `answer`, `follow`) work through it.
  - It reports token usage, and its cost comes from `opencode session export` like the native
    backend's.
  - Each job also keeps `acp-client.jsonl`: what Claustrum sent, next to `stdout.log`.
  - See the MANUAL's "ACP backends" paragraph.
- **A detached fallback retry is no longer lost.** Before, a waiter could print the first job's
  receipt and exit 1 while the retry ran unwatched (#132).

## 0.1.x → 0.2.0

0.2.0 changes how a repo is set up, and some runs that used to work silently now stop and ask. Read
this once before upgrading. The role library goes from `1.0.0` to `1.1.0` at the same time.

### What you have to do

1. **Run `claustrum init` once on every machine.** Claustrum now refuses to run on its built-in
   model defaults without saying so. With neither a user config nor a repo `claustrum.json`,
   `run`, `delegate`, `cast questions/new/create` and `coordinate` exit **2** and tell you to run
   `claustrum init`.
   - `init` asks what each model alias (`frontier-coding`, `cheap-coding`, …) should map to, and
     writes the answers to **your user config**: `~/.config/claustrum/config.json`, or
     `%APPDATA%\claustrum\config.json` on Windows.
   - It never overwrites an alias you already have, and it no longer writes a repo `claustrum.json`.
   - From an agent or a script: `claustrum init questions --json`, answer, then
     `claustrum init --answers <file>`.
   - An existing repo `claustrum.json` still loads and still counts as "initialized".
2. **Make your cast your own.** A cast (who plays which role) is per-developer now and lives in a
   git-ignored `.claustrum/casts/`. `init` adds the ignore rule. If your repo committed a cast before,
   `init` says so, and you untrack it with:

   ```bash
   git rm --cached -r .claustrum/casts
   ```

   Commit the `.gitignore` change. `coordinate` needs that rule committed to accept an untracked
   cast, which it copies into the architect's worktree.
3. **Opt back in to your personal MCP servers only where a role needs them.** claude roles now run
   with `--strict-mcp-config` and get only the repo's own `.mcp.json`, not the servers in your user
   configuration (Gmail, Microsoft 365, …). A role that needs one gets them back with
   `"roles": { "<role>": { "user_mcp": true } }` in **your user config**. A repo's `claustrum.json`
   can't turn this on for you. ui-reviewer and demo-author have it on by default so they keep their
   browser (narrowing that to the browser alone is #129).

### What changed under you

- **Exit codes.** `6` = abandoned: the claustrum process running a live job is gone (`jobs wait`,
  and a tree child's `run`). `7` = needs input: the role stopped with a question (or a tree child's
  `run` returned early with one). `0`–`5` and `130` mean what they did.
- **A run can stop with `needs_input`.** A role that can't reach anyone to ask stops with structured
  questions instead of guessing at a decision that's yours. Continue it with
  `claustrum jobs answer <id> "<answer>"` (or `--answers <file>`). It's answered once.
- **Live sessions.** On claude and opencode, a running role can ask you, and you answer with
  `claustrum jobs say <id> "…"`, inline in a terminal `run`, or from `claustrum jobs follow <id>`.
  `--no-live` restores the old one-shot behaviour, and `--reply-timeout <s>` sets how long a question
  waits. See the MANUAL's "Talking to a running job".
- **claude sessions are kept** (no more `--no-session-persistence`, except for blind reviewers), so
  `jobs answer` can resume them. They appear among your claude sessions.
- **Receipts carry more.** `result.json` gains `conversation`, `questions`, `error_kind`,
  `error_detail`, `before_any_work`, `fallback_of` and `retried_as`. Casts gain `resolved`,
  `fallback` and `resolved_fallback`. Old casts and old receipts still load. A strict parser of these
  documents will see the new keys.
- **Fallback models.** A cast role may name a `fallback`. A provider refusal before any work (quota,
  auth, unknown model, transport) retries once on it, and the receipt says why.
- **Cost.** opencode jobs record the session's real cost (no longer `null`, which charged the cap).
  A resumed claude or opencode session is charged only its own spend.
- **Readable progress.** `--stream` prints one sanitized line per event, and `--stream-raw` is the old
  raw echo. `claustrum jobs follow [<id>]` tails a job from another terminal.
- **The role library is 1.1.0.** Repos synced from 1.0.0 show drift under `claustrum sync --check`
  until you run `claustrum sync` again.
