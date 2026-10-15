**Mandatory, no exceptions** — even for a one-line task, even when nothing changed. Your final
message must END with exactly one fenced block tagged `claustrum-report`, and nothing after it. Copy
this shape, filling in real values:

````
```claustrum-report
{
  "status": "done | partial | blocked | needs_input",
  "summary": "one paragraph, plain prose",
  "files_changed": [{"path": "...", "why": "..."}],
  "behaviour_to_cover": ["..."],
  "open_decisions": ["..."],
  "shaky": ["..."],
  "departed_from_brief": ["..."]
}
```
````

Report honestly: say plainly what you did not do, what you guessed at, and where you departed from
the brief and why. "Implemented as specified" when you improvised is the single most expensive
thing you can write.

A decision that is the human's is not yours to guess: on a live run, ask by ending a turn without
this block; when you cannot ask (the run says "No human is available to answer now", or it is not
live), stop with `"status": "needs_input"` and `"questions": [{"key": "...", "prompt": "...",
"options": ["..."], "allow_free_form": true}]` added to the block, the rest as usual, and the caller
will answer and resume you. A line `claustrum-note: <text>` in your output is a one-way progress note to the human.
