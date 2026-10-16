# Recorded `opencode acp` transcripts (opencode 2.0.30, 2026-10-16, issue #130)

Each `<name>.jsonl` is the **agent's half** of a real ACP run (what `stdout.log` holds): newline-delimited JSON-RPC from
`opencode acp`. Each `<name>.client.jsonl` is the **client's half** (`acp-client.jsonl`): what was written to its stdin. They are
test input, never executed. They hold no credentials, only scratch paths and session ids.

Two edits were made to the recordings, and only these: the model catalogue inside a `configOptions` answer (400 `{value,name}`
pairs, 34 KB a line) is cut to its first three entries, so every line keeps its number; and the probes' `cfg-N` request ids are
written `config-N`, the scheme the client uses. Nothing else is touched.

| File | What it is | Source |
|---|---|---|
| `opencode-success` | A batch run that ended with a `claustrum-report` fence: 3 tool calls, one prompt, `usage_update` $0 (free model) | Claustrum job `20261016-164432-4bf5f173` |
| `opencode-report` | A builder under the deny config: 6 tool calls (`git push` denied, `failed`), a fence, one `usage_update` ($0) at **line 88**, the answer at 89. Ids are numeric (an older probe): the parser must read them by the shape of the result | probe `ctl-acp-deny` |
| `opencode-cancelled` | A `session/request_permission` the client rejected: `stopReason: cancelled`, no report, no text | probe `ctl-acp` |
| `opencode-three-turns` | Three prompts on a paid model, `usage_update` running totals 0.000788515, 0.0010218936, 0.0018094076, then a model-not-found error (`config-99`) after the prompts | probe `measure_cost.py` |
| `opencode-provider-error` | Three prompts each answered with `-32603` and `data.errorName: provider.invalid-request` (no turn, no `usage_update`) | the same probe |
| `opencode-load-replay` | A `session/load`: three history turns replayed as `user_message_chunk` / `agent_thought_chunk` / `agent_message_chunk` before the `load-2` answer, then one turn with running total 0.0019851832 | the same probe, process 2 |
| `opencode-effort-refused` | A handshake refusal: `config-4` (`effort` = `bogus`) answered `-32602 "effort not found"`, no `data.modelId`; nothing was prompted | Claustrum job `20261016-164027-483cab2b` |
| `opencode-cut-off` | A live run killed by its timeout mid-turn: thoughts and a tool call in progress, no answer | Claustrum job `20261016-164447-5129e026` |

The synthetic cases in the tests (truncated copies of these, handshake errors, child sessions, stop reasons) are built from these
frames, not from a different idea of the protocol.
