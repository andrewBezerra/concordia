// The values the panel keeps in $.state. Claude Code before 2.1.299 only finds this file through the manifest's `types`.

/** What an agent writes to its progress file: how many items it finished, out of how many, and at which step. */
export type AgentProgress = { done: number; total: number; step?: string }

/** What is remembered when an agent starts: $.agent.list() has neither its model nor its start time. */
export type AgentBirth = { model: string; startedAt: number }

/** One row of the agents view: the agent as $.agent.list() gives it, what was remembered, and the progress read. */
export type AgentRow = {
  id: string
  description: string
  status: string
  model?: string
  startedAt?: number
  progress?: AgentProgress
}

/** One line of the agent screen: the request, what the agent said, or a tool it called. */
export type ConversationLine = { kind: 'request' | 'text' | 'tool' | 'error'; text: string }

/** One session from `claude agents --json`. */
export type BackgroundSession = { name: string; folder: string; kind: string; status: string; startedAt: number; waitingFor?: string }

export type CiState = {
  pr: number
  total: number
  passed: number
  running: number
  failures: string[]
  /** The pull request's checks page. */
  url?: string
  /** Where to see each failure: the check's job, when GitHub gives one. */
  failureLinks: { name: string; url: string }[]
}

export type ProjectCiTime = { project: string; workflow: string; meanMs: number; samples: number; tips: string[]; measuredAt: number }

export type TokensSummary = {
  /** How many prompts the mean covers (up to 20). */
  prompts: number
  last: number
  mean: number
  /** The last prompt cost more than twice the mean, with at least 5 prompts counted. */
  aboveMean: boolean
  /** The context the last prompt sent again on each step. */
  context: number
  meanSteps: number
  /** The first suggestion, when there is one. */
  tip?: string
}

declare module 'claude-code' {
  interface PluginState {
    concordia: {
      agentBirths: Record<string, AgentBirth>
      agents: AgentRow[]
      sessions: BackgroundSession[]
      openAgent: string | null
      conversation: ConversationLine[]
      conversationError: string | null
      ciState: CiState | null
      /** Why CI cannot be read at all (no gh, not a repository), in plain words. */
      ciProblem: string | null
      ciCheckedAt: number
      ciTimes: ProjectCiTime[]
      tokensSummary: TokensSummary | null
      statusCi: string | null
      statusTokens: string | null
      /** The "how to open it" toast already showed in this session. */
      openHintShown: boolean
    }
  }
}
