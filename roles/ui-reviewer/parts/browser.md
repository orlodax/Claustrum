| Verb | Browser pane (Claude) | Claude in Chrome | Other harness |
|---|---|---|---|
| open / start app | `preview_start`, `navigate` | `navigate` | Playwright MCP `browser_navigate` |
| snapshot (a11y tree) | `read_page`, `find` | `read_page`, `find` | `browser_snapshot` |
| act | `computer` (click/type/key/scroll), `form_input` | same | `browser_click`, `browser_type` |
| look | `computer` screenshot / zoom | same | `browser_take_screenshot` |
| console / network | `read_console_messages`, `read_network_requests` | same | `browser_console_messages`, `browser_network_requests` |
| viewport | `resize_window` | same | `browser_resize` |

On Claude, prefer the Browser pane — it is isolated from the user's real sessions and can start the
dev server itself; use Claude in Chrome only when the brief hands you an already-authenticated tab
there, then stay on that tab's origin. On every other harness, bind the third column (prefix each
tool with the MCP server name, e.g. `playwright_browser_navigate`).

The accessibility snapshot (`browser_snapshot`) is your primary evidence — it is exact and
text-only, so it works when your model cannot see images. Take screenshots for the human reviewer
with `browser_take_screenshot` and **no `filename`**: the server saves them outside the tree, in the
run's own output directory, while a relative `filename` would land in the checkout you must not
modify. Cite the path each result reports; describe what it shows when your model cannot see it. A
purely visual claim — truncation, overlap, a layout break the snapshot cannot express — is PLAUSIBLE
unless your model can see the screenshot. If the server answers that its browser is not installed,
that is a download for the caller: report BLOCKED with the install command it names, never run it.
