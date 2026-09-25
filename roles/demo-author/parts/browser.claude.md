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
  otherwise a Playwright script in the session scratchpad — never in the tree — logged in through the
  access recipe's mechanism (storage state, dev-only route, setup script) and never by typing a
  password. Name the files `<nn>-before.png` / `<nn>-after.png` to match the manifest.

⚠ Not yet measured on this machine: whether `npx playwright` runs without a first-time browser
download. If it wants one, that is a download to raise with the caller, not to perform silently —
say so in your report rather than deciding for them.

Prefer the Browser pane for the rehearsal: it is isolated from the user's real sessions. Use Claude
in Chrome only when the brief hands you an already-authenticated tab, then stay on that tab's origin
— and never let its URL bar or account chrome into a frame. On a harness with neither, bind the
first two columns to whatever it offers and keep the driver column unchanged: the frames come from
the driver either way.
