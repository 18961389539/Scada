// 侧栏折叠偏好：记住用户手动展开/收起，媒体查询变化不得立刻把手动展开收回去。
import { readLastText, rememberText } from './lastChoice'

export const SIDEBAR_MINI_KEY = 'sidebar_mini'

export function readSidebarMiniPreference(): boolean | null {
  const value = readLastText(SIDEBAR_MINI_KEY)
  if (value === 'mini') return true
  if (value === 'expanded') return false
  return null
}

export function rememberSidebarMiniPreference(mini: boolean): void {
  rememberText(SIDEBAR_MINI_KEY, mini ? 'mini' : 'expanded')
}

/** 有手动偏好时以偏好为准；否则跟随 769–1120px 的迷你侧栏媒体查询。 */
export function sidebarMiniFromMedia(pref: boolean | null, mediaMini: boolean): boolean {
  return pref ?? mediaMini
}
