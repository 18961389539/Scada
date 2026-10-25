const MAX = 8

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

export function pushRecent(userName: string | null | undefined, path: string): string[] {
  const bare = path.split('?')[0]
  if (!bare || bare === '/login' || bare.startsWith('/kiosk')) return loadRecents(userName)
  // 动态子路径归一到目录页（画面运行页记为 /screens）
  let normalized = bare
  if (/^\/screens\/\d+$/.test(bare)) normalized = '/screens'
  if (/^\/config\/screens\//.test(bare)) normalized = '/screens'
  if (/^\/config\/object-templates\//.test(bare)) normalized = '/config/object-templates'
  if (/^\/console\//.test(bare)) normalized = '/console'

  const next = [normalized, ...loadRecents(userName).filter((p) => p !== normalized)].slice(0, MAX)
  try {
    localStorage.setItem(key(userName), JSON.stringify(next))
  } catch {
    /* quota */
  }
  try { window.dispatchEvent(new Event('scada-nav-lists-changed')) } catch { /* ssr */ }
  return next
}
