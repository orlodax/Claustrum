| Verb | Browser pane (default) | Claude in Chrome | Other harness |
|---|---|---|---|
| open / start app | `preview_start`, `navigate` | `navigate` | Playwright MCP `browser_navigate` |
| snapshot (a11y tree) | `read_page`, `find` | `read_page`, `find` | `browser_snapshot` |
| act | `computer` (click/type/key/scroll), `form_input` | same | `browser_click`, `browser_type` |
| look | `computer` screenshot / zoom | same | `browser_take_screenshot` |
| console / network | `read_console_messages`, `read_network_requests` | same | `browser_console_messages`, `browser_network_requests` |
| viewport | `resize_window` | same | `browser_resize` |

Prefer the Browser pane: it is isolated from the user's real sessions and can start the dev server
itself. Use Claude in Chrome only when the brief hands you an already-authenticated tab there, and
then stay on that tab's origin. On a harness without either, bind the third column and keep
everything else in this file unchanged.
