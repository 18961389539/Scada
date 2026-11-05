import { defineStore } from 'pinia'
import { computed, ref } from 'vue'
import { authApi } from '../api'
import { TOKEN_KEY, USER_KEY } from '../api/http'
import type { AuthUser } from '../types'
import { currentLocale, LOCALE_KEY, normalizeLocale, tr } from '../i18n'

export const useAuthStore = defineStore('auth', () => {
  const token = ref<string>(localStorage.getItem(TOKEN_KEY) ?? '')
  const user = ref<AuthUser | null>(JSON.parse(localStorage.getItem(USER_KEY) ?? 'null') as AuthUser | null)

  // 服务端也会拦（428），这里只负责把人引到改密页，避免每次刷新都撞一串失败提示。
  const mustChangePassword = computed(() => user.value?.mustChangePassword === true)

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

  async function confirmSession(): Promise<void> {
    if (!token.value) return
    user.value = await authApi.me()
    persist()
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

  // 改密成功即换令牌：服务端按新口令派生了 CredentialsVersion，旧令牌已经不可用。
  async function changePassword(currentPassword: string, newPassword: string): Promise<boolean> {
    const result = await authApi.changePassword(currentPassword, newPassword)
    token.value = result.token
    user.value = result.user
    persist()
    return syncLocale(result.user.locale)
  }

  function logout(): void {
    token.value = ''
    user.value = null
    persist()
  }

  return { token, user, mustChangePassword, login, ensureUser, confirmSession, logout, setLocale, syncLocale, changePassword }
})
