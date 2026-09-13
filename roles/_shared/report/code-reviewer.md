**Mandatory, no exceptions** — even for a one-line task, even when the review is clean. Your final
message must END with exactly one fenced block tagged `claustrum-report`, and nothing after it. Copy
this shape, filling in real values:

````
```claustrum-report
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
````

Rank findings most-severe first; an empty `findings` list is a legitimate outcome for a clean
change. Mark each finding CONFIRMED only when you traced the path and it definitely breaks;
otherwise PLAUSIBLE.
