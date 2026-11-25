import { describe, expect, it } from 'vitest'
import { controlConfirmCurrentLabel, controlConfirmKind } from './controlConfirmDisplay'

describe('controlConfirmKind', () => {
  it('treats a string target as text and a numeric target as number', () => {
    expect(controlConfirmKind('abc')).toBe('text')
    expect(controlConfirmKind(12)).toBe('number')
  })
})

describe('controlConfirmCurrentLabel', () => {
  it('does not invent a current text value when the server left it empty', () => {
    expect(controlConfirmCurrentLabel('text', null, 'unavailable')).toBe('unavailable')
    expect(controlConfirmCurrentLabel('text', 3, 'unavailable')).toBe('unavailable')
  })

  it('keeps numeric current values and marks a missing number as unavailable', () => {
    expect(controlConfirmCurrentLabel('number', 12.5, 'unavailable')).toBe('12.5')
    expect(controlConfirmCurrentLabel('number', 0, 'unavailable')).toBe('0')
    expect(controlConfirmCurrentLabel('number', null, 'unavailable')).toBe('unavailable')
    expect(controlConfirmCurrentLabel('number', null, 'unavailable', '通讯中断')).toBe('通讯中断')
    expect(controlConfirmCurrentLabel('text', null, 'unavailable', '质量无效')).toBe('质量无效')
  })
})
