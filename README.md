# Concordia

**Agent orchestration for Claude Code that proves what it ships.**

Most orchestration plugins ask the agent to test and review its work. Concordia makes it
unavoidable, keeps the process proportional to the task, and measures the result.

- **Verification gate enforced by hooks.** The agent cannot finish while the build or the tests
  are red. Errors come back summarized to a few lines, not as pages of logs.
- **Process sized to the task.** Small changes go straight to implementation plus a test. Medium
  changes get tests first and one reviewer. Large changes get a plan you approve, tests first and
  parallel reviewers.
- **Risk-based review, decided by a script.** A zero-token triage reads the diff and picks the
  review lenses (security, performance, domain) from paths and code signals. The reviewer can
  escalate to one more lens when it sees something outside its own.
- **Measured.** A public benchmark with small and large tasks and hidden acceptance tests compares
  Claude Code alone, Concordia and Superpowers.

The hooks are a single native binary (C#, Native AOT): a few milliseconds per call and nothing to
install besides Claude Code. A .NET preset ships built in; other stacks work through `concordia.json`.

> Status: early (0.1). The benchmark numbers will be published in this README once collected.

## Install

```text
/plugin marketplace add andrewBezerra/concordia
/plugin install concordia@concordia
```

Then, in each repository:

```text
/concordia:init
```

## Use

| Command | What it does |
| --- | --- |
| `/concordia:feature <request>` | Triage (small, medium or large), then the matching pipeline: tests, implementation, review |
| `/concordia:fix <bug>` | Reproduce with a failing test, fix the root cause, prove it |
| `/concordia:init` | Write `concordia.json` for the detected stack and check the commands |

The gate works in every session, with or without these commands: whenever the agent changed code,
it must leave the build and tests green before it stops.

## How it works

```text
SessionStart ─▶ one line of context (no skills preloaded)
Edit / Write ─▶ pre-tool: block edits to committed protected files (e.g. applied migrations)
             └▶ post-tool: remember the file (formatting happens once, at the end)
Agent stops  ─▶ stop gate: format edited files → build → test
                  red   → errors summarized and sent back to the agent (up to 3 attempts)
                  green → done; unchanged trees are never re-verified
Review       ─▶ review-pack: diff + stats into one file, lenses chosen by rules
                  reviewer subagents read the pack and answer in JSON with confidence scores
```

Components:

| Piece | Kind | Role |
| --- | --- | --- |
| `feature`, `fix`, `init` | Skills | Orchestration playbooks |
| `planner`, `test-author`, `reviewer` | Subagents | Plan, write failing tests, review one lens |
| `concordia-hook` | Native binary | Hooks, review pack, gate control |

## Configure

`concordia.json` at the repository root (or `.claude/concordia.json`) overrides the preset.
Every field is optional.

```jsonc
{
  "commands": {
    "format": "dotnet format --include {files} --verbosity quiet", // "" disables a step
    "build": "dotnet build App.slnx --nologo -v q -clp:ErrorsOnly",
    "test": "dotnet test App.slnx --nologo -v q --no-build --logger \"console;verbosity=minimal\""
  },
  "watch": ["**/*.cs", "**/*.csproj"],      // changes that require verification
  "protect": ["**/Migrations/**"],          // committed files here cannot be edited
  "lenses": {
    "security": { "paths": ["**/Auth/**"], "signals": ["FromSqlRaw"] }
  },
  "escalation": { "maxDiffLines": 400, "maxProjects": 3 },
  "gate": { "enabled": true, "maxAttempts": 3, "maxOutputLines": 30, "timeoutSeconds": 600 }
}
```

Turn the gate off for a moment by starting Claude Code with `CONCORDIA_GATE=off`, or per repository
with `concordia-hook gate off "reason"` (and `gate on` to restore it). `gate off` only works from an
interactive terminal, so the agent cannot run it through its Bash tool. Under mintty (Git Bash on
Windows) stdin is not a console, so use another terminal there.

A `concordia.json` that cannot be parsed blocks the agent once with the error. If the same error is
still there at the next stop, the gate lets it finish and warns that nothing was verified.

## Build from source

Requirements: .NET 10 SDK and the Native AOT toolchain for your OS (clang and zlib on Linux,
Xcode command line tools on macOS, the C++ workload on Windows).

```bash
dotnet test Concordia.slnx
build/publish-local.sh            # or build/publish-local.ps1 on Windows
claude --plugin-dir plugins/concordia
```

CI builds the binary for Linux, macOS and Windows (x64 and arm64) and publishes the installable
plugin to the `dist` branch, which the marketplace points to.

## Benchmark

See [benchmark/README.md](benchmark/README.md).

## License

[MIT](LICENSE)
