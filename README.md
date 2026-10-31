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
- **Measured.** The repository includes a benchmark with small and large tasks and hidden acceptance
  tests that compares Claude Code alone, Concordia and Superpowers. Results are pending.

The hooks are a single native binary (C#, Native AOT): a few milliseconds per call and nothing to
install besides Claude Code (2.1.287 or later for the panel; `gh` is optional, for its CI card). A .NET preset ships built in; other stacks work through `concordia.json`.

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
| `/concordia-panel` | Open the panel, and undo having closed it |
| `/concordia-ci` | The CI of the current branch's pull request, check by check |
| `/concordia-tokens [clear]` | The tokens of the last 20 prompts, the mean and what to change; `clear` resets it |
| `/concordia-agents` | The subagents and background sessions, with their progress |
| `/concordia-agent <id>` | One subagent's screen: what it said and the tools it called |

The gate works in every session, with or without these commands: whenever the agent changed code,
it must leave the build and tests green before it stops. Files that were already dirty when the
session started do not trigger it, as long as they stay as they were. Once the session changes
anything, the whole working tree is verified, your uncommitted work included.

The gate runs after the agent's final report, so the report only states what the agent ran itself.
A red gate blocks the report and sends the agent back, up to `gate.maxAttempts` times. The next red
stop is released, and stays released until the tree changes. A build or test command that exceeds
`gate.timeoutSeconds` skips the gate. Neither is a pass: no block does not mean green, and the
gate's own message says what happened. The gate also runs when the agent stops to ask you
something, and it cannot tell a question from a finished report. A question asked while the
session's own tests are red is blocked like any other stop, on purpose, so the agent should ask
before making tests red, or finish the step first.

## The panel

A pane with three cards, updating by itself: **Agents**, **Pull request CI** and **Tokens per prompt**. It needs
Claude Code 2.1.287 or later, and works with or without a `concordia.json`. The status line shows CI and tokens together.

Headless runs (`claude -p`) get the commands only: no timers, no `gh` calls, no panel.

It opens by itself when a session starts, on a timer, so it never delays the first prompt. Claude Code only places a pane
nobody asked for from 144 terminal columns (110 once you have opened it yourself). Below that you get one short toast per
session saying to type `/concordia-panel`. Close the pane and it stays closed in later sessions, on every project,
until you type `/concordia-panel` again.

Those commands use a `concordia-` prefix rather than `/concordia:panel`: a command registered at run time can only have
letters, digits, `_` and `-` in its name.

**CI.** The card follows the current branch's pull request through the GitHub CLI, with a link to each failed job and then
to the checks page, plus the mean CI time of the current repository and tips drawn only from the numbers of its runs, and
the means of up to 3 other projects. A toast says when a running CI ends. It asks `gh` every 30 s, and every 5 minutes once
CI has finished or `gh` fails, keeping the last state on screen through a failure. `gh` is optional: without it, outside a repository, with no GitHub remote or when it is not
logged in, the card says what is missing and everything else keeps working.

**Tokens.** Each prompt is counted with the subagents it opened. The weights compare prompts with each other; they are an
approximation of the price tables, not a bill. The history is kept across sessions and projects, in Claude Code's plugin store. Of each prompt's text, only the first 40
characters are stored, whitespace collapsed; those can still hold whatever you typed. `/concordia-tokens clear` deletes it.

**Agent progress.** The panel shows each subagent's status, model and time. A percentage appears only if the agent writes
one: **the panel never invents progress**, from the status or the elapsed time or anything else. To report progress, have
the agent write this file, and rewrite it after each finished item. A file older than the agent is ignored, as another
run's, and so is any file when the panel did not see the agent start:

```text
~/.claude/concordia/agent-progress/<slug>.json
{ "done": 34, "total": 89, "step": "S1192" }
```

`<slug>` is the description of the agent's call, without accents, in lowercase, with anything but letters and digits turned
into `-`: "Fix the Sonar findings" becomes `fix-the-sonar-findings.json`. `step` is optional. `feitos` and `etapa` are read
as `done` and `step`. Without a known total, write no file: the agent then shows its status and time, and no number.

## How it works

```text
SessionStart ─▶ one line of context (no skills preloaded)
Edit / Write ─▶ pre-tool: block edits to committed protected files (e.g. applied migrations),
             │            to a committed concordia.json and to the gate's state
Shell        ─▶ pre-tool: block commands that visibly cite a committed concordia.json or the gate's state
             └▶ post-tool: remember the file (formatting happens once, at the end)
Agent stops  ─▶ stop gate: format edited files → build → test
                  red   → errors summarized and sent back to the agent (up to 3 attempts),
                          then released until the tree changes
                  green → done; unchanged trees are never re-verified
                  tests removed or skipped → blocked first; only you can accept it
                  timed out → blocked once, saying nothing was verified; then warns, without re-running
                  build outputs locked by a process → blocked once, not counted as a failure; then warns,
                          and the next stop verifies again
Review       ─▶ review-pack: diff + stats into one file, lenses chosen by rules
                  reviewer subagents read the pack and answer in JSON with confidence scores
```

Components:

| Piece | Kind | Role |
| --- | --- | --- |
| `feature`, `fix`, `init` | Skills | Orchestration playbooks |
| `planner`, `test-author`, `reviewer` | Subagents | Plan, write failing tests, review one lens |
| `concordia-hook` | Native binary | Hooks, review pack, gate control |
| `hooks/panel` | Mod (TypeScript, run by Claude Code) | The panel, its commands and the status line |

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
  "gate": { "enabled": true, "maxAttempts": 3, "maxOutputLines": 30, "timeoutSeconds": 600 },
  "testGuard": {                            // each list replaces the preset's; [] turns that check off
    "paths": ["**/tests/**/*.cs"],          // the files that are scanned
    "markers": ["[Fact]", "[Theory]"],      // each occurrence counts as one test
    "skips": ["Skip ="]                     // each occurrence counts as one skip
  }
}
```

Turn the gate off for a moment by starting Claude Code with `CONCORDIA_GATE=off`, or per repository
with `concordia-hook gate off "reason"` (and `gate on` to restore it). `gate off` only works from an
interactive terminal, so the agent cannot run it through its Bash tool. Under mintty (Git Bash on
Windows) stdin is not a console, so use another terminal there.

A `concordia.json` that cannot be parsed blocks the agent once with the error. If the same error is
still there at the next stop, the gate lets it finish and warns that nothing was verified.

A build or test that runs past `timeoutSeconds` blocks the agent once, saying nothing was verified and
which step timed out. At the next stop on the same tree the gate lets it finish, warns you through
`systemMessage`, and does not run the step again. Changing a watched file or committing runs it again.
Timeouts count against `maxAttempts` like failures do, so a tree that keeps changing cannot block forever.

Claude Code kills the Stop hook after the `timeout` in `hooks.json`: 1860 s, which covers format, build
and test at the default 600 s each. If you raise `timeoutSeconds`, keep three times its value below that,
or the hook can be killed before it reports anything.

### What the agent cannot change

The gate's own inputs are protected from the agent's tools. `pre-tool` blocks:

- `Edit`, `Write`, `MultiEdit` and `NotebookEdit` on a committed `concordia.json` or
  `.claude/concordia.json`, and on a new root `concordia.json` that would override a committed
  `.claude/concordia.json`. Files that are not committed stay editable, because `/concordia:init` writes them.
- The same tools on anything under the state directory (`concordia` in the git directory).
- `Bash` and `PowerShell` commands that cite those paths, reading ones included (`cat concordia.json`,
  or the name in a commit message). The `Read` tool is not blocked, so the agent reads them that way.

**This is a brake, not access control.** It stops an agent that reaches for the obvious edit, and tells it
to ask you. The shell check only reads the command's text, so a script, a variable, a glob or a path
built at run time gets past it, and so does a build step the agent wrote. An uncommitted `concordia.json`
is not protected at all. Review changes to these files like any other change.

The gate also keeps the `concordia.json` it found at session start, and that version checks the whole
session, even if the file is edited or committed later. A stop that sees a different file says so, and
the new version applies to a new session, so change the gate's configuration between sessions. This
raises the bar but does not close it: the kept copy is in the state file. A session that started with no
`concordia.json` keeps the preset, so a file `/concordia:init` writes counts from the next session; in a
repository with no recognizable stack there is nothing to keep, and a new file counts at once.
A green result only counts for the config that produced it. `gate status` and `gate accept-test-removal` read the current file, not the kept copy.

### Removed or skipped tests

Removing or skipping a test blocks the agent, and only you can accept it. At each stop the gate
compares the session's starting point (its first HEAD plus the test files already dirty then) with
the tree, committed changes included. It blocks with the list of files when, across all changed test
files, there are fewer test markers or more skip markers. The .NET preset covers xUnit, NUnit and
MSTest in C# and F#, including skips written inside the attribute (`[Test, Ignore("x")]`,
`[TestCase(1, Ignore = "x")]`) and in the test body (`Assert.Skip(`, `Assert.SkipWhen(`,
`Assert.SkipUnless(`, `Assert.Ignore(`, `Assert.Inconclusive(`).

To accept, run `concordia-hook gate accept-test-removal` yourself. Like `gate off`, it needs an
interactive terminal, so the agent cannot run it through its Bash tool. The acceptance covers exactly
the current tree (the session's start plus the content of the changed watched and test files):
committing that tree keeps it, and any other change to those files needs a new acceptance. A change
to a file that is neither watched nor a test file does not.

After `maxAttempts` blocks the session may stop, so it cannot loop forever. That is **not** an
acceptance: every stop warns through `systemMessage` that the test removal is unaccepted, `gate status`
says so too, and the gate blocks again as soon as the tree changes. Restoring the tests clears all of it.

Each session keeps its own record, because each one measures from its own start. A session that began
after the removal sees no loss, and its clean stop leaves the other session's acceptance or warning alone.
An acceptance covers the sessions blocked so far; another session that started before the removal and
stops later is blocked in its turn, and needs its own.

This is a text check that counts substrings. It does not read test code, and it can be evaded:

- **It compares totals, on purpose.** That is what lets a test move or a file be renamed without
  blocking, but it also means a deleted test plus an unrelated new one does not block, and removing a
  skip in one place hides adding one in another.
- **It misses:** emptying a test body or weakening its assertions, `/* */` and `#if` around a test (only
  `//` lines are ignored), a skip through a custom attribute, a skip that returns early or uses
  `Assume.That(`, an attribute split over several lines so that its text no longer matches,
  `[MemberData]` rows, excluding tests in the test command or the project file, and files outside `paths`.
- **The agent can reach its own inputs:** `concordia.json` is in the working tree, and the gate's
  state, with its kept copy of that file, is in the git directory. The brake described in [What the agent cannot change](#what-the-agent-cannot-change)
  stops the obvious edits; an agent determined to do it can still get past it. The terminal check only tests
  that stdin is a console, so on Windows an agent can also get past it, and past `gate off`, by opening a
  new console window (`Start-Process` does it).
- **It does not run** when the gate is off, or in a repository with neither a build nor a test command.

It also blocks when nothing is wrong, and you accept those cases the same way: `Skip =` or `Ignore =` in
ordinary code such as a paging object under `tests/`, a test file with markers moved outside `paths`, and tests that
disappear because the session merged or pulled a change that removed them. A deleted file under `paths`
with no markers, such as a test helper, does not block.

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
