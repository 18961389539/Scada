// 多显示器窗口：优先使用 Window Management API（Chromium，需用户授权）把窗口放到指定显示器；不可用时退化为普通新窗口。
export interface MonitorInfo { label: string; left: number; top: number; width: number; height: number; isPrimary: boolean }
interface ScreenDetailed { label?: string; availLeft: number; availTop: number; availWidth: number; availHeight: number; isPrimary?: boolean }
type WindowWithScreens = Window & { getScreenDetails?: () => Promise<{ screens: ScreenDetailed[] }> }

export const windowManagementSupported = () => typeof (window as WindowWithScreens).getScreenDetails === 'function'

export async function listMonitors(): Promise<MonitorInfo[]> {
  const w = window as WindowWithScreens
  if (w.getScreenDetails) {
    try {
      const details = await w.getScreenDetails()
      return details.screens.map((s, i) => ({ label: s.label || `显示器 ${i + 1}`, left: s.availLeft, top: s.availTop, width: s.availWidth, height: s.availHeight, isPrimary: !!s.isPrimary }))
    } catch { /* 用户拒绝授权：退化 */ }
  }
  return [{ label: '当前显示器', left: (window.screen as unknown as ScreenDetailed).availLeft ?? 0, top: (window.screen as unknown as ScreenDetailed).availTop ?? 0, width: window.screen.availWidth, height: window.screen.availHeight, isPrimary: true }]
}

export function openOnMonitor(url: string, name: string, monitor?: MonitorInfo): Window | null {
  const features = monitor ? `popup,left=${monitor.left},top=${monitor.top},width=${monitor.width},height=${monitor.height}` : 'popup'
  const win = window.open(url, name, features)
  // 部分浏览器忽略 left/top：打开后再移动一次（同源窗口允许）。
  if (win && monitor) { try { win.moveTo(monitor.left, monitor.top); win.resizeTo(monitor.width, monitor.height) } catch { /* 忽略 */ } }
  return win
}
