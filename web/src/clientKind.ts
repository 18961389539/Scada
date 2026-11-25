// 与服务端 X-Scada-Client 一致的移动端判定：按 User-Agent，不按窗口宽度。
// 抽成无依赖模块，避免 http.ts 与 mobile.ts 互相引用。
export const MOBILE_UA = /Android|iPhone|iPad|iPod|Mobile|HarmonyOS/i
export function isMobileUserAgent(ua = typeof navigator === 'undefined' ? '' : navigator.userAgent): boolean {
  return MOBILE_UA.test(ua)
}
