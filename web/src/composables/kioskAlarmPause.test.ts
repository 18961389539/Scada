import { describe, expect, it } from 'vitest'
import type { AlarmEvent } from '../types'
import { applyKioskAlarmEvent, emptyKioskAlarmPause, isBlockingKioskAlarm, isDrillKioskAlarm } from './kioskAlarmPause'

function alarm(partial: Partial<AlarmEvent> = {}): AlarmEvent {
  return {
    id: 1,
    tagId: 10,
    deviceId: 2,
    ruleId: 3,
    level: 'Critical',
    value: 1,
    startTs: 1000,
    endTs: null,
    ackUser: null,
    ackTs: null,
    message: 'high temp',
    ...partial
  }
}

describe('kioskAlarmPause', () => {
  it('treats active unacked critical and warning alarms as blocking', () => {
    expect(isBlockingKioskAlarm(alarm({ level: 'Critical' }))).toBe(true)
    expect(isBlockingKioskAlarm(alarm({ level: 'Warning' }))).toBe(true)
    expect(isBlockingKioskAlarm(alarm({ level: 'Info' }))).toBe(false)
    expect(isBlockingKioskAlarm(alarm({ endTs: 2000 }))).toBe(false)
    expect(isBlockingKioskAlarm(alarm({ ackTs: 2000 }))).toBe(false)
    expect(isBlockingKioskAlarm(alarm({ shelved: true }))).toBe(false)
  })

  it('keeps the banner until the blocking alarm recovers or is acknowledged', () => {
    let open = new Map<number, AlarmEvent>()
    let applied = applyKioskAlarmEvent(open, 'raise', alarm({ message: 'critical' }), item => item.message)
    expect(applied.state).toMatchObject({ paused: true, banner: 'critical', count: 1 })

    applied = applyKioskAlarmEvent(applied.open, 'raise', alarm({ id: 2, level: 'Warning', startTs: 2000, message: 'warn' }), item => item.message)
    expect(applied.state.banner).toBe('critical')
    expect(applied.state.count).toBe(2)

    applied = applyKioskAlarmEvent(applied.open, 'ack', alarm({ id: 1, ackTs: 3000 }), item => item.message)
    expect(applied.state).toMatchObject({ paused: true, banner: 'warn', count: 1 })

    applied = applyKioskAlarmEvent(applied.open, 'recover', alarm({ id: 2, level: 'Warning', endTs: 4000 }), item => item.message)
    expect(applied.state).toEqual(emptyKioskAlarmPause())
  })

  it('does not treat simulated alarms as official blocking, but can count them as a drill hint', () => {
    expect(isBlockingKioskAlarm(alarm({ level: 'Critical', simulated: true }))).toBe(false)
    expect(isDrillKioskAlarm(alarm({ level: 'Critical', simulated: true }))).toBe(true)
    const applied = applyKioskAlarmEvent(new Map(), 'raise', alarm({ level: 'Critical', simulated: true, message: 'drill' }), item => item.message)
    expect(applied.state.paused).toBe(false)
    expect(applied.state.count).toBe(0)
    expect(applied.state.drillCount).toBe(1)
  })

  it('does not clear a remaining critical alarm on a timed release', () => {
    const applied = applyKioskAlarmEvent(new Map(), 'raise', alarm(), item => item.message)
    expect(applied.state.paused).toBe(true)
    expect(applyKioskAlarmEvent(applied.open, 'change', alarm(), item => item.message).state.paused).toBe(true)
  })
})
