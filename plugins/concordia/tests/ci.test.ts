import { describe, expect, test } from 'claude-code/testing'

import { ciPollIsDue, explainGhFailure, explainGhStartFailure, isFinished, statusLine, summarize } from '../hooks/panel/ci-state'
import { mainWorkflow, measure, needsMeasuring, remember, type Run, type RunWithJobs } from '../hooks/panel/ci-times'

describe('the CI summary', () => {
  test('counts the passed and the running, and says how many passed', () => {
    const s = summarize(65, [
      { name: 'Build & test', status: 'IN_PROGRESS' },
      { name: 'Web', status: 'COMPLETED', conclusion: 'SUCCESS' },
      { context: 'sonar', state: 'PENDING' },
    ])
    expect(isFinished(s)).toBe(false)
    expect(statusLine(s)).toBe('PR #65 · CI 1/3 ✓')
  })

  test('goes green when everything passed, skipped or neutral counting as passed', () => {
    const s = summarize(65, [
      { name: 'Build & test', status: 'COMPLETED', conclusion: 'SUCCESS' },
      { name: 'evals', status: 'COMPLETED', conclusion: 'SKIPPED' },
      { context: 'sonar', state: 'SUCCESS' },
    ])
    expect(isFinished(s)).toBe(true)
    expect(statusLine(s)).toBe('PR #65 · CI green ✓')
  })

  test('says what failed, even with others still running', () => {
    const s = summarize(65, [
      { name: 'Build & test', status: 'COMPLETED', conclusion: 'FAILURE' },
      { name: 'E2E', status: 'QUEUED' },
    ])
    expect(statusLine(s)).toBe('PR #65 · CI failed ✗ Build & test (1 running)')
  })

  test('keeps the checks page and the link of each failure', () => {
    const s = summarize(
      65,
      [
        { name: 'Build & test', status: 'COMPLETED', conclusion: 'FAILURE', detailsUrl: 'https://x/job/1' },
        { context: 'sonar', state: 'FAILURE', targetUrl: 'https://x/sonar' },
        { name: 'Web', status: 'COMPLETED', conclusion: 'SUCCESS', detailsUrl: 'https://x/job/2' },
        { name: 'No link', status: 'COMPLETED', conclusion: 'FAILURE' },
      ],
      'https://x/pull/65',
    )
    expect(s.url).toBe('https://x/pull/65/checks')
    expect(s.failures).toEqual(['Build & test', 'sonar', 'No link'])
    expect(s.failureLinks).toEqual([
      { name: 'Build & test', url: 'https://x/job/1' },
      { name: 'sonar', url: 'https://x/sonar' },
    ])
  })

  test('a PR without checks is not finished', () => {
    expect(isFinished(summarize(7, []))).toBe(false)
    expect(statusLine(summarize(7, []))).toBe('PR #7 · no CI')
  })
})

describe('what gh failing means', () => {
  test('the words the engine and gh really produced read as what is missing', () => {
    expect(explainGhStartFailure(new Error("$.process.run(gh) failed to start: ENOENT: Command 'gh' not found"))).toContain('not installed')
    expect(explainGhFailure('failed to run git: fatal: not a git repository (or any of the parent directories): .git')).toContain('not a git repository')
    expect(explainGhFailure('To get started with GitHub CLI, please run:  gh auth login')).toContain('gh auth login')
    expect(explainGhFailure('no git remotes found')).toContain('no GitHub remote')
    expect(explainGhFailure('no pull requests found for branch "x"')).toBeNull()
    expect(explainGhFailure('')).toBe('gh could not read the pull request.')
  })
})

const at = (m: number) => new Date(Date.UTC(2026, 9, 31, 10, m)).toISOString()
const run = (workflowName: string, minutes: number, o: Partial<Run> = {}): Run => ({
  databaseId: 1,
  workflowName,
  startedAt: at(0),
  updatedAt: at(minutes),
  conclusion: 'success',
  ...o,
})
const detailed = (jobs: RunWithJobs['jobs']): RunWithJobs => ({ startedAt: at(0), jobs })

describe('the mean CI time', () => {
  test('the slowest workflow is the main one, with only the first attempt of those that passed', () => {
    const main = mainWorkflow([run('CI', 20), run('CI', 30), run('CI', 2, { conclusion: 'failure' }), run('CI', 50, { attempt: 2 }), run('evals', 1)])
    expect(main).toEqual({ workflow: 'CI', meanMs: 25 * 60_000, samples: 2 })
    expect(mainWorkflow([run('CI', 2, { conclusion: 'failure' })])).toBeUndefined()
  })

  test('nothing slow, no tip', () => {
    const job = { name: 'Web', startedAt: at(0), completedAt: at(1), steps: [{ name: 'Tests', startedAt: at(0), completedAt: at(1) }] }
    expect(measure('o/r', [run('CI', 1)], [detailed([job])], 5)).toMatchObject({ project: 'o/r', tips: [], measuredAt: 5 })
  })

  test('a job that waits for another becomes a tip, with the gain of running together', () => {
    const jobs = [
      { name: 'Build', startedAt: at(0), completedAt: at(10) },
      { name: 'E2E', startedAt: at(10), completedAt: at(18) },
    ]
    expect(measure('o/r', [run('CI', 18)], [detailed(jobs)], 0)?.tips).toEqual(['E2E only starts after Build, because it depends on it or no runner was free: running them together saves up to 8 min.'])
  })

  test('the slowest step becomes a tip by its name; a skipped step, without times, does not count', () => {
    const jobs = [{ name: 'Build', startedAt: at(0), completedAt: at(10), steps: [{ name: 'Run the tests', startedAt: at(1), completedAt: at(9) }, { name: 'Skipped' }] }]
    expect(measure('o/r', [run('CI', 10)], [detailed(jobs)], 0)?.tips).toEqual(['Step "Run the tests", in Build, takes 8 min, 80% of the run: see if it can run in parallel, or only what the change affects.'])
  })

  test('failures become a tip from 20%, with at least 5 runs', () => {
    const ok = [1, 2, 3, 4].map(() => run('CI', 1))
    const failed = run('CI', 1, { conclusion: 'failure' })
    expect(measure('o/r', [...ok, failed], [], 0)?.tips).toEqual(['1 of 5 runs failed: each costs another whole run, so it pays to catch the most common failure before pushing.'])
    expect(measure('o/r', [ok[0]!, failed], [], 0)?.tips).toEqual([])
  })

  test('waiting for a runner becomes a tip from 1 minute', () => {
    const jobs = [{ name: 'Web', startedAt: at(3), completedAt: at(4) }]
    expect(measure('o/r', [run('CI', 4)], [detailed(jobs)], 0)?.tips).toEqual(['The run waits 3 min for a runner before it starts: see if runners are busy or too few.'])
  })

  test('remember replaces the same project and puts the current one first', () => {
    const t = (project: string, meanMs: number) => ({ project, workflow: 'CI', meanMs, samples: 1, tips: [], measuredAt: 0 })
    expect(remember([t('a', 1), t('b', 2)], t('b', 3))).toEqual([t('b', 3), t('a', 1)])
  })
})

test('a failure link is kept only over https', () => {
  const s = summarize(
    1,
    [
      { name: 'a', status: 'COMPLETED', conclusion: 'FAILURE', detailsUrl: 'javascript:alert(1)' },
      { name: 'b', status: 'COMPLETED', conclusion: 'FAILURE', detailsUrl: 'http://x/1' },
      { name: 'c', status: 'COMPLETED', conclusion: 'FAILURE', detailsUrl: 'not a url' },
      { name: 'd', status: 'COMPLETED', conclusion: 'FAILURE', detailsUrl: 'https://x/2' },
    ],
    'file:///etc/passwd',
  )
  expect(s.failures).toEqual(['a', 'b', 'c', 'd'])
  expect(s.failureLinks).toEqual([{ name: 'd', url: 'https://x/2' }])
  expect(s.url).toBeUndefined()
})

test('polling is due every time while CI runs, and after 5 minutes with a problem or once finished', () => {
  const running = summarize(1, [{ name: 'a', status: 'IN_PROGRESS' }])
  const finished = summarize(1, [{ name: 'a', status: 'COMPLETED', conclusion: 'SUCCESS' }])
  expect(ciPollIsDue(running, null, 1_000, 1_000)).toBe(true)
  expect(ciPollIsDue(null, null, 1_000, 1_000)).toBe(true)
  expect(ciPollIsDue(finished, null, 0, 299_999)).toBe(false)
  expect(ciPollIsDue(finished, null, 0, 300_000)).toBe(true)
  expect(ciPollIsDue(null, 'no gh', 0, 299_999)).toBe(false)
  expect(ciPollIsDue(running, 'no gh', 0, 300_000)).toBe(true)
})

test('a project is measured when missing or older than the period', () => {
  const t = { project: 'o/r', workflow: 'CI', meanMs: 1, samples: 1, tips: [], measuredAt: 1_000 }
  expect(needsMeasuring([t], 'o/r', 1_999, 1_000)).toBe(false)
  expect(needsMeasuring([t], 'o/r', 2_000, 1_000)).toBe(true)
  expect(needsMeasuring([t], 'o/other', 1_001, 1_000)).toBe(true)
})
