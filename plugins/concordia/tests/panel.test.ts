import { expect, mock, test } from 'claude-code/testing'

import { composeStatus } from '../hooks/panel/status'

const PANEL = { component: 'Pane', requestId: 'concordia-panel', props: {} } as const
const run = ($: any, command: string, args = '') => $.command.run({ command, args })

// A session start with the clock in hand: nothing opens until the timers fire.
async function start($: any, on: any, store: Record<string, unknown> = {}, placed: object = { isPlaced: true }) {
  const clock = mock.clock(on)
  mock.store(on, store)
  const opened: any[] = []
  const toasts: string[] = []
  const statuses: (string | undefined)[] = []
  on('ui.open', (_$: any, e: any) => (opened.push(e), { value: placed }))
  on('ui.toast', (_$: any, e: any) => (toasts.push(e.text), { value: undefined }))
  on('ui.status', (_$: any, e: any) => (statuses.push(e.text), { value: undefined }))
  on('process.run', () => ({ value: { exitCode: 1, stdout: '', stderr: 'no pull requests found' } }))
  on('agent.list', () => ({ value: [] }))
  on('env.get', () => ({ value: undefined }))
  on('command.register', (_$: any, e: any) => ({ value: { command: e.name } }))
  on('session.start', () => ({ cwd: '.' }))
  await $.session.start({ cwd: '.' })
  return { clock, opened, toasts, statuses }
}

test('the status line joins CI and tokens, with either one alone or neither', () => {
  expect(composeStatus('PR #1 · CI green ✓', 'tokens: last 3k')).toBe('PR #1 · CI green ✓ | tokens: last 3k')
  expect(composeStatus('PR #1 · CI green ✓', null)).toBe('PR #1 · CI green ✓')
  expect(composeStatus(null, 'tokens: last 3k')).toBe('tokens: last 3k')
  expect(composeStatus(null, undefined)).toBeUndefined()
})

test('CI and tokens do not overwrite each other in the one status line', async ($, on) => {
  mock.store(on, {
    'tokens-history': [{ at: '', prompt: 'p', input: 3_000, cacheRead: 0, cacheWritten: 0, output: 0, subagents: 0, steps: 1, tools: 0, context: 0, durationMs: 1 }],
  })
  const statuses: (string | undefined)[] = []
  on('ui.status', (_$: any, e: any) => (statuses.push(e.text), { value: undefined }))
  on('ui.toast', () => ({ value: undefined }))
  on('process.run', (_$: any, e: any) =>
    e.argv.includes('checks') ? { value: { exitCode: 0, stdout: 'ok', stderr: '' } } : { value: { exitCode: 0, stdout: JSON.stringify({ number: 7, statusCheckRollup: [] }), stderr: '' } },
  )
  on('turn.start', (_$: any, e: any) => ({ turnId: e.turnId }))
  on('turn.complete', (_$: any, e: any) => ({ text: e.answer, usage: e.usage }))
  on('command.run', () => ({ text: '' }))
  await $.turn.start({ text: 'p', turnId: 't' })
  await $.turn.complete({ turnId: 't', answer: 'ok', durationMs: 1, isAborted: false, reason: 'answer', usage: { input_tokens: 3_000, cache_read_input_tokens: 0, cache_creation_input_tokens: 0, output_tokens: 0, model: 'm' } })
  await run($, 'concordia-ci')
  const last = statuses.at(-1)
  expect(last).toContain('PR #7 · no CI')
  expect(last).toContain('tokens: last 3k')
})

test('the panel opens on a timer, not while the session starts', async ($, on) => {
  const { clock, opened } = await start($, on)
  expect(opened).toEqual([])
  await clock.advance(10)
  expect(opened.map(o => o.id)).toContain('concordia-panel')
})

// The test kit's $ cannot raise ui.close, so the close itself is checked on a real terminal, not here.
test('once closed by the person, it stays closed in later sessions, and the command clears that', async ($, on) => {
  const { clock, opened } = await start($, on, { 'panel-closed-by-user': true })
  await clock.advance(10)
  expect(opened).toEqual([])

  const answer = await run($, 'concordia-panel')
  expect(answer.text).toBe('Concordia panel opened.')
  expect(opened.map(o => o.id)).toEqual(['concordia-panel'])

  opened.length = 0
  await $.session.start({ cwd: '.' })
  await clock.advance(10)
  expect(opened.map(o => o.id)).toEqual(['concordia-panel'])
})

test('a pane that could not be placed shows one toast, once per session', async ($, on) => {
  const { clock, toasts } = await start($, on, {}, { isPlaced: false, reason: 'needs 144 columns, has 100' })
  await clock.advance(10)
  expect(toasts).toEqual(['The Concordia panel needs a wider terminal. Type /concordia-panel to open it now.'])
  await $.session.start({ cwd: '.' })
  await clock.advance(10)
  expect(toasts.length).toBe(1)
})

for (const surface of ['terminal', 'desktop'] as const) {
  test(`the panel draws its three cards and the new command names on ${surface}`, async ($, on) => {
    on('env.get', () => ({ value: undefined }))
    const ui = await $.ui.mount({ plugin: 'concordia', surface, ...PANEL })
    for (const title of [/Agents/, /Pull request CI/, /Tokens per prompt/, /\/concordia-panel reopens/]) expect(await ui.find({ type: 'Text', text: title })).toBeDefined()
    await ui.unmount()
  })
}

test('without gh, the CI card says what is missing and the rest still draws', async ($, on) => {
  mock.store(on)
  on('process.run', () => ({ deny: "failed to start: ENOENT: Command 'gh' not found" }))
  on('ui.status', () => ({ value: undefined }))
  on('command.run', () => ({ text: '' }))
  const answer = await run($, 'concordia-ci')
  expect(answer.text).toContain('not installed')
  const ui = await $.ui.mount({ plugin: 'concordia', surface: 'terminal', ...PANEL })
  expect(await ui.find({ type: 'Text', text: /not installed or not on PATH/ })).toBeDefined()
  expect(await ui.find({ type: 'Text', text: /No prompts counted yet/ })).toBeDefined()
  await ui.unmount()
})

test('outside a repository, the CI card says so in plain words', async ($, on) => {
  mock.store(on)
  on('process.run', () => ({ value: { exitCode: 1, stdout: '', stderr: 'failed to run git: fatal: not a git repository' } }))
  on('ui.status', () => ({ value: undefined }))
  await run($, 'concordia-ci')
  const ui = await $.ui.mount({ plugin: 'concordia', surface: 'terminal', ...PANEL })
  expect(await ui.find({ type: 'Text', text: /not a git repository/ })).toBeDefined()
  await ui.unmount()
})
