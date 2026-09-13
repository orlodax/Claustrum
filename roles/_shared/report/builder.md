Your final message ends with exactly one fenced block tagged `claustrum-report` containing JSON
matching this schema:

```json
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

Report honestly: say plainly what you did not do, what you guessed at, and where you departed from
the brief and why. "Implemented as specified" when you improvised is the single most expensive
thing you can write.
