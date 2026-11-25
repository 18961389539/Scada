import { describe, expect, it } from 'vitest'
import { missedCriticalAlarms } from './useAlarmSession'
import type { AlarmEvent } from '../types'

function alarm(partial: Partial<AlarmEvent> & Pick<AlarmEvent, 'id' | 'level' | 'startTs'>): AlarmEvent {
  return {
    tagId: 1,
    message: 'trip',
    ackTs: null,
    endTs: null,
    ...partial
  } as AlarmEvent
}

describe('missedCriticalAlarms', () => {
  it('keeps still-active or returned-unacked critical alarms touched in the window', () => {
    const from = 1000
    const to = 2000
    const missed = missedCriticalAlarms([
      alarm({ id: 1, level: 'Critical', startTs: 1500, endTs: null, ackTs: null }),
      alarm({ id: 2, level: 'Critical', startTs: 900, endTs: 1600, ackTs: null }),
      alarm({ id: 3, level: 'Critical', startTs: 1500, endTs: 1800, ackTs: 1700 }),
      alarm({ id: 4, level: 'Warning', startTs: 1500, endTs: null, ackTs: null }),
      alarm({ id: 5, level: 'Critical', startTs: 100, endTs: null, ackTs: null })
    ], from, to)
    expect(missed.map(item => item.id)).toEqual([1, 2])
  })
})
