import { ref } from 'vue'
import { http } from './api/http'
import { isMobileUserAgent } from './clientKind'

export { isMobileUserAgent, MOBILE_UA } from './clientKind'

// 与服务端 X-Scada-Client / http.ts 使用同一套 UA 判定：桌面缩窄窗口不能当成手机禁控，
// 大屏平板（iPad 等）也不能因为窗口够宽而绕过限制。布局适配仍用下方的 isMobile（视口）。
export const isMobileClient = ref(isMobileUserAgent())

// 布局用的窄屏/触屏判定，不参与控制策略。
const query = typeof window !== 'undefined' && window.matchMedia ? window.matchMedia('(max-width: 768px), (pointer: coarse) and (max-width: 1024px)') : null
export const isMobile = ref(query?.matches ?? false)
query?.addEventListener('change', (e) => { isMobile.value = e.matches })

// 服务端返回当前用户在手机端是否允许控制（Mobile:AllowControl / Mobile:ControlRoles）；拿不到时按不允许处理。
export const mobileControl = ref(false)
let loaded: Promise<void> | null = null
export function loadClientPolicy(force = false): Promise<void> {
  if (!loaded || force) loaded = http.get<{ mobileControl: boolean }>('/client-policy').then(r => { mobileControl.value = r.data.mobileControl }).catch(() => { mobileControl.value = false; loaded = null })
  return loaded
}
export const controlBlockedOnMobile = () => isMobileClient.value && !mobileControl.value

// 双指缩放：返回解绑函数。仅处理两指手势，单指仍交给浏览器原生滚动。
export function attachPinch(el: HTMLElement, get: () => number, set: (zoom: number) => void, min = .2, max = 4): () => void {
  let start = 0, base = 1
  const dist = (t: TouchList) => Math.hypot(t[0].clientX - t[1].clientX, t[0].clientY - t[1].clientY)
  const onStart = (e: TouchEvent) => { if (e.touches.length === 2) { start = dist(e.touches); base = get() } }
  const onMove = (e: TouchEvent) => {
    if (e.touches.length !== 2 || !start) return
    e.preventDefault()
    set(Math.round(Math.min(max, Math.max(min, base * dist(e.touches) / start)) * 100) / 100)
  }
  const onEnd = (e: TouchEvent) => { if (e.touches.length < 2) start = 0 }
  el.addEventListener('touchstart', onStart, { passive: true })
  el.addEventListener('touchmove', onMove, { passive: false })
  el.addEventListener('touchend', onEnd)
  el.addEventListener('touchcancel', onEnd)
  return () => { el.removeEventListener('touchstart', onStart); el.removeEventListener('touchmove', onMove); el.removeEventListener('touchend', onEnd); el.removeEventListener('touchcancel', onEnd) }
}
