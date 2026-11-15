import { describe, expect, it } from 'vitest'
import { expressionTagIds, minuteToTime, timeToMinute } from './utils'

describe('expressionTagIds', () => {
  it('extracts unique positive tag ids from alarm expressions', () => {
    expect(expressionTagIds('#12 + #7 * 2')).toEqual([12, 7])
    expect(expressionTagIds('#3 + #3 + #0 + #-1')).toEqual([3])
    expect(expressionTagIds(null)).toEqual([])
    expect(expressionTagIds('1 + 2')).toEqual([])
  })
})

describe('minute helpers', () => {
  it('wraps day boundaries and parses clock text', () => {
    expect(minuteToTime(0)).toBe('00:00')
    expect(minuteToTime(1440)).toBe('00:00')
    expect(minuteToTime(510)).toBe('08:30')
    expect(timeToMinute('08:30')).toBe(510)
    expect(timeToMinute('bad')).toBe(0)
  })
})
