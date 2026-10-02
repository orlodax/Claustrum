**Mandatory, no exceptions** — even when the walk was short, even when everything worked. Your final
message must END with exactly one fenced block tagged `claustrum-report`, and nothing after it. Copy
this shape, filling in real values:

````
```claustrum-report
{
  "status": "done | partial | blocked",
  "deck": "the index.html you wrote — <main checkout>/docs/demos/<feature>/index.html by default",
  "commit": "...",
  "steps": 0,
  "dataset": {
    "rung": 1,
    "kind": "repo-demo | seeded | app-created | live-fallback",
    "mechanism": "..."
  },
  "shareable": true,
  "video": "path of the recording, or null with the reason in gaps",
  "gitignore": "each file you actually changed, with the line: <checkout>/.gitignore (docs/demos/ only, uncommitted) and/or info/exclude | nothing written: already ignored | outside every checkout",
  "left_behind": ["records the deck depends on that you did not delete"],
  "defects_seen": ["what looked wrong on camera — described, never fixed"],
  "gaps": ["what the deck could not show, and why"]
}
```
````

`shareable` is false whenever the deck was recorded against anything but demo data — the deck and
the video are then both internal. Say in `gaps` which rung of the ladder failed and what this repo
would need for rung 1 to work next time. `gitignore` names only what you actually wrote, and
where — never a line you meant to write. Only a `docs/demos/` line you added to a `.gitignore` is
for the orchestrator to commit, exactly that line; an `info/exclude` line is local, and every other
deck directory is ignored through it alone. If the line is not in `info/exclude` afterwards, or the
main checkout still does not ignore the deck, `status` is `blocked`, no deck file was written —
`gitignore` lists any ignore line that was — and `gaps` names the line and the file it must go
into.
`deck` is the path you actually used: when it is a `-<short commit>` sibling because the planned
directory is or was tracked, `gaps` says so (see "Where it all goes"). List every record you created
and did not remove under `left_behind`, so a human can decide its fate.
`video` carries the recording's path — one is made by default, so a null there needs a line in
`gaps` saying why there is none. Anything that looked broken while you were recording goes in
`defects_seen` for the architect to route — you describe it, you never fix it, and you never
re-shoot around it to hide it.
