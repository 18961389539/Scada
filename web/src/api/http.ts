import axios from 'axios'

export const TOKEN_KEY = 'scada_token'
export const USER_KEY = 'scada_user'

export const http = axios.create({
  baseURL: '/api',
  timeout: 30000
})

http.interceptors.request.use((config) => {
  const token = localStorage.getItem(TOKEN_KEY)
  if (token) {
    config.headers.Authorization = `Bearer ${token}`
  }
  return config
})

http.interceptors.response.use(
  (response) => response,
  (error) => {
    if (error.response?.status === 401) {
      localStorage.removeItem(TOKEN_KEY)
      localStorage.removeItem(USER_KEY)
      if (!location.pathname.startsWith('/login')) {
        location.href = '/login'
      }
    }
    return Promise.reject(error)
  }
)

export function errorMessage(error: unknown, fallback = '操作失败'): string {
  const err = error as { response?: { data?: { message?: string; title?: string } }; message?: string }
  return err.response?.data?.message ?? err.response?.data?.title ?? err.message ?? fallback
}
