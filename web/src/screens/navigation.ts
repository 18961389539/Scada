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
