import { expect, mock, test } from 'claude-code/testing'

import { exclusive } from '../hooks/panel/safely'
import { composeStatus } from '../hooks/panel/status'

const PANEL = { component: 'Pane', requestId: 'concordia-panel', props: {} } as any
const INTERACTIVE = { cwd: '.', surface: 'terminal', isInteractive: true } as const
const run = ($: any, command: string, args = '') => $.command.run({ command, args })

// A session start with the clock in hand: nothing opens until the timers fire.
async function start($: any, on: any, store: Record<string, unknown> = {}, placed: object = { isPlaced: true }, session: object = INTERACTIVE) {
  const clock = mock.clock(on)
  mock.store(on, store)
  const opened: any[] = []
  const toasts: string[] = []
  const statuses: (string | undefined)[] = []
  on('ui.open', (_$: any, e: any) => (opened.push(e), { value: placed }))
  on('ui.toast', (_$: any, e: any) => (toasts.push(e.text), { value: undefined }))
  on('ui.status', (_$: any, e: any) => (statuses.push(e.text), { value: undefined }))
  const gh: string[][] = []
  on('process.run', (_$: any, e: any) => (gh.push(e.argv), { value: { exitCode: 1, stdout: '', stderr: 'no pull requests found' } }))
  on('ui.panes', () => ({ value: [] }))
  on('agent.list', () => ({ value: [] }))
  on('env.get', () => ({ value: undefined }))
  on('command.register', (_$: any, e: any) => ({ value: { command: e.name } }))
  on('session.start', () => ({ cwd: '.' }))
  await $.session.start(session)
  return { clock, opened, toasts, statuses, gh }
}

test('the status line joins CI and tokens, with either one alone or neither', () => {
  expect(composeStatus('PR #1 · CI green ✓', 'tokens: last 3k')).toBe('PR #1 · CI green ✓ | tokens: last 3k')
  expect(composeStatus('PR #1 · CI green ✓', null)).toBe('PR #1 · CI green ✓')
  expect(composeStatus(null, 'tokens: last 3k')).toBe('tokens: last 3k')
  expect(composeStatus(null, undefined)).toBeUndefined()
})

test('CI and tokens do not overwrite each other in the one status line', async ($, on) => {
  mock.clock(on)
  mock.store(on, {
    'tokens-history': [{ at: '', prompt: 'p', input: 3_000, cacheRead: 0, cacheWritten: 0, output: 0, subagents: 0, steps: 1, tools: 0, context: 0, durationMs: 1 }],
  })
  const statuses: (string | undefined)[] = []
  on('ui.status', (_$: any, e: any) => (statuses.push(e.text), { value: undefined }))
  on('ui.toast', () => ({ value: undefined }))
  on('process.run', (_$: any, e: any): any =>
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
  await $.session.start(INTERACTIVE)
  await clock.advance(10)
  expect(opened.map(o => o.id)).toEqual(['concordia-panel'])
})

test('a pane that could not be placed shows one toast, once per session', async ($, on) => {
  const { clock, toasts } = await start($, on, {}, { isPlaced: false, reason: 'needs 144 columns, has 100' })
  await clock.advance(10)
  expect(toasts).toEqual(['The Concordia panel needs a wider terminal. Type /concordia-panel to open it now.'])
  await $.session.start(INTERACTIVE)
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
  mock.clock(on)
  mock.store(on)
  on('process.run', (): any => ({ deny: "failed to start: ENOENT: Command 'gh' not found" }))
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
  mock.clock(on)
  mock.store(on)
  on('process.run', (): any => ({ value: { exitCode: 1, stdout: '', stderr: 'failed to run git: fatal: not a git repository' } }))
  on('ui.status', () => ({ value: undefined }))
  await run($, 'concordia-ci')
  const ui = await $.ui.mount({ plugin: 'concordia', surface: 'terminal', ...PANEL })
  expect(await ui.find({ type: 'Text', text: /not a git repository/ })).toBeDefined()
  await ui.unmount()
})

test('a headless session gets the commands, and no timers, gh calls or panel', async ($, on) => {
  const { clock, opened, gh, statuses } = await start($, on, {}, { isPlaced: true }, { cwd: '.', surface: null, isInteractive: false })
  await clock.advance(120_000)
  expect(opened).toEqual([])
  expect(gh).toEqual([])
  expect(statuses).toEqual([])
  expect((await run($, 'concordia-tokens')).text).toContain('No prompts counted')
})

test('a failed look keeps the last state and shows the problem; a definite no-PR clears it', async ($, on) => {
  mock.clock(on)
  mock.store(on)
  const statuses: unknown[] = []
  on('ui.status', (_$: any, e: any) => (statuses.push(e.text), { value: undefined }))
  let answer: any = { exitCode: 0, stderr: '', stdout: JSON.stringify({ number: 5, url: 'https://x/pull/5', statusCheckRollup: [{ name: 'b', status: 'IN_PROGRESS' }] }) }
  on('process.run', (): any => ({ value: answer }))
  await run($, 'concordia-ci')
  answer = { exitCode: 1, stdout: '', stderr: 'error connecting to api.github.com' }
  await run($, 'concordia-ci')
  let ui = await $.ui.mount({ plugin: 'concordia', surface: 'terminal', ...PANEL })
  expect(await ui.find({ type: 'Text', text: /PR #5: running/ })).toBeDefined()
  expect(await ui.find({ type: 'Text', text: /refresh failed: gh could not read the pull request: error connecting/ })).toBeDefined()
  await ui.unmount()
  expect(String(statuses.at(-1))).toContain('PR #5')

  answer = { exitCode: 1, stdout: '', stderr: 'no pull requests found for branch "x"' }
  await run($, 'concordia-ci')
  ui = await $.ui.mount({ plugin: 'concordia', surface: 'terminal', ...PANEL })
  expect(await ui.find({ type: 'Text', text: /No open pull request/ })).toBeDefined()
  expect(await ui.find({ type: 'Text', text: /PR #5/ })).toBeUndefined()
  await ui.unmount()
})

// One session whose gh answers by command; the clock starts at a real-looking time.
async function polled($: any, on: any, store: Record<string, unknown>, answer: (argv: string[]) => unknown) {
  const clock = mock.clock(on, { now: 1_000_000_000 })
  mock.store(on, store)
  const calls: string[] = []
  on('process.run', (_$: any, e: any): any => {
    calls.push(e.argv.slice(1, 3).join(' '))
    return { value: answer(e.argv) }
  })
  on('ui.panes', () => ({ value: [] }))
  on('ui.open', () => ({ value: { isPlaced: true } }))
  on('ui.status', () => ({ value: undefined }))
  on('ui.toast', () => ({ value: undefined }))
  on('command.register', (_$: any, e: any) => ({ value: { command: e.name } }))
  on('session.start', () => ({ cwd: '.' }))
  await $.session.start(INTERACTIVE)
  return { clock, calls }
}

test('polling goes on every 30 s while CI runs, and backs off once it finished', async ($, on) => {
  let status = 'IN_PROGRESS'
  const { clock, calls } = await polled($, on, {}, argv =>
    argv.includes('pr')
      ? { exitCode: 0, stderr: '', stdout: JSON.stringify({ number: 1, statusCheckRollup: [{ name: 'b', status, conclusion: status === 'COMPLETED' ? 'SUCCESS' : undefined }] }) }
      : { exitCode: 1, stdout: '', stderr: 'x' },
  )
  const looks = () => calls.filter(c => c === 'pr view').length
  await clock.advance(10)
  await clock.advance(30_000)
  expect(looks()).toBe(2)
  status = 'COMPLETED'
  await clock.advance(30_000)
  expect(looks()).toBe(3)
  await clock.advance(60_000)
  expect(looks()).toBe(3)
  await clock.advance(300_000)
  expect(looks()).toBeGreaterThan(3)
})

test('a measure newer than the period is not taken again', async ($, on) => {
  const kept = { project: 'o/r', workflow: 'CI', meanMs: 60_000, samples: 1, tips: ['Tip.'], measuredAt: 1_000_000_000 - 60_000 }
  const { clock, calls } = await polled($, on, { 'ci-times': [kept] }, argv =>
    argv.includes('repo') ? { exitCode: 0, stderr: '', stdout: '{"nameWithOwner":"o/r"}' } : { exitCode: 1, stdout: '', stderr: 'no pull requests found' },
  )
  await clock.advance(10)
  expect(calls).toContain('repo view')
  expect(calls).not.toContain('run list')
})

test('the agents refresh skips the files with no pane showing agents, and counts the main panel as one', async ($, on) => {
  const clock = mock.clock(on)
  mock.store(on)
  let panes: { id: string }[] = []
  const stats: string[] = []
  on('ui.panes', (): any => ({ value: panes }))
  on('ui.open', () => ({ value: { isPlaced: true } }))
  on('ui.status', () => ({ value: undefined }))
  on('process.run', (): any => ({ value: { exitCode: 1, stdout: '', stderr: 'no pull requests found' } }))
  on('agent.list', (): any => ({ value: [{ id: 'a1', description: 'Fix it', type: 'general-purpose', status: 'running' }] }))
  on('agent.spawn', (): any => ({ model: 'm', agentId: 'a1' }))
  on('env.get', () => ({ value: 'C:/Users/test' }))
  on('fs.stat', (_$: any, e: any): any => (stats.push(e.path), { deny: 'ENOENT' }))
  on('command.register', (_$: any, e: any) => ({ value: { command: e.name } }))
  on('session.start', () => ({ cwd: '.' }))
  await $.session.start(INTERACTIVE)
  await ($ as any).agent.spawn({ subagentType: 'general-purpose', description: 'Fix it', prompt: 'x' })
  await clock.advance(2_000)
  expect(stats).toEqual([])
  panes = [{ id: 'concordia-panel' }]
  await clock.advance(2_000)
  expect(stats.length).toBeGreaterThan(0)
})

test('a finished agent gets no file read', async ($, on) => {
  const stats: string[] = []
  mock.store(on)
  on('ui.open', () => ({ value: { isPlaced: true } }))
  on('agent.list', (): any => ({ value: [{ id: 'a1', description: 'Fix it', type: 'general-purpose', status: 'completed' }] }))
  on('agent.spawn', (): any => ({ model: 'm', agentId: 'a1' }))
  on('env.get', () => ({ value: 'C:/Users/test' }))
  on('fs.stat', (_$: any, e: any): any => (stats.push(e.path), { deny: 'ENOENT' }))
  on('process.run', (): any => ({ value: { exitCode: 1, stdout: '', stderr: '' } }))
  await ($ as any).agent.spawn({ subagentType: 'general-purpose', description: 'Fix it', prompt: 'x' })
  await run($, 'concordia-panel')
  expect(stats).toEqual([])
})

test('a periodic job skips its turn while the last one still runs', async () => {
  let runs = 0
  let finish = () => {}
  const job = exclusive(() => {
    runs++
    return new Promise<void>(resolve => (finish = resolve))
  })
  const first = job()
  await job()
  expect(runs).toBe(1)
  finish()
  await first
  const second = job()
  expect(runs).toBe(2)
  finish()
  await second
})
