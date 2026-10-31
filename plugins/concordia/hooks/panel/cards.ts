import type { AgentRow, CiState, ProjectCiTime, TokensSummary } from '../../types'

// The panel's content without the drawing: one card per feature, with what it does, how it stands now in plain words,
// and the color of that state.

export type Tone = 'ok' | 'attention' | 'error' | 'neutral'

export type CardLink = { label: string; href: string }

export type CardLine = { tone: Tone; text: string; detail?: string; agentId?: string; links?: CardLink[] }

export type Card = { title: string; role: string; tone: Tone; lines: CardLine[] }

export const SYMBOL: Record<Tone, string> = { ok: '✔', attention: '◐', error: '✘', neutral: '○' }

export const FINISHED = new Set(['completed', 'failed', 'killed'])

/** The words for an agent's status ($.agent.list()) or a session's (`claude agents --json`). */
export const STATUS_NAME: Record<string, string> = {
  pending: 'queued',
  running: 'working',
  waiting: 'waiting',
  idle: 'idle',
  completed: 'finished',
  failed: 'failed',
  killed: 'stopped',
  working: 'working',
  blocked: 'blocked',
  done: 'finished',
  stopped: 'stopped',
  busy: 'busy',
}

/** The time of an instant, like 14:05. */
export function clockTime(ms: number): string {
  const d = new Date(ms)
  return `${String(d.getHours()).padStart(2, '0')}:${String(d.getMinutes()).padStart(2, '0')}`
}

const plural = (n: number, one: string, many: string) => `${n} ${n === 1 ? one : many}`

const projectName = (project: string) => project.split('/').pop() ?? project

/** A short elapsed time: 45 s, 3 min 05 s. */
export function elapsed(ms: number): string {
  const s = Math.max(0, Math.floor(ms / 1000))
  const m = Math.floor(s / 60)
  return m > 0 ? `${m} min ${String(s % 60).padStart(2, '0')} s` : `${s} s`
}

/** The mean time of each project measured, and the tips of the current one, first on the list. */
function timeLines(times: readonly ProjectCiTime[]): CardLine[] {
  const means = times.map<CardLine>(t => ({
    tone: 'neutral',
    text: `Mean time of ${projectName(t.project)}: ${elapsed(t.meanMs)}`,
    detail: `${t.workflow}, ${plural(t.samples, 'run', 'runs')} · measured at ${clockTime(t.measuredAt)}`,
  }))
  const tips = (times[0]?.tips ?? []).map<CardLine>(text => ({ tone: 'attention', text }))
  return [...means, ...tips]
}

/** The CI card: the branch's PR, whether checks run, passed or failed, the mean time and tips; or what is missing. */
export function ciCard(state: CiState | null, checkedAt: number, times: readonly ProjectCiTime[] = [], problem: string | null = null): Card {
  const card = prCard(state, checkedAt, problem)
  return { ...card, lines: [...card.lines, ...timeLines(times)] }
}

function prCard(state: CiState | null, checkedAt: number, problem: string | null): Card {
  const base = { title: 'Pull request CI', role: "Follows the GitHub checks of the current branch's pull request." }
  const when = checkedAt > 0 ? `checked at ${clockTime(checkedAt)}` : undefined
  if (checkedAt === 0) return { ...base, tone: 'neutral', lines: [{ tone: 'neutral', text: 'Checking CI…' }] }
  if (problem) return { ...base, tone: 'neutral', lines: [{ tone: 'neutral', text: problem, detail: when }] }
  if (!state) return { ...base, tone: 'neutral', lines: [{ tone: 'neutral', text: 'No open pull request on this branch.', detail: when }] }
  const pr = `PR #${state.pr}`
  const links: CardLink[] = state.url ? [{ label: 'open the PR checks ›', href: state.url }] : []
  if (state.total === 0) return { ...base, tone: 'neutral', lines: [{ tone: 'neutral', text: `${pr}: no checks yet.`, detail: when, links }] }
  if (state.failures.length > 0) {
    // The problem first: that is where someone looking at the card wants to go.
    const failed = state.failureLinks.map(f => ({ label: `see the failure in ${f.name} ›`, href: f.url }))
    return { ...base, tone: 'error', lines: [{ tone: 'error', text: `${pr}: failed in ${state.failures.join(', ')}.`, detail: when, links: [...failed, ...links] }] }
  }
  if (state.running > 0)
    return { ...base, tone: 'attention', lines: [{ tone: 'attention', text: `${pr}: running, ${state.passed} of ${state.total} checks passed.`, detail: when, links }] }
  return { ...base, tone: 'ok', lines: [{ tone: 'ok', text: `${pr}: all green (${plural(state.total, 'check', 'checks')}).`, detail: when, links }] }
}

/** A bar of the given cells, full in the given proportion. */
export function bar(fraction: number, cells = 10): string {
  const full = Math.max(0, Math.min(cells, Math.round(fraction * cells)))
  return '█'.repeat(full) + '░'.repeat(cells - full)
}

function agentTone(status: string): Tone {
  if (status === 'failed' || status === 'killed') return 'error'
  if (status === 'completed') return 'ok'
  return 'attention'
}

function progressOf(a: AgentRow): string {
  if (!a.progress) return 'no progress file'
  const p = a.progress
  const line = `${bar(p.done / p.total)} ${Math.round((100 * p.done) / p.total)}% (${p.done} of ${p.total})`
  return p.step ? `${line} · ${p.step}` : line
}

/**
 * The agents card: how many finished, and each with its status, model and time; the percentage only when the agent
 * writes its progress file. Without the file, no number: the panel never invents progress.
 */
export function agentsCard(agents: readonly AgentRow[], now: number): Card {
  const base = { title: 'Agents', role: 'The subagents of this session, and the progress each one writes.' }
  if (agents.length === 0) return { ...base, tone: 'neutral', lines: [{ tone: 'neutral', text: 'No subagents in this session.' }] }
  const finished = agents.filter(a => FINISHED.has(a.status)).length
  let tone: Tone = 'attention'
  if (agents.some(a => agentTone(a.status) === 'error')) tone = 'error'
  else if (finished === agents.length) tone = 'ok'
  const lines: CardLine[] = [{ tone, text: `${bar(finished / agents.length)} ${finished} of ${agents.length} finished` }]
  for (const a of agents) {
    const model = a.model ? ` (${a.model})` : ''
    const time = a.startedAt ? ` · ${elapsed(now - a.startedAt)}` : ''
    lines.push({ tone: agentTone(a.status), text: `${a.description}${model}: ${STATUS_NAME[a.status] ?? a.status}${time}`, detail: progressOf(a), agentId: a.id })
  }
  return { ...base, tone, lines }
}

/** Tokens in few letters: 950, 12k, 1.4M. */
export function shortTokens(n: number): string {
  return n >= 1_000_000 ? `${(n / 1_000_000).toFixed(1)}M` : n >= 1_000 ? `${Math.round(n / 1_000)}k` : `${Math.round(n)}`
}

/** The tokens card: the last prompt against the mean, and the first suggestion when there is one. */
export function tokensCard(summary: TokensSummary | null): Card {
  const base = { title: 'Tokens per prompt', role: 'Counts the tokens of each prompt and suggests how to spend less.' }
  if (!summary) return { ...base, tone: 'neutral', lines: [{ tone: 'neutral', text: 'No prompts counted yet.', detail: 'the first shows when its turn ends' }] }
  const tone: Tone = summary.aboveMean ? 'attention' : 'ok'
  const lines: CardLine[] = [
    {
      tone,
      text: `Last: ${shortTokens(summary.last)} tokens · mean of the last ${summary.prompts}: ${shortTokens(summary.mean)}`,
      ...(summary.aboveMean ? { detail: 'more than twice the mean' } : {}),
    },
    { tone: 'neutral', text: `Context: ${shortTokens(summary.context)} · ${summary.meanSteps.toFixed(1)} steps per prompt` },
  ]
  if (summary.tip) lines.push({ tone: 'attention', text: summary.tip, detail: 'the other suggestions are in /concordia-tokens' })
  return { ...base, tone: summary.tip && tone === 'ok' ? 'attention' : tone, lines }
}
