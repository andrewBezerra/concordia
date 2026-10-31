import type { CiState } from '../../types'

// The CI of a pull request, from the statusCheckRollup of `gh pr view --json`: GitHub Actions checks (CheckRun: status
// and conclusion) and commit statuses (StatusContext: state).

export type Check = {
  name?: string
  context?: string
  status?: string
  conclusion?: string
  state?: string
  detailsUrl?: string
  targetUrl?: string
}

const OK = new Set(['SUCCESS', 'SKIPPED', 'NEUTRAL'])

// These URLs come from whatever posted the status, and they become links on the screen.
const isHttps = (url: string) => {
  try {
    return new URL(url).protocol === 'https:'
  } catch {
    return false
  }
}

/** Sums the checks up: how many passed, how many run, and the name and link of those that failed. */
export function summarize(pr: number, checks: readonly Check[], prUrl?: string): CiState {
  const state: CiState = { pr, total: checks.length, passed: 0, running: 0, failures: [], failureLinks: [], ...(prUrl && isHttps(prUrl) ? { url: `${prUrl}/checks` } : {}) }
  for (const c of checks) {
    const name = c.name ?? c.context ?? '?'
    const end = c.conclusion ?? (c.state === 'PENDING' || c.state === 'EXPECTED' ? undefined : c.state)
    const ended = c.status === undefined ? end !== undefined : c.status === 'COMPLETED'
    if (!ended || !end) state.running++
    else if (OK.has(end)) state.passed++
    else {
      state.failures.push(name)
      const url = c.detailsUrl ?? c.targetUrl
      if (url && isHttps(url)) state.failureLinks.push({ name, url })
    }
  }
  return state
}

export const isFinished = (s: CiState) => s.total > 0 && s.running === 0

const BACKOFF_MS = 5 * 60_000

/** With a problem, or CI finished, nothing is likely to change soon: gh is asked every 5 minutes instead of every 30 s. */
export function ciPollIsDue(state: CiState | null, problem: string | null, lastAttemptAt: number, now: number): boolean {
  const isQuiet = problem !== null || (state !== null && isFinished(state))
  return !isQuiet || now - lastAttemptAt >= BACKOFF_MS
}

/** The status line: `PR #65 · CI 3/5 ✓`, `PR #65 · CI green ✓`, `PR #65 · CI failed ✗ Build & test`. */
export function statusLine(s: CiState): string {
  if (s.total === 0) return `PR #${s.pr} · no CI`
  if (s.failures.length > 0) return `PR #${s.pr} · CI failed ✗ ${s.failures.join(', ')}${s.running > 0 ? ` (${s.running} running)` : ''}`
  if (s.running > 0) return `PR #${s.pr} · CI ${s.passed}/${s.total} ✓`
  return `PR #${s.pr} · CI green ✓`
}

/** Why gh could not start, in plain words. The engine rejects with `failed to start: ENOENT` when it is missing. */
export function explainGhStartFailure(error: unknown): string {
  return /ENOENT|not found/i.test(String(error))
    ? 'The GitHub CLI (gh) is not installed or not on PATH. Install it from https://cli.github.com to see CI here.'
    : 'gh could not run, so CI cannot be read here.'
}

/** What a failed `gh pr view` means: `null` is simply no pull request on this branch; a string is what is missing. */
export function explainGhFailure(stderr: string): string | null {
  if (/no (open )?pull requests? found/i.test(stderr)) return null
  if (/not a git repository/i.test(stderr)) return 'This folder is not a git repository, so there is no pull request to follow.'
  if (/gh auth login|not logged in/i.test(stderr)) return 'gh is not logged in. Run gh auth login to see CI here.'
  if (/no git remotes|none of the git remotes|could not determine/i.test(stderr)) return 'This repository has no GitHub remote, so there is no pull request to follow.'
  const first = stderr.trim().split('\n')[0]?.slice(0, 120)
  return first ? `gh could not read the pull request: ${first}` : 'gh could not read the pull request.'
}
