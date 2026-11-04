export interface AlarmScope {
  alarmAreaId?: number | null
  alarmDeviceId?: number | null
  alarmScreenId?: number | null
}

export interface ScopedAlarm {
  deviceId: number | null
  tagId: number | null
  areas?: number[]
}

/** An unbound list matches every alarm. A bound list matches only that area, device, or screen. */
export function alarmMatchesScope(scope: AlarmScope, alarm: ScopedAlarm, screenTags?: ReadonlySet<number> | null): boolean {
  if (scope.alarmDeviceId) return alarm.deviceId === scope.alarmDeviceId
  if (scope.alarmAreaId) return (alarm.areas ?? []).includes(scope.alarmAreaId)
  if (scope.alarmScreenId) return alarm.tagId != null && (screenTags?.has(alarm.tagId) ?? false)
  return true
}

export function alarmScopeBound(scope: AlarmScope): boolean {
  return scope.alarmAreaId != null || scope.alarmDeviceId != null || scope.alarmScreenId != null
}
