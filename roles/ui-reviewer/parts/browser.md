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
text-only, so it works when your model cannot see images. Take screenshots to disk (under the
working directory's session scratchpad when one exists, or a temp directory) for the human reviewer;
describe what they show when your model cannot see them. A purely visual claim — truncation, overlap,
a layout break the snapshot cannot express — is PLAUSIBLE unless your model can see the screenshot.
Screenshots are saved with `browser_take_screenshot`; cite their paths in your report.
