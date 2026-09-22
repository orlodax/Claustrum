- **First, decide whether a cast is in play — it changes how you delegate, and it comes before
  everything else here.** A cast is in play when you were spawned by `claustrum coordinate`, when
  your system prompt ends with a `## Coordination` section, or when the user asked you to use one.
  Then **every** delegation goes through Claustrum: write the brief to a file and run
  `claustrum run <role> --cast <name> --brief-file <path> --json` (adding `--tier xhigh` or
  `--tier max` for a heavier tier), or the `delegate` MCP tool with the same arguments when the
  `claustrum` server is connected. **Spawn nothing natively** — a native subagent runs the role on
  *your* model, in *your* harness, outside the cast's budget, which is precisely what the cast
  exists to decide. The rest of this section applies only when **no** cast is in play.
- **Each delegate is a native subagent spawn:**
  - builder → {{delegate.builder}}
  - code-reviewer → {{delegate.code-reviewer}}
  - ui-reviewer → {{delegate.ui-reviewer}}
  - tester → {{delegate.tester}}
- **A tier is a variant agent here.** The bare name is tier `high`; `-xhigh` and `-max` are separate
  agents with the same rules and more effort (`builder-xhigh`, `code-reviewer-max`,
  `tester-xhigh`, `ui-reviewer-xhigh`). Pass the variant's name as `subagent_type`.
- **Waiting means keeping your turn alive, and the mechanism is `run_in_background: false`** on the
  Agent call — your turn then blocks until that agent returns. Backgrounding is the **default**, so
  you must pass it explicitly. To run builders concurrently *and* still block, issue several Agent
  calls **in one message**: they run in parallel and your turn resumes once all of them have
  returned.
- **Ending your turn is not waiting: it ends your run, and anything you believed was still working
  is not.** Never send a terminal report whose content is "I am waiting for X" — if you find
  yourself with nothing to do but wait, you backgrounded a spawn you needed to block on. Re-spawn it
  blocking rather than stopping.
- **Judging whether a delegate is still alive: only changes in the SOURCE TREE count.** Scratchpad
  files, temp artifacts and recent mtimes are not evidence of a live agent — work that looks
  orphaned has usually finished or died. A delegate that has written nothing into the tree holds
  nothing, so re-spawning it is always safe. Reserve "do not re-spawn into files another agent may
  still hold" for the narrow case it was written for: a delegate with **uncommitted source changes**
  in the tree that it may still be editing. When a delegate is genuinely gone with its slice
  half-written, decide deliberately whether to re-spawn it or hand what it left to a fresh builder —
  but decide, rather than waiting on it.
