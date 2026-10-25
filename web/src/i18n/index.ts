import { createI18n } from 'vue-i18n'
import zhCN from './locales/zh-CN'
import enUS from './locales/en-US'

export type AppLocale = 'zh-CN' | 'en-US'
export const LOCALE_KEY = 'scada_locale'
export const locales: AppLocale[] = ['zh-CN', 'en-US']

function initial(): AppLocale {
  const saved = localStorage.getItem(LOCALE_KEY)
  return saved === 'en-US' || saved === 'zh-CN' ? saved : 'zh-CN'
}

export const i18n = createI18n({ legacy: false, locale: initial(), fallbackLocale: 'zh-CN', messages: { 'zh-CN': zhCN, 'en-US': enUS }, missingWarn: false, fallbackWarn: false })
export const t = (key: string, args?: Record<string, unknown>) => i18n.global.t(key, args ?? {})
