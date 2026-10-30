---
name: feature
description: Build or change behavior in the codebase with a process sized to the task — triage (small/medium/large), tests first when it pays off, implementation, and risk-based review. Use for any feature, endpoint, rule or refactor request.
argument-hint: <what to build, acceptance criteria, or an issue>
---

# Concordia: feature

You orchestrate one change from request to verified result. Concordia's promise is that what you
deliver is **proven**, not claimed: tests show it works and the verification gate (a Stop hook)
blocks you from finishing while build or tests are red. Do not try to work around the gate.

The helper CLI path is in the session context as "Concordia helper". Call it with Bash.

## 1. Triage (one line, then move on)

Classify the request and state it in one sentence, for example
`Triage: medium — new endpoint + domain rule, ~5 files.` The user may override it.

| Size | Typical shape |
| --- | --- |
| **Small** | ≤ 3 files, no new public contract (route, DTO, schema), clear requirement |
| **Medium** | One feature slice: new endpoint or domain rule, up to ~10 files, one project or two |
| **Large** | Crosses layers or projects, schema or migration, new dependency, security-sensitive, or ambiguous requirements |

When unsure between two sizes, pick the larger one only if the requirement is ambiguous;
otherwise pick the smaller one. Process that is heavier than the task wastes the user's money.

## 2. Pipeline by size

### Small
1. Read only the files you need.
2. Implement the change.
3. Add or update a test that fails without the change and passes with it.
4. Finish. The gate formats, builds and runs the tests.

### Medium
1. Write down the acceptance criteria as a short list (from the request; ask only if one is genuinely missing).
2. Delegate to the **test-author** subagent: give it the criteria and the relevant paths. It writes failing tests.
3. Run those tests yourself and confirm they fail for the expected reason.
4. Implement until they pass. Never edit the new tests to make them pass; if a test is wrong, say so and why, then fix it.
5. Review — see section 3.
6. Fix findings with confidence ≥ 0.7 and severity medium or high. Mention the ones you dismiss and why.

### Large
1. Delegate to the **planner** subagent with the request and any issue text.
2. Show the plan to the user and **wait for approval**. Do not write code before approval.
3. Continue as in Medium, from step 2, implementing the plan in small steps and running the
   affected tests after each step.
4. Review with every lens the triage selects; run independent lenses **in parallel**.

## 3. Review (medium and large)

1. Run `"<Concordia helper>" review-pack` (add `--base <ref>` if the work spans commits).
   It writes the diff and stats to a pack file and prints JSON with the selected lenses.
2. Medium: start **one** reviewer subagent with the general lens plus the lenses listed in the JSON.
   Large: one reviewer per listed lens, in parallel (all lenses when `escalateAll` is true).
3. Pass each reviewer the pack path and its lens name. Reviewers do not edit code.
4. If a reviewer returns `escalate` entries, run **only** those extra lenses, at most 3 lenses in total.
5. After fixes, run at most two more review rounds. Then stop and report what is left.

## 4. Rules

- Respect protected paths. If the pre-tool hook blocks an edit, create a new file (for example a new migration) or ask the user.
- Do not disable the gate yourself. If it keeps failing for reasons outside the task (pre-existing failures), tell the user.
- Ask before you make tests red, or finish the step first. The gate runs whenever you stop and cannot tell a question from a finished report, so a question asked while the session's own tests are red gets blocked, on purpose.
- Prefer scripts and commands over reading large outputs into context.

## 5. Final report (keep it short)

The gate runs after this report, so you have not seen its result for it. State only what you ran
yourself, and never write that the gate passed. If the gate is red it blocks the report and sends
you back with the errors.

```
Size: <small|medium|large>
Changed: <files or areas, one line>
Tests: <added N; the commands you ran and their result | not run>
Review: <lenses run> — <N fixed, N dismissed with reason>
Gate: runs after this report and blocks it if red
```

Request: $ARGUMENTS
