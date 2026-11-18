export type DeviceRuntimeKind = 'online' | 'offline' | 'fault' | 'unknown'

export interface DeviceRuntimeSnapshot {
  online: boolean
  error: string | null
  lastPollTs?: number
  commFailures?: number
}

export function deviceRuntimeKind(status?: DeviceRuntimeSnapshot | null): DeviceRuntimeKind {
  if (!status) return 'unknown'
  if (status.error) return 'fault'
  return status.online ? 'online' : 'offline'
}

export function matchesDeviceRuntimeFilter(status: DeviceRuntimeSnapshot | null | undefined, filter: 'all' | DeviceRuntimeKind): boolean {
  if (filter === 'all') return true
  return deviceRuntimeKind(status) === filter
}
