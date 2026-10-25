import axios from 'axios'
import { tr } from '../i18n'

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
  // 本机登记的工作站：服务端据此执行“只监视工位不能下发”的策略。
  const workstation = localStorage.getItem('scada_workstation')
  if (workstation) config.headers['X-Scada-Workstation'] = workstation
  // 界面语言：服务端据此翻译错误提示与报表表头。
  const locale = localStorage.getItem('scada_locale') || 'zh-CN'
  config.headers['X-Scada-Locale'] = locale
  // 移动端：服务端按 Mobile 配置/角色决定是否允许下发。
  if (window.matchMedia?.('(max-width: 768px), (pointer: coarse) and (max-width: 1024px)').matches) config.headers['X-Scada-Client'] = 'mobile'
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

export function errorMessage(error: unknown, fallback = tr('http.k09e424')): string {
  const err = error as { response?: { data?: { message?: string; title?: string } }; message?: string }
  return err.response?.data?.message ?? err.response?.data?.title ?? err.message ?? fallback
}
