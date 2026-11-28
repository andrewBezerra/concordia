---
name: init
description: Set up Concordia in this repository — write concordia.json for the detected stack and check that build and test commands work.
disable-model-invocation: true
---

# Concordia: init

1. Run `"<Concordia helper>" init` (the path is in the session context as "Concordia helper"). If the context has no such entry (the session started outside a git repository, or the context was lost), use `${CLAUDE_PLUGIN_ROOT}/bin/concordia-hook` instead; in PowerShell or cmd on Windows, use `${CLAUDE_PLUGIN_ROOT}/bin/concordia-hook.cmd`. If the variable is empty, look for the newest `concordia-hook` under `~/.claude/plugins/cache/concordia/concordia/*/bin/`.
   If the helper is not available, tell the user the plugin binaries are missing and stop.
2. Open the generated `concordia.json` and adapt it to this repository:
   - `commands.build` and `commands.test` must work from the repository root. If there are several
     solution files, point them at the right one (for example `dotnet build App.slnx`).
   - Run both commands once. If the repository already has failing tests, tell the user: the gate
     would block every task until they are fixed or excluded.
   - `protect`: paths whose committed files must never be edited (applied migrations, generated code).
   - `lenses`: adjust the paths and signals that trigger security, performance and domain review.
   - If `concordia.json` is already committed, Concordia blocks you from editing it: show the user the
     change you suggest and let them make it.
3. Run `"<Concordia helper>" gate status` and show the result.
4. Suggest, without writing it, a short `CLAUDE.md` section with the build and test commands and
   the conventions you noticed. Only write it if the user agrees.

Keep the summary to a few lines: what was configured and anything the user must decide.
