// Hooks fail open: work on a timer or behind a button never surfaces an error to the session. What a failure means for
// the person is shown where it matters (the CI card says what is missing), not thrown.
export const safely = (work: () => Promise<unknown>) => async (): Promise<void> => {
  try {
    await work()
  } catch {
    // The next tick tries again.
  }
}
