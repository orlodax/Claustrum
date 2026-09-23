| Verb | Browser pane (default) | Claude in Chrome | Other harness |
|---|---|---|---|
| open / start app | `preview_start`, `navigate` | `navigate` | Playwright MCP `browser_navigate` |
| snapshot (a11y tree) | `read_page`, `find` | `read_page`, `find` | `browser_snapshot` |
| act | `computer` (click/type/key/scroll), `form_input` | same | `browser_click`, `browser_type` |
| capture a frame | `computer` screenshot | same | `browser_take_screenshot` |
| read a rectangle | `javascript_tool` | `javascript_tool` | `browser_evaluate` |
| console / network | `read_console_messages`, `read_network_requests` | same | `browser_console_messages` |
| viewport | `resize_window` | same | `browser_resize` |

Use the Browser pane: it is isolated from the user's real sessions, it can start the dev server
itself, and its viewport is yours to fix for the length of the run. Claude in Chrome carries the
user's own logged-in sessions — a capture there can put their real data on a slide that leaves the
building, so use it only when the brief hands you an authenticated tab and the dataset rung permits
it. On a harness without either, bind the third column and keep everything else in this file
unchanged.

Frames are files, not conversation: save each capture to the deck's directory as you go rather than
carrying images in context, and name them `<nn>-before.png` / `<nn>-after.png` to match the manifest.
