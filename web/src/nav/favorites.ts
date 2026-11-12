function key(userName: string | null | undefined): string {
  return `scada_nav_fav_${userName || 'anon'}`
}

export function loadFavorites(userName: string | null | undefined): string[] {
  try {
    const raw = localStorage.getItem(key(userName))
    if (!raw) return []
    const parsed = JSON.parse(raw) as unknown
    return Array.isArray(parsed) ? parsed.filter((x): x is string => typeof x === 'string') : []
  } catch {
    return []
  }
}

function write(userName: string | null | undefined, paths: string[]): void {
  try {
    localStorage.setItem(key(userName), JSON.stringify(paths))
  } catch {
    /* quota */
  }
  try { window.dispatchEvent(new Event('scada-nav-lists-changed')) } catch { /* ssr */ }
}

export function saveFavorites(userName: string | null | undefined, paths: string[]): void {
  write(userName, paths)
}

export function toggleFavorite(userName: string | null | undefined, path: string): string[] {
  const bare = path.split('?')[0]
  const cur = loadFavorites(userName)
  const next = cur.includes(bare) ? cur.filter((p) => p !== bare) : [...cur, bare]
  write(userName, next)
  return next
}
