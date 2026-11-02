export interface NavOrder {
  groups: string[]
  items: Record<string, string[]>
}

const empty = (): NavOrder => ({ groups: [], items: {} })

function key(userName: string | null | undefined): string {
  return `scada_nav_order_${userName || 'anon'}`
}

export function loadNavOrder(userName: string | null | undefined): NavOrder {
  try {
    const raw = localStorage.getItem(key(userName))
    if (!raw) return empty()
    const parsed = JSON.parse(raw) as unknown
    if (!parsed || typeof parsed !== 'object') return empty()
    const record = parsed as { groups?: unknown; items?: unknown }
    const groups = Array.isArray(record.groups) ? record.groups.filter((id): id is string => typeof id === 'string') : []
    const items: Record<string, string[]> = {}
    if (record.items && typeof record.items === 'object') {
      for (const [groupId, value] of Object.entries(record.items)) {
        if (Array.isArray(value)) items[groupId] = value.filter((id): id is string => typeof id === 'string')
      }
    }
    return { groups, items }
  } catch {
    return empty()
  }
}

export function saveNavOrder(userName: string | null | undefined, order: NavOrder): void {
  try {
    localStorage.setItem(key(userName), JSON.stringify(order))
  } catch {
    /* quota */
  }
  try { window.dispatchEvent(new Event('scada-nav-lists-changed')) } catch { /* ssr */ }
}

/** 已保存的 id 优先；目录新增的条目按原顺序接在后面。不在目录中的 id 被忽略。 */
export function displayOrder(catalogIds: readonly string[], saved: readonly string[] | undefined): string[] {
  if (!saved?.length) return [...catalogIds]
  const catalog = new Set(catalogIds)
  const seen = new Set<string>()
  const result: string[] = []
  for (const id of saved) {
    if (catalog.has(id) && !seen.has(id)) {
      result.push(id)
      seen.add(id)
    }
  }
  for (const id of catalogIds) {
    if (!seen.has(id)) result.push(id)
  }
  return result
}

/** 把 from 插到 to 的前面或后面。隐藏在两者之间的条目保持原位。 */
export function moveRelative(order: readonly string[], from: string, to: string, after: boolean): string[] {
  if (from === to || !order.includes(from) || !order.includes(to)) return [...order]
  const next = order.filter(id => id !== from)
  let index = next.indexOf(to)
  if (after) index += 1
  next.splice(index, 0, from)
  return next
}

/**
 * 只在当前看得到的条目之间移动，并保住暂不可见 id 的相对位置。
 * 例如权限暂时看不到的菜单项，恢复权限后仍回到原来的空档。
 */
export function reorderVisible(
  saved: readonly string[] | undefined,
  catalogIds: readonly string[],
  from: string,
  to: string,
  after: boolean
): string[] {
  const current = displayOrder(catalogIds, saved)
  const moved = moveRelative(current, from, to, after)
  const catalog = new Set(catalogIds)
  if (!(saved ?? []).some(id => !catalog.has(id))) return moved
  const result: string[] = []
  const seen = new Set<string>()
  let visibleIndex = 0
  for (const id of saved ?? []) {
    if (seen.has(id)) continue
    if (catalog.has(id)) {
      const nextId = moved[visibleIndex++]
      if (nextId && !seen.has(nextId)) {
        result.push(nextId)
        seen.add(nextId)
      }
    } else {
      result.push(id)
      seen.add(id)
    }
  }
  while (visibleIndex < moved.length) {
    const nextId = moved[visibleIndex++]
    if (!seen.has(nextId)) {
      result.push(nextId)
      seen.add(nextId)
    }
  }
  return result
}
