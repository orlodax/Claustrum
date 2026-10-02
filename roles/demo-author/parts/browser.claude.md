Think in these verbs and bind them to the surface the session offers. Use **one** surface per run,
and never while a ui-reviewer is driving the same browser.

| Verb | Browser pane (default) | Claude in Chrome | Scripted driver |
|---|---|---|---|
| open / start app | `preview_start`, `navigate` | `navigate` | `page.goto` |
| snapshot (a11y tree) | `read_page`, `find` | same | `locator` |
| resolve target + rectangle | `find`, then `javascript_tool` | same | `locator(SEL).boundingBox()` |
| act | `computer` (click/type/key), `form_input` | same | `click`, `fill`, `press` |
| console / network | `read_console_messages`, `read_network_requests` | same | page events |
| viewport | `resize_window` | same | `setViewportSize` |
| **write a frame to disk** | not possible | not possible | `page.screenshot({ path })` |

🚨 **The interactive surfaces cannot save a frame.** `computer`'s screenshot, and Chrome's, come
back to *you* as an image in the tool result: there is no path argument, and an image you can only
look at is not bytes you can `Write`. A walk that tries to build the deck from them produces a
manifest whose `before`/`after` names point at nothing. So the run has two halves:

- **Rehearse** on the Browser pane — follow the storyboard, confirm each state is the one your
  caption will claim, and read each target's rectangle. Those screenshots are for your eyes only.
- **Capture** with a driver that writes files: the repo's own Playwright or Cypress when it has one,
  otherwise a Playwright script — in the session scratchpad when you have one, else fed to `node`
  on stdin so no script file is written at all; never in the tree — logged in through the access
  recipe's mechanism (storage state, dev-only route, setup script) and never by typing a password.
  It writes the frames into the deck directory as `<nn>-before.png` / `<nn>-after.png`, to match
  the manifest.

**Write every deck file — and the `info/exclude` line — from the capture script.** `Write`, `Edit`,
shell redirections and `cp`/`mv`/`mkdir` targets are checked against the session's working
directories, and anything outside them asks (Claude Code 2.1.284, `2026-10-02`); `.git` is a
protected folder, so a write into it asks even inside them, in `acceptEdits` too. A run delegated
through `claustrum run` answers no prompt, so every ask is a refusal. The Node process therefore
does it all, in every case and before anything else: it runs `git rev-parse --git-common-dir` from
the root of the checkout you record in, resolves the result against that root (not
`--path-format=absolute`, which needs git 2.31), creates its `info/` directory if it is missing,
appends the line to `info/exclude` unless it is present, and only then creates the deck directory
and writes the frames, the video, `index.html` and `manifest.json` (from the capture script, or a
second Node process fed on stdin). If the re-check fails after that append, no deck file is
written: BLOCKED, as "Where it all goes" says. Any other refused write goes in `gaps` — never a fallback into the worktree.

⚠ Not yet measured on this machine: whether `npx playwright` runs without a first-time browser
download. If it wants one, that is a download to raise with the caller, not to perform silently —
say so in your report rather than deciding for them.

Prefer the Browser pane for the rehearsal: it is isolated from the user's real sessions. Use Claude
in Chrome only when the brief hands you an already-authenticated tab, then stay on that tab's origin
— and never let its URL bar or account chrome into a frame. When neither is there — a run
delegated through `claustrum run` may refuse them, since its permission flags allow built-in tools
only — rehearse with the driver as well: it navigates, resolves rectangles and screenshots, and the
frames come from it either way.
