import { describe, expect, test } from 'claude-code/testing'

import { agentsCard, ciCard, decisionsCard, tokensCard } from '../hooks/panel/cards'
import { isMissingFile, parseDecisions } from '../hooks/panel/decisions'

describe('the panel cards', () => {
  const at1405 = new Date(2026, 9, 21, 14, 5).getTime()
  const pr = (o: object) => ({ pr: 65, total: 5, passed: 5, running: 0, failures: [], failureLinks: [], ...o })

  test('CI says in words if it is green, running, failed, or if there is no PR', () => {
    expect(ciCard(pr({}), at1405)).toMatchObject({ tone: 'ok', lines: [{ text: 'PR #65: all green (5 checks).', detail: 'checked at 14:05' }] })
    expect(ciCard(pr({ passed: 3, running: 2 }), at1405).lines[0]?.text).toBe('PR #65: running, 3 of 5 checks passed.')
    expect(ciCard(pr({ failures: ['Build & test'] }), at1405)).toMatchObject({ tone: 'error' })
    expect(ciCard(null, at1405).lines[0]?.text).toBe('No open pull request on this branch.')
    expect(ciCard(null, 0).lines[0]?.text).toBe('Checking CI…')
  })

  test('CI says what is missing when it cannot be read at all', () => {
    expect(ciCard(null, at1405, [], 'The GitHub CLI (gh) is not installed.')).toMatchObject({ tone: 'neutral', lines: [{ text: 'The GitHub CLI (gh) is not installed.' }] })
  })

  test('CI links to each failure first, and to the checks page after', () => {
    const url = 'https://x/pull/65/checks'
    expect(ciCard(pr({ url }), at1405).lines[0]?.links).toEqual([{ label: 'open the PR checks ›', href: url }])
    const failed = ciCard(pr({ url, failures: ['Web'], failureLinks: [{ name: 'Web', url: 'https://x/job/1' }] }), at1405)
    expect(failed.lines[0]?.links).toEqual([
      { label: 'see the failure in Web ›', href: 'https://x/job/1' },
      { label: 'open the PR checks ›', href: url },
    ])
  })

  const time = (project: string, tips: string[] = []) => ({ project, workflow: 'CI', meanMs: 185_000, samples: 18, tips, measuredAt: at1405 })

  test('the current project first with its tips, by name, and up to 3 other means', () => {
    const list = [time('o/a', ['Tip of a.']), time('o/current', ['Tip of the current.']), time('o/b'), time('o/c'), time('o/d')]
    const card = ciCard(pr({}), at1405, list, null, 'o/current')
    expect(card.tone).toBe('ok')
    expect(card.lines.map(l => l.text)).toEqual(['PR #65: all green (5 checks).', 'Mean time of current: 3 min 05 s', 'Mean time of a: 3 min 05 s', 'Mean time of b: 3 min 05 s', 'Mean time of c: 3 min 05 s', 'Tip of the current.'])
    expect(card.lines[1]?.detail).toBe('CI, 18 runs · measured at 14:05')
  })

  test('with no matching project, other means and no tips', () => {
    const texts = ciCard(pr({}), at1405, [time('o/a', ['Tip of a.'])], null, null).lines.map(l => l.text)
    expect(texts).toEqual(['PR #65: all green (5 checks).', 'Mean time of a: 3 min 05 s'])
  })

  test('a kept state shows the failed refresh as its detail', () => {
    expect(ciCard(pr({}), at1405, [], 'gh could not run.').lines[0]).toMatchObject({ text: 'PR #65: all green (5 checks).', detail: 'checked at 14:05 · refresh failed: gh could not run.' })
  })

  test('tokens: the last against the mean, attention above it, and the first suggestion', () => {
    expect(tokensCard(null)).toMatchObject({ tone: 'neutral', lines: [{ text: 'No prompts counted yet.' }] })
    const summary = { prompts: 20, last: 1_400_000, mean: 900_000, aboveMean: false, context: 120_000, meanSteps: 8.25 }
    const usual = tokensCard(summary)
    expect(usual.tone).toBe('ok')
    expect(usual.lines.map(l => l.text)).toEqual(['Last: 1.4M tokens · mean of the last 20: 900k', 'Context: 120k · 8.3 steps per prompt'])
    const costly = tokensCard({ ...summary, aboveMean: true, tip: 'Use /clear.' })
    expect(costly.tone).toBe('attention')
    expect(costly.lines[0]?.detail).toBe('more than twice the mean')
    expect(costly.lines[2]).toMatchObject({ text: 'Use /clear.', detail: 'the other suggestions are in /concordia-tokens' })
  })

  test('agents: the percentage only for those who write the file, and the count of finished', () => {
    expect(agentsCard([], at1405)).toMatchObject({ tone: 'neutral', lines: [{ text: 'No subagents in this session.' }] })
    const card = agentsCard(
      [
        { id: 'a1', description: 'Engine', status: 'running', model: 'opus', startedAt: at1405 - 185_000, progress: { done: 34, total: 89, step: 'S1192' } },
        { id: 'a2', description: 'Server', status: 'completed' },
      ],
      at1405,
    )
    expect(card.lines[2]?.agentId).toBe('a2')
    expect(card.tone).toBe('attention')
    expect(card.lines[0]?.text).toBe('█████░░░░░ 1 of 2 finished')
    expect(card.lines[1]).toMatchObject({ text: 'Engine (opus): working · 3 min 05 s', detail: '████░░░░░░ 38% (34 of 89) · S1192' })
    expect(card.lines[2]).toMatchObject({ tone: 'ok', text: 'Server: finished', detail: 'no progress file' })
    expect(agentsCard([{ id: 'a3', description: 'Model', status: 'failed' }], at1405).tone).toBe('error')
  })
})

describe('the open pull requests on the CI card', () => {
  const at = new Date(2026, 9, 21, 14, 5).getTime()
  const open = (number: number, ci: 'passed' | 'failed' | 'running' | 'none' = 'running') => ({ repo: 'o/shop', number, title: `T${number}`, url: `https://github.com/o/shop/pull/${number}`, ci })

  test('shows them when the branch has no pull request, without turning the card red', () => {
    const card = ciCard(null, at, [], null, null, [open(1, 'failed'), open(2, 'passed')])
    expect(card.tone).toBe('neutral')
    expect(card.lines.map(l => [l.text, l.tone])).toEqual([
      ['No open pull request on this branch.', 'neutral'],
      ['shop #1: failed.', 'error'],
      ['shop #2: all green.', 'ok'],
    ])
    expect(card.lines[1]).toMatchObject({ detail: 'T1', links: [{ href: 'https://github.com/o/shop/pull/1/checks' }] })
  })

  test("does not repeat the branch's own pull request", () => {
    const state = { pr: 1, total: 1, passed: 1, running: 0, failures: [], failureLinks: [], url: 'https://github.com/o/shop/pull/1/checks' }
    expect(ciCard(state, at, [], null, null, [open(1), open(2)]).lines.map(l => l.text)).toEqual(['PR #1: all green (1 check).', 'shop #2: running.'])
  })

  test('shows five and counts the rest', () => {
    const lines = ciCard(null, at, [], null, null, [1, 2, 3, 4, 5, 6, 7].map(n => open(n))).lines
    expect(lines).toHaveLength(7)
    expect(lines[6]?.text).toBe('And 2 more open pull requests.')
  })
})

describe('the pending decisions', () => {
  const file = (...decisions: object[]) => JSON.stringify({ decisions })

  test('what blocks comes first, then what carries a risk, keeping the order of the file', () => {
    const titles = parseDecisions(file({ title: 'plain' }, { title: 'risk', risk: 'r' }, { title: 'block', blocks: 'b' }, { title: 'both', blocks: 'b', risk: 'r' }))
    expect(titles.map(d => d.title)).toEqual(['block', 'both', 'risk', 'plain'])
  })

  test('drops an entry without a title and a link that is not https', () => {
    expect(parseDecisions(file({ url: 'https://x.y' }, { title: 'a', url: 'javascript:alert(1)' }))).toEqual([{ title: 'a' }])
  })

  test('a file in another shape is a failure, and only a missing file means nothing is pending', () => {
    expect(() => parseDecisions('{}')).toThrow()
    expect(() => parseDecisions('not json')).toThrow()
    expect(isMissingFile(new Error('ENOENT: no such file'))).toBe(true)
    expect(isMissingFile(new Error('EACCES'))).toBe(false)
  })

  test('the card marks what blocks and what carries a risk, with the facts and the link', () => {
    const card = decisionsCard(parseDecisions(file({ title: 'plain' }, { title: 'r', risk: 'may leak' }, { title: 'b', blocks: 'the store', risk: 'late', url: 'https://x.y/1' })), null)
    expect(card.tone).toBe('error')
    expect(card.lines.map(l => [l.text, l.tone])).toEqual([['BLOCKING + RISK: b', 'error'], ['RISK: r', 'attention'], ['plain', 'neutral']])
    expect(card.lines[0]).toMatchObject({ detail: 'Blocks: the store · Risk: late', links: [{ href: 'https://x.y/1' }] })
    expect(card.lines[2]?.detail).toBeUndefined()
  })

  test('the card with nothing pending, with only risks, and with a problem', () => {
    expect(decisionsCard([], null)).toMatchObject({ tone: 'ok', lines: [{ text: 'No pending decisions.' }] })
    expect(decisionsCard([{ title: 'r', risk: 'x' }], null).tone).toBe('attention')
    expect(decisionsCard([{ title: 'kept' }], 'unreadable').lines[0]?.text).toBe('unreadable')
  })
})
