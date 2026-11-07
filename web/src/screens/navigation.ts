import type { LocationQuery, RouteLocationRaw } from 'vue-router'

const listKeys = ['q', 'status', 'sort'] as const

/** 画面列表的筛选条件，运行页跳转时原样带回，返回列表才不会丢掉。 */
export function listQuery(query: LocationQuery): LocationQuery {
  const next: LocationQuery = {}
  for (const key of listKeys) {
    const value = query[key]
    if (typeof value === 'string' && value) next[key] = value
  }
  return next
}

/** 当前画面之前的画面栈，最多 8 层，只接受正整数编号。 */
export function trailIds(query: LocationQuery): number[] {
  const raw = query.trail
  if (typeof raw !== 'string' || !raw) return []
  return raw.split(',').map(item => Number(item)).filter(id => Number.isInteger(id) && id > 0).slice(-8)
}

export function screenRunLocation(screenId: number, query: LocationQuery, trail: number[], base = '/screens'): RouteLocationRaw {
  const next: LocationQuery = { ...listQuery(query) }
  if (query.monitorOnly === '1') next.monitorOnly = '1'
  if (typeof query.replayAt === 'string' && /^\d+$/.test(query.replayAt)) next.replayAt = query.replayAt
  if (trail.length) next.trail = trail.join(',')
  return { path: `${base}/${screenId}`, query: next }
}

// Only return to the current screen or an internal runtime layout.
export function runtimeReturnPath(value: unknown, screenId: number): string | null {
  if (typeof value !== 'string' || value.length > 2048 || !value.startsWith('/') || value.startsWith('//')) return null
  try {
    const target = new URL(value, 'http://scada.local')
    if (target.origin !== 'http://scada.local') return null
    const sameScreen = target.pathname === `/screens/${screenId}` || target.pathname === `/kiosk/screens/${screenId}`
    if (!sameScreen && !/^\/console(?:\/[1-9]\d*)?$/.test(target.pathname)) return null
    return target.pathname + target.search + target.hash
  } catch { return null }
}
