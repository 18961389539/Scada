import { defineStore } from 'pinia'
import { ref } from 'vue'
import { authApi } from '../api'
import { TOKEN_KEY, USER_KEY } from '../api/http'
import type { AuthUser } from '../types'

export const useAuthStore = defineStore('auth', () => {
  const token = ref<string>(localStorage.getItem(TOKEN_KEY) ?? '')
  const user = ref<AuthUser | null>(JSON.parse(localStorage.getItem(USER_KEY) ?? 'null') as AuthUser | null)

  function persist(): void {
    if (token.value) localStorage.setItem(TOKEN_KEY, token.value)
    else localStorage.removeItem(TOKEN_KEY)
    if (user.value) localStorage.setItem(USER_KEY, JSON.stringify(user.value))
    else localStorage.removeItem(USER_KEY)
  }

  async function login(userName: string, password: string): Promise<void> {
    const result = await authApi.login(userName, password)
    token.value = result.token
    user.value = result.user
    persist()
  }

  async function ensureUser(): Promise<void> {
    if (!token.value || user.value) return
    try {
      user.value = await authApi.me()
      persist()
    } catch {
      logout()
      throw new Error('登录已过期')
    }
  }

  function logout(): void {
    token.value = ''
    user.value = null
    persist()
  }

  return { token, user, login, ensureUser, logout }
})
