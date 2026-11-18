import { afterEach, describe, expect, it, vi } from 'vitest'
import { healthHintText, hubIssueText, isHealthAbnormal, isStandbyRole, standbyPrimaryUrl } from './useServiceStatus'
import type { Health } from '../types'

const t = (key: string, args?: unknown[]) => args ? `${key}:${(args as unknown[]).join(',')}` : key

function health(overrides: Partial<Health> = {}): Health {
  return {
    status: 'ok',
    environment: 'prod',
    serverTime: '2026-11-18T00:00:00Z',
    uptimeSeconds: 1,
    devices: [],
    onlineDevices: 3,
    totalDevices: 4,
    configuredTags: 10,
    busBacklog: 0,
    disk: { root: 'C', totalGb: 100, freeGb: 12.5 },
    storage: { configDb: 'db', configDbBytes: 1, historyBytes: 1 },
    ...overrides
  }
}

describe('hubIssueText', () => {
  it('only labels reconnecting and disconnected', () => {
    expect(hubIssueText('reconnecting', t)).toBe('appLayout.hubReconnecting')
    expect(hubIssueText('disconnected', t)).toBe('appLayout.hubDisconnected')
    expect(hubIssueText('connected', t)).toBe('')
    expect(hubIssueText('connecting', t)).toBe('')
  })
})

describe('health status helpers', () => {
  it('treats a failed health request or a non-ok payload as abnormal', () => {
    expect(isHealthAbnormal(null, '')).toBe(false)
    expect(isHealthAbnormal(null, 'down')).toBe(true)
    expect(isHealthAbnormal(health(), '')).toBe(false)
    expect(isHealthAbnormal(health({ status: 'degraded' }), '')).toBe(true)
  })

  it('formats the health hint and keeps a missing disk figure as --', () => {
    expect(healthHintText(null, t)).toBe('')
    expect(healthHintText(health(), t)).toBe('appLayout.healthDetail:3,4,0,12.5')
    expect(healthHintText(health({ disk: { root: 'C', totalGb: 100, freeGb: undefined as unknown as number } }), t)).toBe('appLayout.healthDetail:3,4,0,--')
  })

  it('exposes standby role and primary url without inventing a link', () => {
    expect(isStandbyRole(health())).toBe(false)
    expect(standbyPrimaryUrl(health())).toBe('')
    const standby = health({ ha: { node: 'b', role: 'Standby', primaryUrl: 'https://primary' } })
    expect(isStandbyRole(standby)).toBe(true)
    expect(standbyPrimaryUrl(standby)).toBe('https://primary')
    expect(standbyPrimaryUrl(health({ ha: { node: 'b', role: 'Standby', primaryUrl: null } }))).toBe('')
  })
})

afterEach(() => {
  vi.clearAllMocks()
})
