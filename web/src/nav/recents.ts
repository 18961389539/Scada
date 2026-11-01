const LIMIT = 8

function key(userName: string | null | undefined): string {
  return `scada_nav_recent_${userName || 'anon'}`
}

export function loadRecents(userName: string | null | undefined): string[] {
  try {
    const raw = localStorage.getItem(key(userName))
    if (!raw) return []
    const parsed = JSON.parse(raw) as unknown
    return Array.isArray(parsed) ? parsed.filter((x): x is string => typeof x === 'string') : []
  } catch {
    return []
  }
}

/** 记录一次页面访问；同一页连点不重复占位，超限丢弃最旧的。 */
export function pushRecent(userName: string | null | undefined, path: string): void {
  const bare = path.split('?')[0].replace(/\/+$/, '') || '/'
  const current = loadRecents(userName)
  if (current[0] === bare) return
  const next = [bare, ...current.filter((item) => item !== bare)].slice(0, LIMIT)
  try {
    localStorage.setItem(key(userName), JSON.stringify(next))
  } catch {
    /* quota */
  }
  try { window.dispatchEvent(new Event('scada-nav-lists-changed')) } catch { /* ssr */ }
}
