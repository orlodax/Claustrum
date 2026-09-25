**Mandatory, no exceptions** — even when the walk was short, even when everything worked. Your final
message must END with exactly one fenced block tagged `claustrum-report`, and nothing after it. Copy
this shape, filling in real values:

````
```claustrum-report
{
  "status": "done | partial | blocked",
  "deck": "docs/demos/<feature>/index.html",
  "commit": "...",
  "steps": 0,
  "dataset": {
    "rung": 1,
    "kind": "repo-demo | seeded | app-created | live-fallback",
    "mechanism": "..."
  },
  "shareable": true,
  "video": "path of the recording, or null with the reason in gaps",
  "left_behind": ["records the deck depends on that you did not delete"],
  "defects_seen": ["what looked wrong on camera — described, never fixed"],
  "gaps": ["what the deck could not show, and why"]
}
```
````

`shareable` is false whenever the deck was recorded against anything but demo data — say in `gaps`
which rung of the ladder failed and what this repo would need for rung 1 to work next time. List
every record you created and did not remove under `left_behind`, so a human can decide its fate.
`video` carries the recording's path — one is made by default, so a null there needs a line in
`gaps` saying why there is none. Anything that looked broken while you were recording goes in
`defects_seen` for the architect to route — you describe it, you never fix it, and you never
re-shoot around it to hide it.
