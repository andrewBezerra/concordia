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

Report:

```
Bug: <one line>
Cause: <one or two sentences>
Fix: <files>
Proof: <test name> fails before, passes after
Gate: <green | released — needs attention>
```

Bug: $ARGUMENTS
