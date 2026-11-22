import { describe, expect, it } from 'vitest'
import type { ControlRun } from '../api/control'
import { describeSequenceRun } from './sequenceRunFeedback'

function run(partial: Partial<ControlRun> = {}): ControlRun {
  return {
    id: 9,
    sequenceId: 1,
    sequenceName: 'start line',
    areaId: 1,
    status: 'Running',
    startedTs: 1,
    endedTs: null,
    startedBy: 'op',
    approvedBy: null,
    message: null,
    steps: [
      { index: 0, name: 'open', tagId: 1, value: 1, status: 'done', message: null, startedTs: 1, endedTs: 2 },
      { index: 1, name: 'wait', tagId: null, value: 0, status: 'running', message: null, startedTs: 2, endedTs: null }
    ],
    ...partial
  }
}

describe('describeSequenceRun', () => {
  it('keeps a started run in the running state instead of treating it as done', () => {
    const feedback = describeSequenceRun(run())
    expect(feedback).toMatchObject({ state: 'pending', running: true, terminal: false, doneSteps: 1, totalSteps: 2, currentStep: { name: 'wait' } })
  })

  it('marks failed and aborted runs on the widget', () => {
    expect(describeSequenceRun(run({ status: 'Failed', message: 'timeout', steps: [
      { index: 0, name: 'open', tagId: 1, value: 1, status: 'failed', message: 'timeout', startedTs: 1, endedTs: 2 }
    ] }))).toMatchObject({ state: 'rejected', failed: true, terminal: true, currentStep: { name: 'open' } })
    expect(describeSequenceRun(run({ status: 'Aborted' }))).toMatchObject({ state: 'rejected', aborted: true, terminal: true })
    expect(describeSequenceRun(run({ status: 'Completed', endedTs: 3 }))).toMatchObject({ state: 'ok', terminal: true, running: false })
  })
})
