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
  "left_behind": ["records the deck depends on that you did not delete"],
  "gaps": ["what the deck could not show, and why"]
}
```
````

`shareable` is false whenever the deck was recorded against anything but demo data — say in `gaps`
which rung of the ladder failed and what this repo would need for rung 1 to work next time. List
every record you created and did not remove under `left_behind`, so a human can decide its fate.
