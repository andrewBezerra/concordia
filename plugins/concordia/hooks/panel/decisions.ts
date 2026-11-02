// The decisions still to be made. Whoever works with the person keeps the file, and the panel only reads it:
// ~/.claude/concordia/pending-decisions.json, documented in the README.

export type Decision = { title: string; blocks?: string; risk?: string; url?: string }

const text = (value: unknown): string | undefined => (typeof value === 'string' && value.trim() ? value.trim() : undefined)

// The file is written by an agent, and its URLs become links on the screen.
const https = (value: unknown): string | undefined => {
  const url = text(value)
  if (!url) return undefined
  try {
    return new URL(url).protocol === 'https:' ? url : undefined
  } catch {
    return undefined
  }
}

const weight = (d: Decision) => (d.blocks ? 0 : d.risk ? 1 : 2)

/** The decisions of a file: what blocks first, then what carries a risk, each group in the file's order. An entry without a title is dropped. */
export function parseDecisions(content: string): Decision[] {
  const raw = JSON.parse(content) as { decisions?: unknown }
  if (!Array.isArray(raw.decisions)) throw new Error('no decisions list')
  const decisions: Decision[] = []
  for (const entry of raw.decisions as Record<string, unknown>[]) {
    const title = text(entry?.title)
    if (!title) continue
    const blocks = text(entry.blocks)
    const risk = text(entry.risk)
    const url = https(entry.url)
    decisions.push({ title, ...(blocks ? { blocks } : {}), ...(risk ? { risk } : {}), ...(url ? { url } : {}) })
  }
  return decisions.map((d, i) => ({ d, i })).sort((a, b) => weight(a.d) - weight(b.d) || a.i - b.i).map(({ d }) => d)
}

/** A file that does not exist means nothing is pending; any other failure is a problem to show. */
export const isMissingFile = (error: unknown) => /ENOENT|not found|no such file/i.test(String(error))
