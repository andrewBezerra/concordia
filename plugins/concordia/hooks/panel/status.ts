// A plugin has one status line, and $.ui.status replaces it, so CI and tokens have to share it.
export function composeStatus(ci: string | null | undefined, tokens: string | null | undefined): string | undefined {
  return [ci, tokens].filter(Boolean).join(' | ') || undefined
}
