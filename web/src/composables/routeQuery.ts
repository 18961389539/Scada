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

export function replaceQuery(router: Router, current: LocationQuery, patch: Record<string, QueryValue>): void {
  const query: LocationQueryRaw = { ...current }
  for (const [key, value] of Object.entries(patch)) {
    if (value == null || value === '' || (Array.isArray(value) && !value.length)) delete query[key]
    else query[key] = Array.isArray(value) ? value.join(',') : String(value)
  }
  if (JSON.stringify(query) !== JSON.stringify(current)) void router.replace({ query })
}
