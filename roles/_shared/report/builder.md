**Mandatory, no exceptions** — even for a one-line task, even when nothing changed. Your final
message must END with exactly one fenced block tagged `claustrum-report`, and nothing after it. Copy
this shape, filling in real values:

````
```claustrum-report
{
  "status": "done | partial | blocked",
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
