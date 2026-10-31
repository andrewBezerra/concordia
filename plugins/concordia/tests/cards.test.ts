import { describe, expect, test } from 'claude-code/testing'

import { agentsCard, ciCard, tokensCard } from '../hooks/panel/cards'

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

  test('the mean time of each project, and the tips of the current one only', () => {
    const time = (project: string, tips: string[]) => ({ project, workflow: 'CI', meanMs: 185_000, samples: 18, tips, measuredAt: at1405 })
    const card = ciCard(pr({}), at1405, [time('o/current', ['Tip of the current.']), time('o/other', ['Tip of the other.'])])
    expect(card.tone).toBe('ok')
    expect(card.lines.map(l => l.text)).toEqual(['PR #65: all green (5 checks).', 'Mean time of current: 3 min 05 s', 'Mean time of other: 3 min 05 s', 'Tip of the current.'])
    expect(card.lines[1]?.detail).toBe('CI, 18 runs · measured at 14:05')
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
