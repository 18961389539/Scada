import { describe, expect, it } from 'vitest'
import { isMomentaryPressBlockedByConfirm, shouldWarnMomentaryConfirm } from './momentaryConfirm'

describe('isMomentaryPressBlockedByConfirm', () => {
  it('blocks only a jog press when the tag requires confirmation', () => {
    expect(isMomentaryPressBlockedByConfirm('press', true)).toBe(true)
    expect(isMomentaryPressBlockedByConfirm('press', false)).toBe(false)
    expect(isMomentaryPressBlockedByConfirm('release', true)).toBe(false)
    expect(isMomentaryPressBlockedByConfirm('pulse', true)).toBe(false)
    expect(isMomentaryPressBlockedByConfirm(undefined, true)).toBe(false)
  })
})

describe('shouldWarnMomentaryConfirm', () => {
  it('warns only when a jog control is bound to a confirmation tag', () => {
    expect(shouldWarnMomentaryConfirm('jog', 7, [7])).toBe(true)
    expect(shouldWarnMomentaryConfirm('jog', 7, new Set([7]))).toBe(true)
    expect(shouldWarnMomentaryConfirm('jog', 7, [8])).toBe(false)
    expect(shouldWarnMomentaryConfirm('jog', null, [7])).toBe(false)
    expect(shouldWarnMomentaryConfirm('pulse', 7, [7])).toBe(false)
    expect(shouldWarnMomentaryConfirm('button', 7, [7])).toBe(false)
  })
})
