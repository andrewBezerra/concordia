import { expect, test } from 'claude-code/testing'

import { parseProgress, slug } from '../hooks/panel/progress'

const VIEW = { component: 'Pane', requestId: 'concordia-agents', props: {} } as const
const SCREEN = { component: 'Pane', requestId: 'concordia-agent', props: {} } as const
const run = ($: any, command: string, args = '') => $.command.run({ command, args })

test('the slug drops accents and turns everything else into dashes', () => {
  expect(slug('Corrigir achados Sonar do Módulo!')).toBe('corrigir-achados-sonar-do-modulo')
})

test('a progress file reads in English or with the old field names, and a bad one counts as none', () => {
  expect(parseProgress('{"done":3,"total":9,"step":"x"}')).toEqual({ done: 3, total: 9, step: 'x' })
  expect(parseProgress('{"feitos":3,"total":9,"etapa":"x"}')).toEqual({ done: 3, total: 9, step: 'x' })
  expect(parseProgress('{"done":3,"total":0}')).toBeUndefined()
  expect(parseProgress('{"total":9}')).toBeUndefined()
})

test('without subagents, the view says there are none', async ($, on) => {
  on('ui.open', () => ({ value: { isPlaced: true } }))
  on('agent.list', () => ({ value: [] }))
  on('env.get', () => ({ value: undefined }))
  on('process.run', () => ({ value: { exitCode: 1, stdout: '', stderr: '' } }))
  await run($, 'concordia-agents')
  const ui = await $.ui.mount({ plugin: 'concordia', surface: 'terminal', ...VIEW })
  expect(await ui.find({ type: 'Text', text: /No subagents in this session/ })).toBeDefined()
  await ui.unmount()
})

test('an agent with a progress file in the new folder shows the real percentage', async ($, on) => {
  on('ui.open', () => ({ value: { isPlaced: true } }))
  on('agent.list', () => ({
    value: [
      { id: 'a1', description: 'Fix the Engine findings', type: 'general-purpose', status: 'running' },
      { id: 'a2', description: 'Fix the Server findings', type: 'general-purpose', status: 'completed' },
    ],
  }))
  on('env.get', () => ({ value: 'C:/Users/test' }))
  on('process.run', () => ({ value: { exitCode: 1, stdout: '', stderr: '' } }))
  on('fs.read', ($, e) =>
    String(e.path).replaceAll('\\', '/').endsWith('/.claude/concordia/agent-progress/fix-the-engine-findings.json')
      ? { value: JSON.stringify({ done: 34, total: 89, step: 'S1192' }) }
      : { deny: 'ENOENT' },
  )
  await run($, 'concordia-agents')
  const ui = await $.ui.mount({ plugin: 'concordia', surface: 'terminal', ...VIEW })
  expect(await ui.find({ type: 'Text', text: /38% \(34 of 89\) · S1192/ })).toBeDefined()
  expect(await ui.find({ type: 'Text', text: /1 of 2 finished/ })).toBeDefined()
  await ui.unmount()
})

test('an agent without a progress file gets no invented percentage', async ($, on) => {
  on('ui.open', () => ({ value: { isPlaced: true } }))
  on('agent.list', () => ({ value: [{ id: 'a1', description: 'Fix the Model', type: 'general-purpose', status: 'running' }] }))
  on('env.get', () => ({ value: 'C:/Users/test' }))
  on('process.run', () => ({ value: { exitCode: 1, stdout: '', stderr: '' } }))
  on('fs.read', () => ({ deny: 'ENOENT' }))
  await run($, 'concordia-agents')
  const ui = await $.ui.mount({ plugin: 'concordia', surface: 'terminal', ...VIEW })
  expect(await ui.find({ type: 'Text', text: /no progress file/ })).toBeDefined()
  expect(await ui.find({ type: 'Text', text: /\d+%/ })).toBeUndefined()
  await ui.unmount()
})

const CONVERSATION = [
  { role: 'user', text: 'Fix the findings', toolUses: [] },
  { role: 'assistant', text: 'Starting with the Engine.', toolUses: [{ tool_use_id: 't1', tool: 'Bash', input: { description: 'Runs the tests', command: 'dotnet test' } }] },
  { role: 'assistant', text: '', toolUses: [{ tool_use_id: 't2', tool: 'Edit', input: { file_path: 'a.cs' }, isError: true }] },
]

test('the agent link opens the screen with what it said and the tools it used', async ($, on) => {
  const opened: unknown[] = []
  on('ui.open', ($, e) => (opened.push(e), { value: { isPlaced: true } }))
  on('ui.panes', () => ({ value: [{ id: 'concordia-agent' }] }))
  on('agent.list', () => ({ value: [{ id: 'a1', description: 'Fix the Engine', type: 'general-purpose', status: 'running' }] }))
  on('env.get', () => ({ value: undefined }))
  on('process.run', () => ({ value: { exitCode: 1, stdout: '', stderr: '' } }))
  on('session.messages', ($, e) => (e.agentId === 'a1' ? { value: CONVERSATION } : { value: { deny: 'other' } }))
  await run($, 'concordia-agents')
  const view = await $.ui.mount({ plugin: 'concordia', surface: 'terminal', ...VIEW })
  await view.press({ key: 'view:a1' })
  await view.unmount()
  expect(opened.at(-1)).toMatchObject({ id: 'concordia-agent', title: 'Agent: Fix the Engine' })
  const screen = await $.ui.mount({ plugin: 'concordia', surface: 'terminal', ...SCREEN })
  expect(await screen.find({ type: 'Text', text: /Starting with the Engine/ })).toBeDefined()
  expect(await screen.find({ type: 'Text', text: /Bash: Runs the tests/ })).toBeDefined()
  expect(await screen.find({ type: 'Text', text: /Edit: a\.cs \(failed\)/ })).toBeDefined()
  await screen.unmount()
})

test('the agent command with an unknown id says so and opens nothing', async ($, on) => {
  const opened: unknown[] = []
  on('ui.open', ($, e) => (opened.push(e), { value: { isPlaced: true } }))
  on('agent.list', () => ({ value: [] }))
  on('env.get', () => ({ value: undefined }))
  const r = await run($, 'concordia-agent', 'nope')
  expect(String(r.text)).toMatch(/No agent nope/)
  expect(opened).toEqual([])
})

test('a conversation the session cannot read says why on the screen', async ($, on) => {
  on('ui.open', () => ({ value: { isPlaced: true } }))
  on('ui.panes', () => ({ value: [{ id: 'concordia-agent' }] }))
  on('agent.list', () => ({ value: [{ id: 'a1', description: 'Fix', type: 'general-purpose', status: 'running' }] }))
  on('env.get', () => ({ value: undefined }))
  on('session.messages', () => ({ value: { deny: 'transcript unavailable' } }))
  await run($, 'concordia-agent', 'a1')
  const screen = await $.ui.mount({ plugin: 'concordia', surface: 'terminal', ...SCREEN })
  expect(await screen.find({ type: 'Text', text: /transcript unavailable/ })).toBeDefined()
  await screen.unmount()
})
