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
`fault_in: "test"` only when you wrote the test incorrectly and then fixed it yourself. Never leave a
failure unattributed.
