import { defineStore } from 'pinia'
import { ref } from 'vue'
import { authApi } from '../api'
import { TOKEN_KEY, USER_KEY } from '../api/http'
import type { AuthUser } from '../types'
import { currentLocale, LOCALE_KEY, normalizeLocale, tr } from '../i18n'

export const useAuthStore = defineStore('auth', () => {
  const token = ref<string>(localStorage.getItem(TOKEN_KEY) ?? '')
  const user = ref<AuthUser | null>(JSON.parse(localStorage.getItem(USER_KEY) ?? 'null') as AuthUser | null)

  function persist(): void {
    if (token.value) localStorage.setItem(TOKEN_KEY, token.value)
    else localStorage.removeItem(TOKEN_KEY)
    if (user.value) localStorage.setItem(USER_KEY, JSON.stringify(user.value))
    else localStorage.removeItem(USER_KEY)
  }

  // 返回 true 表示服务端保存的语言偏好与当前界面不同，调用方应整页跳转以加载该语言。
  async function login(userName: string, password: string): Promise<boolean> {
    const result = await authApi.login(userName, password)
    token.value = result.token
    user.value = result.user
    persist()
    return syncLocale(result.user.locale)
  }

  function syncLocale(locale: string | null | undefined): boolean {
    const preferred = normalizeLocale(locale)
    if (!preferred || preferred === currentLocale()) return false
    localStorage.setItem(LOCALE_KEY, preferred)
    return true
  }

  async function setLocale(locale: string): Promise<void> {
    await authApi.setLocale(locale)
    if (user.value) { user.value = { ...user.value, locale }; persist() }
  }

  async function ensureUser(): Promise<void> {
    // 旧版本缓存的用户信息没有权限明细时，重新拉取一次。
    if (!token.value || (user.value && user.value.access)) return
    try {
      user.value = await authApi.me()
      persist()
    } catch {
      logout()
      throw new Error(tr('auth.k756ae9'))
    }
  }

  function logout(): void {
    token.value = ''
    user.value = null
    persist()
  }

  return { token, user, login, ensureUser, logout, setLocale, syncLocale }
})
