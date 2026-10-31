import type { ProjectCiTime } from '../../types'

// A project's mean CI time, and tips drawn from that measure: only what the numbers of the runs show, no generic advice.
// The inputs are those of `gh run list` and `gh run view --json jobs`.

export type Run = { databaseId: number; workflowName: string; startedAt: string; updatedAt: string; conclusion: string; attempt?: number }

export type Step = { name: string; startedAt?: string; completedAt?: string }

export type Job = { name: string; startedAt?: string; completedAt?: string; steps?: Step[] }

export type RunWithJobs = { startedAt: string; jobs: Job[] }

const MINUTE = 60_000

const between = (from?: string, to?: string): number | undefined => {
  if (!from || !to) return undefined
  const d = Date.parse(to) - Date.parse(from)
  return Number.isFinite(d) && d >= 0 ? d : undefined
}

const mean = (values: readonly number[]) => values.reduce((a, b) => a + b, 0) / values.length

export const shortTime = (t: number) => (t < MINUTE ? `${Math.round(t / 1000)} s` : `${Math.round(t / MINUTE)} min`)

// The first attempt of those that passed: a failure stops midway, and a re-run moves updatedAt without measuring anything.
const passed = (r: Run) => r.conclusion === 'success' && (r.attempt ?? 1) === 1

const duration = (r: Run) => between(r.startedAt, r.updatedAt)

/** The slowest workflow, which is what holds the PR, with the mean of the runs that passed. */
export function mainWorkflow(runs: readonly Run[]): { workflow: string; meanMs: number; samples: number } | undefined {
  const byWorkflow = new Map<string, number[]>()
  for (const r of runs) {
    const d = duration(r)
    if (!passed(r) || d === undefined) continue
    byWorkflow.set(r.workflowName, [...(byWorkflow.get(r.workflowName) ?? []), d])
  }
  let best: { workflow: string; meanMs: number; samples: number } | undefined
  for (const [workflow, values] of byWorkflow) {
    const meanMs = mean(values)
    if (!best || meanMs > best.meanMs) best = { workflow, meanMs, samples: values.length }
  }
  return best
}

/** What to do about a slow step, by its name. */
function stepSuggestion(name: string): string {
  if (/restor|install|npm ci|depend|setup|cache/i.test(name)) return 'check that the dependency cache is being used'
  if (/test|e2e/i.test(name)) return 'see if it can run in parallel, or only what the change affects'
  if (/sonar|analy[sz]|scan/i.test(name)) return 'see if the PR can analyze only the changed files'
  if (/build|compil|publish/i.test(name)) return 'see if it can reuse the build of an earlier job'
  return 'see what in it can be cached or reused'
}

/** The mean per name across runs: a job that did not run in one run does not count for that one. */
function meansByName(items: ReadonlyArray<{ name: string; ms: number }>): Array<{ name: string; ms: number }> {
  const groups = new Map<string, number[]>()
  for (const i of items) groups.set(i.name, [...(groups.get(i.name) ?? []), i.ms])
  return [...groups].map(([name, values]) => ({ name, ms: mean(values) }))
}

function queueTip(runs: readonly RunWithJobs[]): string | undefined {
  const waits = runs.flatMap(r => {
    const starts = r.jobs.flatMap(j => (j.startedAt ? [Date.parse(j.startedAt)] : []))
    if (starts.length === 0) return []
    const wait = Math.min(...starts) - Date.parse(r.startedAt)
    return Number.isFinite(wait) && wait >= 0 ? [wait] : []
  })
  if (waits.length === 0 || mean(waits) < MINUTE) return undefined
  return `The run waits ${shortTime(mean(waits))} for a runner before it starts: see if runners are busy or too few.`
}

/** A job that only starts when another ends adds both times; in parallel, it would save the smaller of them. */
function sequenceTip(run: RunWithJobs | undefined): string | undefined {
  if (!run) return undefined
  const jobs = run.jobs.flatMap(j => {
    const time = between(j.startedAt, j.completedAt)
    return time === undefined ? [] : [{ name: j.name, start: Date.parse(j.startedAt!), end: Date.parse(j.completedAt!), time }]
  })
  let largest: { after: string; before: string; gain: number } | undefined
  for (const after of jobs)
    for (const before of jobs) {
      if (before === after || after.start < before.end) continue
      const gain = Math.min(before.time, after.time)
      if (!largest || gain > largest.gain) largest = { after: after.name, before: before.name, gain }
    }
  if (!largest || largest.gain < 2 * MINUTE) return undefined
  return `${largest.after} only starts after ${largest.before}, because it depends on it or no runner was free: running them together saves up to ${shortTime(largest.gain)}.`
}

function stepTip(runs: readonly RunWithJobs[], meanMs: number): string | undefined {
  const steps = runs.flatMap(r =>
    r.jobs.flatMap(j =>
      (j.steps ?? []).flatMap(s => {
        const time = between(s.startedAt, s.completedAt)
        return time === undefined ? [] : [{ name: `${s.name}\n${j.name}`, ms: time }]
      }),
    ),
  )
  const [slow] = meansByName(steps).sort((a, b) => b.ms - a.ms)
  if (!slow || slow.ms < 2 * MINUTE || slow.ms < meanMs * 0.2) return undefined
  const [step, job] = slow.name.split('\n')
  return `Step "${step}", in ${job}, takes ${shortTime(slow.ms)}, ${Math.round((100 * slow.ms) / meanMs)}% of the run: ${stepSuggestion(step ?? '')}.`
}

function failuresTip(runs: readonly Run[], workflow: string): string | undefined {
  const ended = runs.filter(r => r.workflowName === workflow && (r.conclusion === 'success' || r.conclusion === 'failure'))
  const failed = ended.filter(r => r.conclusion === 'failure').length
  if (ended.length < 5 || failed / ended.length < 0.2) return undefined
  return `${failed} of ${ended.length} runs failed: each costs another whole run, so it pays to catch the most common failure before pushing.`
}

/** The project's measure: the main workflow's mean and the tips, or undefined with no run that passed. */
export function measure(project: string, runs: readonly Run[], detailed: readonly RunWithJobs[], now: number): ProjectCiTime | undefined {
  const main = mainWorkflow(runs)
  if (!main) return undefined
  const tips = [sequenceTip(detailed[0]), stepTip(detailed, main.meanMs), failuresTip(runs, main.workflow), queueTip(detailed)]
  return { project, ...main, tips: tips.filter((t): t is string => t !== undefined), measuredAt: now }
}

/** The list per project: the new measure replaces the project's old one, and the current project comes first. */
export function remember(list: readonly ProjectCiTime[], fresh: ProjectCiTime): ProjectCiTime[] {
  return [fresh, ...list.filter(t => t.project !== fresh.project)]
}
