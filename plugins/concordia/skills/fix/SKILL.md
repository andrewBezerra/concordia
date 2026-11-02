---
name: fix
description: Fix a bug the Concordia way — reproduce it with a failing test first, find the root cause, apply the smallest fix, and prove it. Use when the user reports a bug, a wrong result, an exception or a failing test.
argument-hint: <bug report, error, or failing behavior>
---

# Concordia: fix

A fix is only done when a test that reproduced the bug now passes and nothing else broke.
The verification gate (a Stop hook) enforces the second part. You make sure of the first.

The helper CLI path is in the session context as "Concordia helper".

1. **Reproduce.** Write the smallest test that fails because of the bug. Run it and confirm it
   fails for the reported reason, not for an unrelated one. If you cannot reproduce it, stop and
   tell the user what you tried.
2. **Root cause.** Explain in one or two sentences why the bug happens. Fix the cause, not the
   symptom. Search for the same pattern elsewhere; mention other occurrences instead of silently
   expanding the scope.
3. **Fix.** Apply the smallest change that makes the test pass. Do not change the new test to
   make it pass.
4. **Risk check.** Run `"<Concordia helper>" review-pack`. If it selects any lens, delegate to one
   **reviewer** subagent with the pack path and those lenses. Address findings with
   confidence ≥ 0.7.
5. **Finish.** The gate builds and runs every test.

Report. The gate runs after it, so you have not seen its result for it. State only what you ran
yourself, and never write that the gate passed. If the gate is red it blocks the report and sends
you back with the errors, up to its attempt limit. After `attempt N of N`, your next stop is
released even if the tests are still red, and a command that times out skips the gate, so no block
is not a pass. If you received `attempt N of N` and the failure remains, use the second `Gate:` form.

```
Bug: <one line>
Cause: <one or two sentences>
Fix: <files>
Proof: <test name> fails before, passes after, as you ran it
Gate: <runs after this report and blocks it if red, up to its attempt limit; no block is not a pass | still red after N attempts, will be released — needs attention>
```

Bug: $ARGUMENTS
