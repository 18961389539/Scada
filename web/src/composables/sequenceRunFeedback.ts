import type { ControlRun, ControlStepResult } from '../api/control'

export type SequenceRunFeedbackState = 'pending' | 'ok' | 'rejected'

export type SequenceRunFeedback = {
  state: SequenceRunFeedbackState
  running: boolean
  terminal: boolean
  failed: boolean
  aborted: boolean
  currentStep: ControlStepResult | undefined
  doneSteps: number
  totalSteps: number
}

export function describeSequenceRun(run: Pick<ControlRun, 'status' | 'steps'>): SequenceRunFeedback {
  const steps = run.steps ?? []
  const currentStep = steps.find(step => step.status === 'running') ?? steps.find(step => step.status === 'failed')
  const doneSteps = steps.filter(step => step.status === 'done' || step.status === 'failed' || step.status === 'skipped').length
  const running = run.status === 'Running'
  const failed = run.status === 'Failed'
  const aborted = run.status === 'Aborted'
  const terminal = run.status === 'Completed' || failed || aborted
  const state: SequenceRunFeedbackState = failed || aborted ? 'rejected' : running || run.status === 'Pending' ? 'pending' : 'ok'
  return { state, running, terminal, failed, aborted, currentStep, doneSteps, totalSteps: steps.length }
}
