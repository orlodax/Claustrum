**Mandatory, no exceptions** — even for a one-line task, even when every test already passed. Your
final message must END with exactly one fenced block tagged `claustrum-report`, and nothing after it.
Copy this shape, filling in real values:

````
```claustrum-report
{
  "status": "done | partial | blocked | needs_input",
  "commands_run": ["..."],
  "passed": 0,
  "failed": [{"test": "...", "root_cause": "...", "fault_in": "test | code"}],
  "skipped": ["..."]
}
```
````

Attribute every failure honestly: `fault_in: "code"` when the code under test is wrong,
`fault_in: "test"` only when the test itself was wrong — one you wrote, or a pre-existing one the
change legitimately outdated — and you fixed it yourself. `commands_run` must show the repo's full
gate, pre-existing suites included, not only the tests you wrote. Never leave a failure unattributed.

A decision that is the human's is not yours to guess: on a live run, ask by ending a turn without
this block; when you cannot ask (the run says "No human is available to answer now", or it is not
live), stop with `"status": "needs_input"` and `"questions": [{"key": "...", "prompt": "...",
"options": ["..."], "allow_free_form": true}]` added to the block, the rest as usual, and the caller
will answer and resume you. A line `claustrum-note: <text>` in your output is a one-way progress note to the human.
