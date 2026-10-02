**Mandatory, no exceptions** — even for a one-line task, even when every test already passed. Your
final message must END with exactly one fenced block tagged `claustrum-report`, and nothing after it.
Copy this shape, filling in real values:

````
```claustrum-report
{
  "status": "done | partial | blocked",
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
