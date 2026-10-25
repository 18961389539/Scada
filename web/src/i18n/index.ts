import { createI18n } from 'vue-i18n'
import zhFlow from './locales/zh-CN'
import enFlow from './locales/en-US'
import zhCN from './locales/zh-CN.json'
import enUS from './locales/en-US.json'

export type AppLocale = 'zh-CN' | 'en-US'
export const LOCALE_KEY = 'scada_locale'
export const locales: { value: AppLocale; label: string }[] = [{ value: 'zh-CN', label: '简体中文' }, { value: 'en-US', label: 'English' }]

export function normalizeLocale(value: string | null | undefined): AppLocale | null {
  return value === 'en-US' || value === 'zh-CN' ? value : null
}
function initial(): AppLocale {
  // 默认中文（不跟随浏览器语言，避免现场电脑系统语言不同导致界面不一致）；用户选择保存在服务端并同步到本机。
  return normalizeLocale(localStorage.getItem(LOCALE_KEY)) ?? 'zh-CN'
}

// 文案以中文为源（zh-CN.json 由 scripts/i18n-extract.py 生成），英文缺失时回退中文；npm run i18n:check 检查未翻译键。
export const i18n = createI18n({
  legacy: false, globalInjection: true, locale: initial(), fallbackLocale: 'zh-CN',
  messages: { 'zh-CN': { ...zhCN, ...zhFlow }, 'en-US': { ...enUS, ...enFlow } },
  missingWarn: false, fallbackWarn: false
})
document.documentElement.lang = i18n.global.locale.value

export const currentLocale = (): AppLocale => i18n.global.locale.value as AppLocale
export function t(key: string, args?: unknown[] | Record<string, unknown>): string {
  return args ? i18n.global.t(key, args as Record<string, unknown>) : i18n.global.t(key)
}
// 脚本内使用 tr（避免与局部变量 t 冲突）；模板内使用 $t。
export const tr = t

// 切换语言：保存到本机（服务端偏好由调用方保存），整页重载使模块级文案、Element Plus 语言与图表全部更新。
export function applyLocale(locale: AppLocale, reload = true): void {
  if (localStorage.getItem(LOCALE_KEY) === locale && currentLocale() === locale) return
  localStorage.setItem(LOCALE_KEY, locale)
  if (reload) location.reload()
}
