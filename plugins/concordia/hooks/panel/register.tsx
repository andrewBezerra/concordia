import { atom, read, update } from 'claude-code'
import type { EngineInterface, On, Register } from 'claude-code'

import type { AgentBirth, AgentProgress, AgentRow, BackgroundSession, CiState, ConversationLine, ProjectCiTime, TokensSummary } from '../../types'

import { agentsCard, bar, ciCard, elapsed, FINISHED, STATUS_NAME, SYMBOL, tokensCard, type Card, type Tone } from './cards'
import { explainGhFailure, explainGhStartFailure, isFinished, statusLine, summarize, type Check } from './ci-state'
import { mainWorkflow, measure, remember, type Job, type Run, type RunWithJobs } from './ci-times'
import { conversationLines, SCREEN_LINES } from './conversation'
import { parseProgress, slug } from './progress'
import { safely } from './safely'
import { composeStatus } from './status'
import { add, summarizeTokens, tokensReport, tokensStatusLine, total, WINDOW, zero, type PartialRecord, type PromptRecord } from './tokens-stats'

// The one hooks module of the plugin. The command hooks in hooks.json are the gate; this is the panel. Everything that
// touches $ is in this file, because the engine refuses a module that passes $ across an import: the other files here
// are plain functions, tested on their own.

// ---- The shared status line. Each part lives in $.state, so a reload of the module keeps both.

const ciPart = atom({ plugin: 'concordia', key: 'statusCi' } as const, null as string | null)
const tokensPart = atom({ plugin: 'concordia', key: 'statusTokens' } as const, null as string | null)

async function setStatus($: EngineInterface, part: 'ci' | 'tokens', text: string | undefined): Promise<void> {
  if (part === 'ci') await update($, ciPart, () => text ?? null)
  else await update($, tokensPart, () => text ?? null)
  $.ui.status(composeStatus(await read($, ciPart), await read($, tokensPart)))
}

// ---- Agents. The subagents of this session and the background sessions. An agent's percentage only appears when the
// agent itself writes it to a progress file: the API has neither a percentage nor tokens per agent, and the panel never
// invents one from the status or the elapsed time.
//
// The file: ~/.claude/concordia/agent-progress/<slug of the agent's description>.json, documented in the README.

const AGENTS_PANE = 'concordia-agents'
const AGENT_SCREEN = 'concordia-agent'
const AGENTS_PERIOD_MS = 2_000
const SESSIONS_PERIOD_MS = 10_000
const WIDE_BAR = 20

const births = atom({ plugin: 'concordia', key: 'agentBirths' } as const, {} as Record<string, AgentBirth>)
const agents = atom({ plugin: 'concordia', key: 'agents' } as const, [] as AgentRow[])
const sessions = atom({ plugin: 'concordia', key: 'sessions' } as const, [] as BackgroundSession[])

// The agent on the screen and the end of its conversation, or why it could not be read.
const openAgentId = atom({ plugin: 'concordia', key: 'openAgent' } as const, null as string | null)
const conversation = atom({ plugin: 'concordia', key: 'conversation' } as const, [] as ConversationLine[])
const conversationError = atom({ plugin: 'concordia', key: 'conversationError' } as const, null as string | null)

function progressLine(p: AgentProgress): string {
  const percent = Math.round((100 * p.done) / p.total)
  return `${bar(p.done / p.total, WIDE_BAR)} ${percent}% (${p.done} of ${p.total})` + (p.step ? ` · ${p.step}` : '')
}

async function progressFolder($: EngineInterface): Promise<string | undefined> {
  const home = (await $.env.get('USERPROFILE')) ?? (await $.env.get('HOME'))
  return home ? `${home}/.claude/concordia/agent-progress` : undefined
}

async function readProgress($: EngineInterface, folder: string, description: string): Promise<AgentProgress | undefined> {
  try {
    return parseProgress(await $.fs.read(`${folder}/${slug(description)}.json`))
  } catch {
    return undefined
  }
}

async function refreshAgents($: EngineInterface): Promise<void> {
  const list = await $.agent.list()
  const known = await read($, births)
  const folder = await progressFolder($)
  const rows: AgentRow[] = []
  for (const a of list) {
    const birth = known[a.id]
    rows.push({
      id: a.id,
      description: a.description,
      status: a.status,
      model: birth?.model,
      startedAt: birth?.startedAt,
      progress: folder ? await readProgress($, folder, a.description) : undefined,
    })
  }
  await update($, agents, () => rows)
  await refreshOpenScreen($)
}

async function readConversation($: EngineInterface, id: string): Promise<void> {
  const messages = await $.session.messages({ agentId: id })
  if ('deny' in messages) {
    await update($, conversationError, () => messages.deny)
    return
  }
  await update($, conversationError, () => null)
  await update($, conversation, () => conversationLines(messages))
}

// Only while the screen is open: closed, the timer reads nobody's conversation.
async function refreshOpenScreen($: EngineInterface): Promise<void> {
  const id = await read($, openAgentId)
  if (id === null) return
  if ((await $.ui.panes()).some(p => p.id === AGENT_SCREEN)) await readConversation($, id)
  else await update($, openAgentId, () => null)
}

/** Opens the screen of an agent of this session; false when it is not on the list. */
async function openAgent($: EngineInterface, id: string): Promise<boolean> {
  const agent = (await $.agent.list()).find(a => a.id === id)
  if (!agent) return false
  await update($, conversation, () => [])
  await update($, openAgentId, () => id)
  await readConversation($, id)
  await $.ui.open({ id: AGENT_SCREEN, title: `Agent: ${agent.description}` })
  return true
}

async function refreshSessions($: EngineInterface): Promise<void> {
  try {
    const r = await $.process.run(['claude', 'agents', '--json'], { timeoutMs: 15_000 })
    if (r.exitCode !== 0) return
    const parsed = JSON.parse(r.stdout) as Array<Record<string, unknown>>
    const background = parsed
      .filter(s => s.kind === 'background')
      .map(s => ({
        name: String(s.name ?? s.id ?? 'unnamed'),
        folder: String(s.cwd ?? ''),
        kind: String(s.kind),
        status: String(s.state ?? s.status ?? '?'),
        startedAt: Number(s.startedAt ?? 0),
        waitingFor: typeof s.waitingFor === 'string' ? s.waitingFor : undefined,
      }))
    await update($, sessions, () => background)
  } catch {
    // No claude on PATH, or JSON that does not parse: the sessions section stays as it was.
  }
}

// Spawning `claude agents` costs a process, so it only runs while the pane that shows its answer is open.
async function refreshSessionsIfShown($: EngineInterface): Promise<void> {
  if ((await $.ui.panes()).some(p => p.id === AGENTS_PANE)) await refreshSessions($)
}

async function startAgents($: EngineInterface): Promise<void> {
  await $.command.register({ name: 'concordia-agents', description: 'Open the view of the subagents and background sessions, with their progress.' })
  await $.command.register({ name: 'concordia-agent', description: 'Open the screen of one subagent, by id, with what it is doing.', argumentHint: '<id>' })
  $.clock.every(AGENTS_PERIOD_MS, safely(() => refreshAgents($)))
  $.clock.every(SESSIONS_PERIOD_MS, safely(() => refreshSessionsIfShown($)))
}

function registerAgents(on: On): void {
  // $.agent.list() has neither the model nor the start time, so they are kept when the agent starts. next comes first
  // and nothing after it throws: the panel never stops or repeats an agent's start. No .catch for the same reason: one
  // that called next again could start the agent twice.
  on('agent.spawn', async ($, e, next) => {
    const started = await next(e)
    try {
      if (started && !started.deny && started.agentId) {
        const id = started.agentId
        await update($, births, current => ({ ...current, [id]: { model: started.model, startedAt: Date.now() } }))
      }
    } catch {
      // Without the model and the time, the agent shows with its status alone.
    }
    return started
  })

  on('command.run', { command: 'concordia-agents' }, async $ => {
    await safely(() => refreshAgents($))()
    await $.ui.open({ id: AGENTS_PANE, title: 'Concordia agents' })
    void safely(() => refreshSessions($))()
    return {}
  })

  on('command.run', { command: 'concordia-agent' }, async ($, e) => {
    const id = e.args.trim()
    return (await openAgent($, id)) ? {} : { text: `No agent ${id} in this session.` }
  })

  on('ui.render', { component: 'Pane', requestId: AGENT_SCREEN }, async ($, e) => {
    const { Box, Button, Text } = $.ui.resolve(e)
    const id = await read($, openAgentId)
    const lines = await read($, conversation)
    const problem = await read($, conversationError)
    const agent = (await read($, agents)).find(a => a.id === id)
    return (
      <Box flexDirection="column">
        {agent && <Text bold>{(STATUS_NAME[agent.status] ?? agent.status) + (agent.progress ? ` · ${progressLine(agent.progress)}` : '')}</Text>}
        {problem && <Text color="error">{`Could not read the conversation: ${problem}`}</Text>}
        {!problem && lines.length === 0 && <Text dimColor>No messages yet.</Text>}
        {lines.length === SCREEN_LINES && <Text dimColor>{`The last ${SCREEN_LINES} lines.`}</Text>}
        {lines.map(l => (
          <Box marginTop={l.kind === 'text' ? 1 : 0}>
            <Text color={l.kind === 'error' ? 'error' : undefined} dimColor={l.kind === 'request' || l.kind === 'tool'} wrap="wrap">
              {(l.kind === 'tool' || l.kind === 'error' ? '› ' : '') + l.text}
            </Text>
          </Box>
        ))}
        <Box marginTop={1}>
          <Button key="close" role="dismiss" onPress={safely(() => $.ui.close({ id: AGENT_SCREEN }))}>
            Close
          </Button>
        </Box>
      </Box>
    )
  })

  on('ui.render', { component: 'Pane', requestId: AGENTS_PANE }, async ($, e) => {
    const { Box, Button, Text } = $.ui.resolve(e)
    const list = await read($, agents)
    const background = await read($, sessions)
    const now = Date.now()
    const finished = list.filter(a => FINISHED.has(a.status)).length

    return (
      <Box flexDirection="column">
        <Text bold>Subagents of this session</Text>
        {list.length === 0 && <Text dimColor>No subagents in this session.</Text>}
        {list.length > 0 && <Text>{`${bar(finished / list.length, WIDE_BAR)} ${finished} of ${list.length} finished`}</Text>}
        {list.map(a => (
          <Box flexDirection="column" marginTop={1}>
            <Text bold={!FINISHED.has(a.status)} dimColor={FINISHED.has(a.status)}>
              {a.description + (a.model ? ` (${a.model})` : '')}
            </Text>
            <Text color={a.status === 'failed' ? 'red' : a.status === 'completed' ? 'green' : undefined}>
              {(STATUS_NAME[a.status] ?? a.status) + (a.startedAt ? ` · ${elapsed(now - a.startedAt)}` : '')}
            </Text>
            {a.progress ? <Text>{progressLine(a.progress)}</Text> : <Text dimColor>no progress file</Text>}
            <Button key={`view:${a.id}`} plain onPress={safely(() => openAgent($, a.id))}>
              see what it is doing ›
            </Button>
          </Box>
        ))}
        <Box marginTop={1}>
          <Text bold>Background sessions</Text>
        </Box>
        {background.length === 0 && <Text dimColor>None.</Text>}
        {background.map(s => (
          <Text>
            {`${s.name}: ${STATUS_NAME[s.status] ?? s.status}` + (s.waitingFor ? ` (${s.waitingFor})` : '') + (s.startedAt ? ` · ${elapsed(now - s.startedAt)}` : '')}
          </Text>
        ))}
      </Box>
    )
  })
}

// ---- Pull request CI.

// Every 30 s, asks gh for the CI of the current branch's pull request and shows it in the status line. When a running CI
// ends, a toast says if it went green or what failed. Without gh or outside a repository, the card says what is missing.
const CI_PERIOD_MS = 30_000

// The last state seen, to tell only when running turns into finished. A reload clears it: at worst, one toast fewer.
let previous: CiState | undefined

const ciState = atom({ plugin: 'concordia', key: 'ciState' } as const, null as CiState | null)
const ciProblem = atom({ plugin: 'concordia', key: 'ciProblem' } as const, null as string | null)
const ciCheckedAt = atom({ plugin: 'concordia', key: 'ciCheckedAt' } as const, 0)
const ciTimes = atom({ plugin: 'concordia', key: 'ciTimes' } as const, [] as ProjectCiTime[])

type Lookup = { state: CiState | null; problem: string | null }

async function lookUp($: EngineInterface): Promise<Lookup> {
  try {
    const r = await $.process.run(['gh', 'pr', 'view', '--json', 'number,url,statusCheckRollup'], { timeoutMs: 20_000 })
    if (r.exitCode !== 0) return { state: null, problem: explainGhFailure(r.stderr) }
    const pr = JSON.parse(r.stdout) as { number: number; url?: string; statusCheckRollup?: Check[] }
    return { state: summarize(pr.number, pr.statusCheckRollup ?? [], pr.url), problem: null }
  } catch (error) {
    return { state: null, problem: explainGhStartFailure(error) }
  }
}

async function refreshCi($: EngineInterface): Promise<void> {
  const { state, problem } = await lookUp($)
  await setStatus($, 'ci', state ? statusLine(state) : undefined)
  await update($, ciState, () => state)
  await update($, ciProblem, () => problem)
  await update($, ciCheckedAt, () => Date.now())
  // Only when the same PR goes from running to finished: not on every look, nor when the session opens.
  if (state && previous && previous.pr === state.pr && !isFinished(previous) && isFinished(state))
    $.ui.toast(state.failures.length > 0 ? `PR #${state.pr}: CI failed in ${state.failures.join(', ')}` : `PR #${state.pr}: CI green`)
  previous = state ?? undefined
}

// The mean time changes slowly, and measuring costs several gh calls: far less often than the PR state.
const TIMES_PERIOD_MS = 30 * 60_000
const TIMES_KEY = 'ci-times'
const MEASURED_RUNS = 40
const DETAILED_RUNS = 5

async function gh<T>($: EngineInterface, args: string[]): Promise<T | undefined> {
  const r = await $.process.run(['gh', ...args], { timeoutMs: 30_000 })
  return r.exitCode === 0 ? (JSON.parse(r.stdout) as T) : undefined
}

async function measureProject($: EngineInterface): Promise<ProjectCiTime | undefined> {
  const repo = await gh<{ nameWithOwner: string }>($, ['repo', 'view', '--json', 'nameWithOwner'])
  if (!repo) return undefined
  const runs = await gh<Run[]>($, ['run', 'list', '--status', 'completed', '--limit', String(MEASURED_RUNS), '--json', 'databaseId,workflowName,startedAt,updatedAt,conclusion,attempt'])
  const main = runs && mainWorkflow(runs)
  if (!runs || !main) return undefined
  const recent = runs.filter(r => r.workflowName === main.workflow && r.conclusion === 'success').slice(0, DETAILED_RUNS)
  const detailed: RunWithJobs[] = []
  for (const r of recent) {
    const run = await gh<{ jobs: Job[] }>($, ['run', 'view', String(r.databaseId), '--json', 'jobs'])
    if (run) detailed.push({ startedAt: r.startedAt, jobs: run.jobs })
  }
  return measure(repo.nameWithOwner, runs, detailed, Date.now())
}

async function refreshTimes($: EngineInterface): Promise<void> {
  const kept = ((await $.store.get(TIMES_KEY)) as ProjectCiTime[] | undefined) ?? []
  await update($, ciTimes, () => kept)
  const fresh = await measureProject($)
  if (!fresh) return
  const list = remember(kept, fresh)
  await $.store.set(TIMES_KEY, list)
  await update($, ciTimes, () => list)
}

async function checksText($: EngineInterface): Promise<string> {
  try {
    const r = await $.process.run(['gh', 'pr', 'checks'], { timeoutMs: 20_000 })
    const out = r.stdout.trim()
    if (out) return out
    if (r.exitCode === 0) return 'This pull request has no checks yet.'
    return explainGhFailure(r.stderr) ?? 'This branch has no open pull request.'
  } catch (error) {
    return explainGhStartFailure(error)
  }
}

async function startCi($: EngineInterface): Promise<void> {
  await $.command.register({ name: 'concordia-ci', description: 'The CI of the current branch pull request, check by check.' })
  // On timers: gh takes seconds, and session.start holds the first prompt.
  $.clock.after(1, safely(() => refreshCi($)))
  $.clock.every(CI_PERIOD_MS, safely(() => refreshCi($)))
  $.clock.after(1, safely(() => refreshTimes($)))
  $.clock.every(TIMES_PERIOD_MS, safely(() => refreshTimes($)))
}

function registerCi(on: On): void {
  on('command.run', { command: 'concordia-ci' }, async $ => {
    const text = await checksText($)
    await safely(() => refreshCi($))()
    return { text }
  })
}

// ---- Tokens per prompt.

const HISTORY_KEY = 'tokens-history'
async function history($: EngineInterface): Promise<PromptRecord[]> {
  return ((await $.store.get(HISTORY_KEY)) as PromptRecord[] | undefined) ?? []
}

const tokensSummary = atom({ plugin: 'concordia', key: 'tokensSummary' } as const, null as TokensSummary | null)

async function publish($: EngineInterface, list: readonly PromptRecord[]): Promise<void> {
  await setStatus($, 'tokens', tokensStatusLine(list))
  await update($, tokensSummary, () => summarizeTokens(list))
}

async function startTokens($: EngineInterface): Promise<void> {
  await $.command.register({
    name: 'concordia-tokens',
    description: 'The tokens of the last 20 prompts, the mean and what to change to spend less (clear resets it).',
    argumentHint: '[clear]',
  })
  $.clock.after(1, safely(async () => publish($, await history($))))
}

function registerTokens(on: On): void {
  // The prompt running now: the main loop's turn, which is what a prompt opens.
  let open: { turnId: string; prompt: string; partial: PartialRecord } | undefined

  on('turn.start', async ($, e, next) => {
    open = { turnId: e.turnId, prompt: e.text, partial: zero() }
    return next(e)
  })

  on('turn.step', async function* ($, e, next) {
    const answer = yield* next(e)
    if (open && !e.agentId && e.turnId === open.turnId) {
      open.partial.steps += 1
      open.partial.tools += answer.toolUses.length
      const u = answer.usage
      if (u) open.partial.context = Math.max(open.partial.context, u.input_tokens + u.cache_read_input_tokens + u.cache_creation_input_tokens)
    }
    return answer
  })

  on('turn.complete', async ($, e, next) => {
    const end = await next(e)
    if (!open) return end

    // A subagent's run: what it spent counts for the prompt that opened it.
    if (e.agentId) {
      if (e.usage) {
        add(open.partial, e.usage)
        const u = e.usage
        open.partial.subagents += u.input_tokens + u.cache_read_input_tokens + u.cache_creation_input_tokens + u.output_tokens
      }
      return end
    }

    if (e.turnId !== open.turnId) return end
    const partial = open.partial
    if (e.usage) add(partial, e.usage)
    const record: PromptRecord = { ...partial, at: new Date().toISOString(), prompt: open.prompt, durationMs: e.durationMs }
    open = undefined
    if (total(record) === 0) return end

    await safely(async () => {
      const list = [...(await history($)), record].slice(-WINDOW)
      await $.store.set(HISTORY_KEY, list)
      await publish($, list)
    })()
    return end
  })

  on('command.run', { command: 'concordia-tokens' }, async ($, e) => {
    if (e.args.trim() === 'clear') {
      await $.store.delete(HISTORY_KEY)
      await publish($, [])
      return { text: 'Token history cleared.' }
    }
    return { text: tokensReport(await history($)) }
  })
}

// ---- The panel.

// The panel: one pane with three cards, opened by itself when a session starts, and closed for good once the person
// closes it. The explicit command opens it again and forgets that choice.
const PANE = 'concordia-panel'
const TITLE = 'Concordia'
const CLOSED_KEY = 'panel-closed-by-user'

const openHintShown = atom({ plugin: 'concordia', key: 'openHintShown' } as const, false)

// The theme's colors, by what they mean.
const COLOR: Record<Tone, string> = { ok: 'success', attention: 'warning', error: 'error', neutral: 'inactive' }

async function autoOpen($: EngineInterface): Promise<void> {
  if ((await $.store.get(CLOSED_KEY)) === true) return
  const opened = await $.ui.open({ id: PANE, title: TITLE })
  if (opened.isPlaced || (await read($, openHintShown))) return
  await update($, openHintShown, () => true)
  $.ui.toast('The Concordia panel needs a wider terminal. Type /concordia-panel to open it now.')
}

async function startPane($: EngineInterface): Promise<void> {
  await $.command.register({ name: 'concordia-panel', description: 'Open the Concordia panel: agents, pull request CI and tokens per prompt.' })
  // On a timer: nothing here may hold the first prompt.
  $.clock.after(1, safely(() => autoOpen($)))
}

function registerPane(on: On): void {
  on('command.run', { command: 'concordia-panel' }, async $ => {
    await $.store.delete(CLOSED_KEY)
    await $.ui.open({ id: PANE, title: TITLE })
    return { text: 'Concordia panel opened.' }
  })

  // Only the person's close counts: `plugin` is this code, `unload` is the engine dropping the pane. next always runs,
  // since answering without it would keep the pane open.
  on('ui.close', { id: PANE }, async ($, e, next) => {
    const closed = await next(e)
    if (e.origin.kind === 'person') await safely(() => $.store.set(CLOSED_KEY, true))()
    return closed
  })

  on('ui.render', { component: 'Pane', requestId: PANE }, async ($, e) => {
    const { Box, Button, Link, Text } = $.ui.resolve(e)
    const cards = [
      // First: it is what changes while agents work.
      agentsCard(await read($, agents), Date.now()),
      ciCard(await read($, ciState), await read($, ciCheckedAt), await read($, ciTimes), await read($, ciProblem)),
      tokensCard(await read($, tokensSummary)),
    ]

    const draw = (c: Card) => (
      <Box flexDirection="column" borderStyle="round" borderColor={COLOR[c.tone]} paddingX={1} marginBottom={1}>
        <Text>
          <Text color={COLOR[c.tone]} bold>
            {SYMBOL[c.tone]} {c.title}
          </Text>
        </Text>
        <Text dimColor wrap="wrap">
          {c.role}
        </Text>
        <Box flexDirection="column" marginTop={1}>
          {c.lines.map(l => (
            <Box flexDirection="column">
              <Text color={COLOR[l.tone]} wrap="wrap">
                {SYMBOL[l.tone]} {l.text}
              </Text>
              {l.detail && (
                <Text dimColor wrap="truncate-end">
                  {'   '}
                  {l.detail}
                </Text>
              )}
              {l.links?.map(link => (
                <Text key={link.href}>
                  {'   '}
                  <Link href={link.href}>{link.label}</Link>
                </Text>
              ))}
              {l.agentId && (
                <Button key={`view:${l.agentId}`} plain onPress={safely(() => openAgent($, l.agentId!))}>
                  {'   see what it is doing ›'}
                </Button>
              )}
            </Box>
          ))}
        </Box>
      </Box>
    )

    return (
      <Box flexDirection="column">
        {cards.map(draw)}
        <Text dimColor wrap="wrap">
          Updates by itself. /concordia-ci lists the checks · /concordia-tokens lists the prompts · /concordia-agents opens the full agents view · /concordia-panel reopens this panel.
        </Text>
      </Box>
    )
  })
}

export const register: Register = on => {
  registerPane(on)
  registerAgents(on)
  registerCi(on)
  registerTokens(on)

  // The engine takes one unmatched session.start per module. One feature failing here must not take the others down.
  on('session.start', async ($, e, next) => {
    await safely(() => startPane($))()
    await safely(() => startAgents($))()
    await safely(() => startCi($))()
    await safely(() => startTokens($))()
    return next(e)
  })
}
