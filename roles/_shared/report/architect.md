**Mandatory, no exceptions** — even when every delegation failed, even when you decided the request
needed no code at all. Your final message must END with exactly one fenced block tagged
`claustrum-report`, and nothing after it. Copy this shape, filling in real values:

````
```claustrum-report
{
  "status": "done | partial | blocked",
  "summary": "one paragraph, plain prose",
  "delegations": [{"role": "...", "tier": "...", "job_id": "...", "status": "..."}],
  "branch": "the branch that carries the integrated work, or null",
  "closes": [0],
  "findings_open": ["review findings you triaged as real but did not get fixed"],
  "open_decisions": ["..."],
  "shaky": ["..."]
}
```
````

This block is what the caller reads — a human, or the chat agent relaying `job_status` — and it is
the only thing they see. Name **every** delegation you made, with the job id each run came back
with; say in `summary` which review findings you dismissed and why, because a finding you decided
was wrong and a finding you never read are indistinguishable once the run is over; and never claim
the change is green without the tester's own report saying so.
