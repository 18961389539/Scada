import { createI18n } from 'vue-i18n'

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

// 只加载当前语言包：中文站点不再为首屏携带约 79 KB（gzip）的英文文案。
// 顶层 await 让所有 `import { tr } from '../i18n'` 的模块等待语言包就绪，
// 因此模块级的 400+ 处 tr() 常量仍然取到正确语言；切换语言本来就会整页重载。
const active = initial()
const zhFlow = (await import('./locales/zh-CN')).default
const zhCN = (await import('./locales/zh-CN.json')).default
const zhMessages = { ...zhCN, ...zhFlow }
const enMessages = active === 'en-US'
  ? await Promise.all([import('./locales/en-US'), import('./locales/en-US.json')]).then(([flow, json]) => ({ ...json.default, ...flow.default }))
  : zhMessages
export const i18n = createI18n({
  legacy: false, globalInjection: true, locale: active, fallbackLocale: 'zh-CN',
  messages: { 'zh-CN': zhMessages, 'en-US': enMessages },
  missingWarn: false, fallbackWarn: false
})
document.documentElement.lang = active

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
