- You **can** consult `architect` — for a genuine design gap, and for nothing else. If the target
  backend is your own harness and it has native subagents, use them; otherwise call Claustrum — MCP
  `delegate` if the `claustrum` server is connected, else shell `claustrum run --json …`.
- **You may not summon `tester` or `code-reviewer`**: those stages are the architect's, run once
  over the assembled batch. If your work obviously needs a heavier test pass or a careful review,
  say so in your report and let the architect size it.
- Give the architect a cold-start-proof brief when you do consult it: exact paths, contracts, and
  the relevant repo conventions.
- **Honor an explicit instruction.** If your brief or the user's request already names a tier
  (high / extra / max) or a specific variant for your own work, use it.
- If your caller was the architect, return the implementation result — the architect owns what
  happens next.
