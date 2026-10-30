# Concordia benchmark

A reproducible way to answer one question: **does an orchestration setup make Claude Code deliver
correct code more often, and at what cost?** It compares Claude Code without plugins, with
Concordia and with Superpowers on the same tasks, small and large.

## How it works

1. `app/` is a small ASP.NET Core API (VetAgenda: veterinary appointments) with a test suite and
   one real bug.
2. Each task in `tasks/` has a prompt (`task.md`) and **hidden acceptance tests** (`hidden/`) that
   only talk to the API over HTTP, so they work with any implementation.
3. For every task × variant × run, the runner copies `app/` into a fresh git repository, runs
   `claude -p` with the prompt, and only then copies the hidden tests in and runs them, together
   with the pre-existing tests.
4. A run **succeeds** when every hidden test and every pre-existing test passes.

| Task | Size | Kind | What it asks |
| --- | --- | --- | --- |
| S01 | small | feature | Reject appointments in the past, using `IClock` |
| S02 | small | fix | Double booking across midnight (the planted bug) |
| S03 | small | feature | Daily agenda endpoint |
| L01 | large | feature | Cancellation with a late-cancellation fee, new contract fields and status codes |
| L02 | large | feature | Working hours, validation order and an availability endpoint |

The agent never sees the hidden tests. Prompts state the full contract (routes, status codes,
error codes), so a failure means the implementation is wrong, not that the test guessed a name.

## Running it

Requirements: .NET 10 SDK, Claude Code, `ANTHROPIC_API_KEY`.

```bash
# Sanity check, no AI: base app is green and every task's hidden tests fail on it
benchmark/scripts/sanity.sh

# Build the Concordia hook for this machine (the concordia variant loads the plugin from this repo)
build/publish-local.sh

# Run: all tasks, three variants, three runs each
dotnet run benchmark/runner/run.cs -- --variants baseline,concordia,superpowers --runs 3

# A quick subset
dotnet run benchmark/runner/run.cs -- --variants baseline,concordia --tasks S02,L01
```

Results land in `benchmark/results/<timestamp>/`: `results.csv` (one row per run) and `summary.md`
(success rate, hidden tests passed, mean cost, time and turns per size and per task). Work folders
of failed runs are kept in the system temp folder for inspection.

**Cost and safety.** Each run is a full agent session. A complete run (5 tasks × 3 variants × 3 runs)
is 45 sessions. Runs use `--permission-mode bypassPermissions` inside throwaway copies, so run the
benchmark in a container or VM, never on a machine with credentials you care about.

## Fairness rules

- Every variant gets the same prompt text. Concordia's variant prefixes it with its own skill
  (`/concordia:feature` or `/concordia:fix`), because that is how the plugin is meant to be used;
  Superpowers activates its skills on its own.
- Each variant runs with its own `CLAUDE_CONFIG_DIR` (`benchmark/.config/<variant>/`), so plugins
  installed for one variant never leak into another. To prepare the Superpowers variant once:

  ```bash
  CLAUDE_CONFIG_DIR=benchmark/.config/superpowers claude
  # inside Claude Code:
  /plugin marketplace add obra/superpowers-marketplace
  /plugin install superpowers@superpowers-marketplace
  ```
- Use the same model for all variants (`--model`), and at least three runs: agent results vary
  between runs, and one run proves nothing.
- Publish the whole `results.csv`, not only the summary.
