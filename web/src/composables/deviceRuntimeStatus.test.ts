import { describe, expect, it } from 'vitest'
import { deviceRuntimeKind, matchesDeviceRuntimeFilter } from './deviceRuntimeStatus'

describe('deviceRuntimeKind', () => {
  it('treats a missing snapshot as unknown', () => {
    expect(deviceRuntimeKind(undefined)).toBe('unknown')
    expect(deviceRuntimeKind(null)).toBe('unknown')
  })

  it('prefers fault when an error is present, even if the device is still marked online', () => {
    expect(deviceRuntimeKind({ online: true, error: 'timeout' })).toBe('fault')
  })

  it('maps online and offline flags', () => {
    expect(deviceRuntimeKind({ online: true, error: null })).toBe('online')
    expect(deviceRuntimeKind({ online: false, error: null })).toBe('offline')
  })
})

describe('matchesDeviceRuntimeFilter', () => {
  const offline = { online: false, error: null }
  it('lets every snapshot through the all filter', () => {
    expect(matchesDeviceRuntimeFilter(undefined, 'all')).toBe(true)
    expect(matchesDeviceRuntimeFilter(offline, 'all')).toBe(true)
  })

  it('filters by the resolved runtime kind', () => {
    expect(matchesDeviceRuntimeFilter(offline, 'offline')).toBe(true)
    expect(matchesDeviceRuntimeFilter(offline, 'online')).toBe(false)
    expect(matchesDeviceRuntimeFilter(undefined, 'offline')).toBe(false)
  })
})
