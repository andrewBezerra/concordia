import type { TurnUsage } from 'claude-code'

import type { TokensSummary } from '../../types'

import { shortTokens } from './cards'

/** A prompt sent and what it cost, adding up the turn's steps and the subagents it opened. */
export type PromptRecord = {
  at: string
  prompt: string
  input: number
  cacheRead: number
  cacheWritten: number
  output: number
  /** What the turn's subagents spent, already added to the fields above. */
  subagents: number
  steps: number
  tools: number
  /** The largest context a step of the turn sent again. */
  context: number
  durationMs: number
}

export const WINDOW = 20

/** All that is kept of a prompt's text, which may hold anything the person typed. */
export const promptExcerpt = (prompt: string) => prompt.replace(/\s+/g, ' ').trim().slice(0, 40)

// The weight of each kind of token in the price, against uncached input: cache read at 10%, written (1 hour cache) at
// 200%, output at 500%. An approximation of the price tables, to compare prompts with each other.
const WEIGHT = { input: 1, cacheRead: 0.1, cacheWritten: 2, output: 5 }

export const total = (r: PromptRecord) => r.input + r.cacheRead + r.cacheWritten + r.output

export const relativeCost = (r: PromptRecord) =>
  r.input * WEIGHT.input + r.cacheRead * WEIGHT.cacheRead + r.cacheWritten * WEIGHT.cacheWritten + r.output * WEIGHT.output

export const mean = (list: readonly PromptRecord[], of: (r: PromptRecord) => number) =>
  list.length === 0 ? 0 : list.reduce((sum, r) => sum + of(r), 0) / list.length

/** What to change, looking at the last prompts: each suggestion says the number that triggered it. */
export function suggestions(list: readonly PromptRecord[]): string[] {
  if (list.length === 0) return []
  const out: string[] = []
  const cost = mean(list, relativeCost)
  const last = list[list.length - 1]!

  if (last.context > 150_000)
    out.push(`The context is at ${shortTokens(last.context)} tokens, and every step sends it again. When you change subject, use /clear; to keep going on the same one, /compact.`)

  const steps = mean(list, r => r.steps)
  if (steps > 15)
    out.push(`On average, ${steps.toFixed(0)} steps per prompt. Name the exact file, function or command, and what counts as done, so the model does not have to search.`)

  const tools = mean(list, r => r.tools)
  if (tools > 25)
    out.push(`On average, ${tools.toFixed(0)} tool calls per prompt. Point at excerpts (file:line) instead of asking to read whole files or sweep the project.`)

  const written = mean(list, r => r.cacheWritten * WEIGHT.cacheWritten) / (cost || 1)
  if (written > 0.3)
    out.push(`${Math.round(written * 100)}% of the cost is cache written again. Switching model or effort mid-session, and long pauses, invalidate the cache: keep the model and come back within the hour.`)

  const output = mean(list, r => r.output * WEIGHT.output) / (cost || 1)
  if (output > 0.4)
    out.push(`${Math.round(output * 100)}% of the cost is generated text. Ask for short answers, or lower the model's effort on simple tasks.`)

  const sub = mean(list, r => r.subagents) / (mean(list, total) || 1)
  if (sub > 0.3)
    out.push(`${Math.round(sub * 100)}% of the tokens come from subagents. For a simple search, ask to look directly, without delegating.`)

  const shortExpensive = list.filter(r => r.prompt.trim().length < 40 && relativeCost(r) > 1.5 * cost)
  if (shortExpensive.length >= 2)
    out.push(`${shortExpensive.length} short prompts cost over 1.5 times the mean. In a single prompt, say the goal, the context and what counts as done.`)

  return out
}

export function tokensStatusLine(list: readonly PromptRecord[]): string | undefined {
  if (list.length === 0) return undefined
  const last = list[list.length - 1]!
  const alert = list.length >= 5 && relativeCost(last) > 2 * mean(list, relativeCost) ? ' ▲ above the mean, see /concordia-tokens' : ''
  return `tokens: last ${shortTokens(total(last))} · mean (${list.length < WINDOW ? list.length : WINDOW}) ${shortTokens(mean(list, total))}${alert}`
}

/** The /concordia-tokens report: the prompts, the means and the suggestions. */
export function tokensReport(list: readonly PromptRecord[]): string {
  if (list.length === 0) return 'No prompts counted yet: the first shows here when its turn ends.'
  const lines = list.map((r, i) => {
    const excerpt = r.prompt.replace(/\s+/g, ' ').trim().slice(0, 40) || '(no text)'
    return `${String(i + 1).padStart(2)}. ${shortTokens(total(r)).padStart(6)} tokens  ${String(r.steps).padStart(3)} steps  ${String(r.tools).padStart(3)} tools  ${excerpt}`
  })
  const read = mean(list, r => r.cacheRead) / (mean(list, total) || 1)
  const tips = suggestions(list)
  return [
    `The last ${list.length} prompts (the total adds input, cache read, cache written and output, subagents included):`,
    ...lines,
    '',
    `Mean: ${shortTokens(mean(list, total))} tokens per prompt, ${Math.round(read * 100)}% of them read from the cache (they cost 10%).`,
    `Mean output: ${shortTokens(mean(list, r => r.output))}; mean steps: ${mean(list, r => r.steps).toFixed(1)}.`,
    '',
    tips.length > 0 ? 'What to change:' : 'Nothing out of the ordinary in the last prompts.',
    ...tips.map(t => `- ${t}`),
  ].join('\n')
}

export type PartialRecord = Omit<PromptRecord, 'at' | 'prompt' | 'durationMs'>

export const zero = (): PartialRecord => ({
  input: 0,
  cacheRead: 0,
  cacheWritten: 0,
  output: 0,
  subagents: 0,
  steps: 0,
  tools: 0,
  context: 0,
})

export const add = (r: PartialRecord, u: TurnUsage) => {
  r.input += u.input_tokens
  r.cacheRead += u.cache_read_input_tokens
  r.cacheWritten += u.cache_creation_input_tokens
  r.output += u.output_tokens
}

/** What the panel's card shows. */
export function summarizeTokens(list: readonly PromptRecord[]): TokensSummary | null {
  if (list.length === 0) return null
  const last = list[list.length - 1]!
  const tip = suggestions(list)[0]
  return {
    prompts: list.length,
    last: total(last),
    mean: mean(list, total),
    aboveMean: list.length >= 5 && relativeCost(last) > 2 * mean(list, relativeCost),
    context: last.context,
    meanSteps: mean(list, r => r.steps),
    ...(tip ? { tip } : {}),
  }
}
