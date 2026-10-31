import type { AgentProgress } from '../../types'

/** The file name for a description: no accents, lowercase, anything but letters and digits turned into "-". */
export function slug(text: string): string {
  return text
    .normalize('NFD')
    .replace(/[̀-ͯ]/g, '')
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, '-')
    .replace(/^-+/, '')
    .replace(/-+$/, '')
}

/** A progress file as written. `feitos` and `etapa` are the field names of the mod this came from, still accepted. */
export function parseProgress(text: string): AgentProgress | undefined {
  const raw = JSON.parse(text) as Record<string, unknown>
  const done = raw.done ?? raw.feitos
  const step = raw.step ?? raw.etapa
  if (typeof done !== 'number' || typeof raw.total !== 'number' || raw.total <= 0) return undefined
  return { done, total: raw.total, step: typeof step === 'string' ? step : undefined }
}
