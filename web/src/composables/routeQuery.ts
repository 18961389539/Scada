import type { LocationQuery, LocationQueryRaw, Router } from 'vue-router'

// 分析类页面把「已查询」的条件写进地址：刷新、返回或把链接发给别人都能回到同一份结果

export const queryNumber = (value: unknown): number | undefined =>
  typeof value === 'string' && value !== '' && Number.isFinite(Number(value)) ? Number(value) : undefined

export const queryNumbers = (value: unknown): number[] =>
  (typeof value === 'string' && value ? value.split(',') : []).filter(x => x !== '').map(Number).filter(Number.isFinite)

export const queryRange = (query: LocationQuery): [Date, Date] | null => {
  const from = queryNumber(query.from), to = queryNumber(query.to)
  return from != null && to != null && from < to ? [new Date(from), new Date(to)] : null
}

type QueryValue = string | number | number[] | null | undefined

export function projectQuery(current: LocationQuery, patch: Record<string, QueryValue>): LocationQuery {
  const query: LocationQuery = { ...current }
  for (const [key, value] of Object.entries(patch)) {
    if (value == null || value === '' || (Array.isArray(value) && !value.length)) delete query[key]
    else query[key] = Array.isArray(value) ? value.join(',') : String(value)
  }
  return query
}

export function replaceQuery(router: Router, current: LocationQuery, patch: Record<string, QueryValue>): void {
  const query: LocationQueryRaw = projectQuery(current, patch)
  if (JSON.stringify(query) !== JSON.stringify(current)) void router.replace({ query })
}

function querySignature(query: LocationQuery, keys: readonly string[]): string {
  const picked: Record<string, string> = {}
  for (const key of keys) {
    const value = query[key]
    picked[key] = typeof value === 'string' ? value : ''
  }
  return JSON.stringify(picked)
}

// The same route component stays mounted when only the query changes. note() marks
// the query this page just wrote so that replace does not query again. changed()
// is true for a later popstate, such as the browser Back button.
export function createQuerySync(read: () => LocationQuery, keys: readonly string[]) {
  let synced = querySignature(read(), keys)
  return {
    note(base: LocationQuery, patch: Record<string, QueryValue>) {
      synced = querySignature(projectQuery(base, patch), keys)
    },
    changed(): boolean {
      const next = querySignature(read(), keys)
      if (next === synced) return false
      synced = next
      return true
    }
  }
}
