import type { AlarmEvent } from '../types'

const BLOCKING_LEVELS = new Set(['Critical', 'Warning'])
const RELEASE_ACTIONS = new Set(['recover', 'ack', 'shelve', 'suppress'])

/** 活动且未确认、未暂挂/抑制的紧急或警告，会让 Kiosk 保持横幅并暂停轮播。 */
export function isBlockingKioskAlarm(alarm: Pick<AlarmEvent, 'level' | 'endTs' | 'ackTs' | 'shelved' | 'suppressed'>): boolean {
  if (!BLOCKING_LEVELS.has(alarm.level)) return false
  if (alarm.endTs != null || alarm.ackTs != null) return false
  if (alarm.shelved || alarm.suppressed) return false
  return true
}

export type KioskAlarmPauseState = {
  paused: boolean
  banner: string
  alarm: AlarmEvent | null
  count: number
}

export function emptyKioskAlarmPause(): KioskAlarmPauseState {
  return { paused: false, banner: '', alarm: null, count: 0 }
}

function preferred(current: AlarmEvent | null, next: AlarmEvent): AlarmEvent {
  if (!current) return next
  if (current.level !== 'Critical' && next.level === 'Critical') return next
  if (current.level === next.level && next.startTs > current.startTs) return next
  return current
}

/**
 * 按推送更新暂停集合。恢复或确认（以及暂挂/抑制）后，若没有其余活动报警，即可恢复轮播。
 * 横幅始终展示当前最严重、最近发生的一条，单画面大屏同样适用。
 */
export function applyKioskAlarmEvent(
  open: Map<number, AlarmEvent>,
  action: string,
  alarm: AlarmEvent,
  messageOf: (item: AlarmEvent) => string
): { open: Map<number, AlarmEvent>; state: KioskAlarmPauseState } {
  const next = new Map(open)
  if (action === 'raise' || action === 'change' || action === 'unshelve' || action === 'unsuppress') {
    if (isBlockingKioskAlarm(alarm)) next.set(alarm.id, alarm)
    else next.delete(alarm.id)
  } else if (RELEASE_ACTIONS.has(action) || !isBlockingKioskAlarm(alarm)) {
    next.delete(alarm.id)
  }

  let selected: AlarmEvent | null = null
  for (const item of next.values()) selected = preferred(selected, item)
  return {
    open: next,
    state: selected
      ? { paused: true, banner: messageOf(selected), alarm: selected, count: next.size }
      : emptyKioskAlarmPause()
  }
}
