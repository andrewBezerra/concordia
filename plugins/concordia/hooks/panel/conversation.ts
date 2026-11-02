import type { ConversationLine } from '../../types'

// The agent screen shows the end of the conversation: that is what is happening now.
export const SCREEN_LINES = 40
const LINE_LENGTH = 400

type Message = {
  role: string
  text: string
  toolUses: ReadonlyArray<{ tool: string; input: Record<string, unknown>; isError?: true }>
}

const oneLine = (text: string) => {
  const clean = text.replace(/\s+/g, ' ').trim()
  return clean.length > LINE_LENGTH ? `${clean.slice(0, LINE_LENGTH)}…` : clean
}

// What identifies the call without its whole content: the description, when the tool has one.
function toolSummary(input: Record<string, unknown>): string {
  for (const field of ['description', 'file_path', 'pattern', 'command', 'url', 'query']) {
    const value = input[field]
    if (typeof value === 'string' && value) return oneLine(value)
  }
  return ''
}

export function conversationLines(messages: readonly Message[]): ConversationLine[] {
  const lines: ConversationLine[] = []
  for (const m of messages) {
    if (m.text.trim()) lines.push({ kind: m.role === 'user' ? 'request' : 'text', text: oneLine(m.text) })
    for (const use of m.toolUses) {
      const summary = toolSummary(use.input)
      const text = use.tool + (summary ? `: ${summary}` : '') + (use.isError ? ' (failed)' : '')
      lines.push({ kind: use.isError ? 'error' : 'tool', text })
    }
  }
  return lines.slice(-SCREEN_LINES)
}
