# Concordia (repository guide)

Claude Code plugin for verified agent orchestration. Everything in this repository is in English.

## Layout
- `src/Concordia.Hook/` — the `concordia-hook` binary (C#, .NET 10, Native AOT). Entry: `Cli.cs`; one class per command in `Commands/`
- `tests/Concordia.Hook.Tests/` — xUnit tests; `GateIntegrationTests` needs `git` on PATH
- `plugins/concordia/` — the plugin: manifest, hooks, skills, agents, launcher scripts in `bin/`
- `benchmark/` — sample app, tasks with hidden tests, runner (`runner/run.cs`)
- `.github/workflows/build.yml` — tests, AOT matrix, `dist` branch publishing

## Commands
- Test: `dotnet test Concordia.slnx`
- Local plugin build: `build/publish-local.sh` then `claude --plugin-dir plugins/concordia`
- Benchmark sanity: `benchmark/scripts/sanity.sh`

## Rules
- The hook must stay AOT-safe: System.Text.Json only through `ConcordiaJson` (source generated), regexes via `[GeneratedRegex]` or plain `Regex`, no reflection-based APIs. Warnings are errors.
- Hooks fail open: an internal error must never block the user's session (see `Cli.Run`).
- Anything a hook prints to stderr on exit code 2 goes into the model context. Keep it short; summarize through `OutputSummarizer`.
- Prefer a deterministic script step over an instruction in a skill when both would work.
- Hidden benchmark tests may only use HTTP plus `IClock`; never reference types an agent might rename.
