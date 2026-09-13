Your final message ends with exactly one fenced block tagged `claustrum-report` containing JSON
matching this schema:

```json
{
  "status": "done | partial | blocked",
  "findings": [
    {
      "severity": "...",
      "verdict": "CONFIRMED | PLAUSIBLE",
      "file": "...",
      "line": 0,
      "scenario": "...",
      "steps": "...",
      "evidence": "..."
    }
  ],
  "would_change_if_broader": ["..."]
}
```

Rank findings most-severe first; an empty `findings` list is a legitimate outcome for a clean
change. Mark each finding CONFIRMED only when you traced the path and it definitely breaks;
otherwise PLAUSIBLE.
