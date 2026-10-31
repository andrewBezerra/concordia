import { expect, mock, test } from 'claude-code/testing'

import { promptExcerpt, suggestions, summarizeTokens, tokensReport, type PromptRecord } from '../hooks/panel/tokens-stats'

const usage = (input: number, read: number, written: number, output: number) => ({
  input_tokens: input,
  cache_read_input_tokens: read,
  cache_creation_input_tokens: written,
  output_tokens: output,
  model: 'claude-opus-5-5',
})

// A prompt: the turn starts, a subagent runs inside it, and the turn ends.
async function onePrompt($: any, turnId: string, text: string) {
  await $.turn.start({ text, turnId })
  await $.turn.complete({ turnId, agentId: 'sub-1', answer: '', durationMs: 10, isAborted: false, reason: 'answer', usage: usage(100, 0, 0, 50) })
  await $.turn.complete({ turnId, answer: 'ok', durationMs: 900, isAborted: false, reason: 'answer', usage: usage(1_000, 20_000, 2_000, 500) })
}

test('each prompt is counted with its subagents, the report shows the mean, and clear resets it', async ($, on) => {
  const tokens = (args: string) => $.command.run({ command: 'concordia-tokens', args } as Parameters<typeof $.command.run>[0])
  mock.store(on)
  on('turn.start', (_$: any, e: any) => ({ turnId: e.turnId }))
  on('turn.complete', (_$: any, e: any) => ({ text: e.answer, usage: e.usage }))
  on('command.run', () => ({ text: '' }))

  await onePrompt($, 't1', 'fix the reader test')
  await onePrompt($, 't2', 'and the generator one')

  const { text } = await tokens('')
  expect(text).toContain('The last 2 prompts')
  // 1,000 + 20,000 + 2,000 + 500 of the turn, plus 150 of the subagent
  expect(text).toContain('24k tokens')
  expect(text).toContain('fix the reader test')

  const cleared = await tokens('clear')
  expect(cleared.text).toBe('Token history cleared.')
  expect((await tokens('')).text).toContain('No prompts counted')
})

const record = (partial: Partial<PromptRecord>): PromptRecord => ({
  at: '2026-10-22T00:00:00Z',
  prompt: 'implement the feature described in the intent, with tests',
  input: 1_000,
  cacheRead: 50_000,
  cacheWritten: 1_000,
  output: 1_000,
  subagents: 0,
  steps: 5,
  tools: 6,
  context: 60_000,
  durationMs: 1_000,
  ...partial,
})

test('suggestions point at a large context, many steps and subagents, and at nothing in common use', () => {
  expect(suggestions([record({}), record({})])).toEqual([])

  const costly = suggestions([record({ context: 180_000, steps: 30, tools: 40, subagents: 40_000 })])
  expect(costly.some(s => s.includes('/clear'))).toBe(true)
  expect(costly.some(s => s.includes('steps per prompt'))).toBe(true)
  expect(costly.some(s => s.includes('file:line'))).toBe(true)
  expect(costly.some(s => s.includes('subagents'))).toBe(true)

  expect(tokensReport([])).toContain('No prompts counted')
})

test('the card summary: null without prompts, and the last against the mean with the first suggestion', () => {
  expect(summarizeTokens([])).toBeNull()
  const list = [1, 2, 3, 4].map(() => record({})).concat(record({ cacheRead: 900_000, context: 180_000 }))
  const summary = summarizeTokens(list)!
  expect(summary.prompts).toBe(5)
  expect(summary.aboveMean).toBe(true)
  expect(summary.context).toBe(180_000)
  expect(summary.tip).toContain('/clear')
})

test('only a 40-character excerpt of the prompt is stored', async ($, on) => {
  const stored: any = {}
  on('store.get', (): any => ({ value: undefined }))
  on('store.set', (_$: any, e: any): any => ((stored[e.key] = e.value), { value: undefined }))
  on('ui.status', () => ({ value: undefined }))
  on('turn.start', (_$: any, e: any) => ({ turnId: e.turnId }))
  on('turn.complete', (_$: any, e: any) => ({ text: e.answer, usage: e.usage }))
  await onePrompt($, 't1', 'a  secret\n' + 'x'.repeat(100))
  expect(stored['tokens-history'][0].prompt).toBe(('a secret ' + 'x'.repeat(100)).slice(0, 40))
})

test('the excerpt collapses whitespace, and the short prompt rule still tells short from long', () => {
  expect(promptExcerpt('  a \n\t b  ')).toBe('a b')
  const costly = (prompt: string) => record({ prompt: promptExcerpt(prompt), cacheRead: 900_000 })
  const cheap = record({ prompt: 'x'.repeat(40), input: 0, cacheRead: 0, cacheWritten: 0, output: 0 })
  const many = [cheap, cheap, cheap, cheap, cheap, cheap]
  expect(suggestions([...many, costly('short'), costly('also short')]).some(s => s.includes('short prompts'))).toBe(true)
  expect(suggestions([...many, costly('y'.repeat(200)), costly('z'.repeat(200))]).some(s => s.includes('short prompts'))).toBe(false)
})
