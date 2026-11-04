import type { MonitorInfo } from '../components/console/windowing'

export interface StoredMonitor { label: string; left: number; top: number; width: number; height: number }

interface CatalogMemory { favorites: number[]; recent: number[] }

const catalogKey = (userId: number) => `scada-screen-catalog:${userId}`
const monitorKey = (userId: number) => `scada-screen-monitor:${userId}`
const ids = (value: unknown): number[] => Array.isArray(value)
  ? [...new Set(value.filter((id): id is number => Number.isSafeInteger(id) && id > 0))].slice(0, 1000)
  : []

export function readCatalogMemory(userId: number | null | undefined): CatalogMemory {
  if (!userId) return { favorites: [], recent: [] }
  try {
    const stored = JSON.parse(localStorage.getItem(catalogKey(userId)) ?? '{}') as { favorites?: unknown; recent?: unknown }
    return { favorites: ids(stored?.favorites), recent: ids(stored?.recent).slice(0, 8) }
  } catch { return { favorites: [], recent: [] } }
}

function writeCatalog(userId: number, memory: CatalogMemory): void {
  try { localStorage.setItem(catalogKey(userId), JSON.stringify(memory)) } catch { /* the choice stays in this window */ }
}

export function toggleScreenFavorite(userId: number | null | undefined, id: number): number[] {
  const memory = readCatalogMemory(userId)
  if (!userId) return memory.favorites
  memory.favorites = memory.favorites.includes(id) ? memory.favorites.filter(previous => previous !== id) : [...memory.favorites, id]
  writeCatalog(userId, memory)
  return memory.favorites
}

export function rememberScreen(userId: number | null | undefined, id: number): void {
  if (!userId || !Number.isSafeInteger(id) || id <= 0) return
  const memory = readCatalogMemory(userId)
  if (memory.recent[0] === id) return
  memory.recent = [id, ...memory.recent.filter(previous => previous !== id)].slice(0, 8)
  writeCatalog(userId, memory)
}

export function readScreenMonitor(userId: number | null | undefined): StoredMonitor | null {
  if (!userId) return null
  try {
    const stored = JSON.parse(localStorage.getItem(monitorKey(userId)) ?? 'null') as Partial<StoredMonitor> | null
    if (!stored || typeof stored.label !== 'string' || !['left', 'top', 'width', 'height'].every(key => Number.isFinite(stored[key as keyof StoredMonitor]))) return null
    return { label: stored.label, left: stored.left!, top: stored.top!, width: stored.width!, height: stored.height! }
  } catch { return null }
}

export function writeScreenMonitor(userId: number | null | undefined, monitor: MonitorInfo): void {
  if (!userId) return
  const stored: StoredMonitor = { label: monitor.label, left: monitor.left, top: monitor.top, width: monitor.width, height: monitor.height }
  try { localStorage.setItem(monitorKey(userId), JSON.stringify(stored)) } catch { /* the dialog remains available */ }
}

export function matchMonitor(saved: StoredMonitor, monitors: MonitorInfo[]): number {
  const exact = monitors.findIndex(item => item.label === saved.label && item.width === saved.width && item.height === saved.height && item.left === saved.left && item.top === saved.top)
  if (exact >= 0) return exact
  const matches = monitors.map((item, index) => ({ item, index })).filter(({ item }) => item.label === saved.label && item.width === saved.width && item.height === saved.height)
  return matches.length === 1 ? matches[0].index : -1
}
