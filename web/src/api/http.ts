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
  const workstationId = localStorage.getItem('scada_workstation_id')
  if (workstationId != null) config.headers['X-Scada-Workstation-Id'] = workstationId
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

// 具体原因可能落在 message（自定义错误）、detail（ProblemDetails）或 errors（逐条校验）里。
// 只读 message/title 会把 ASP.NET 的通用英文短语（Bad Request、Service Unavailable、Conflict）显示给中文界面，
// 而 Results.Problem("…") 把中文原因放在 detail，errors 数组则来自批量保存/发布校验。
export function serverMessage(body: unknown): string {
  if (!body || typeof body !== 'object') return ''
  const data = body as { message?: unknown; detail?: unknown; title?: unknown; errors?: unknown }
  const text = (value: unknown) => (typeof value === 'string' && value.trim() ? value.trim() : '')
  const message = text(data.message)
  if (message) return message
  const detail = text(data.detail)
  if (detail) return detail
  if (Array.isArray(data.errors)) {
    const list = data.errors.filter((item): item is string => !!text(item))
    if (list.length) return list.join('；')
  }
  return text(data.title)
}

type RequestError = {
  response?: { status?: number; data?: unknown }
  code?: string
  message?: string
}

export function errorMessage(error: unknown, fallback = tr('http.k09e424')): string {
  const err = error as RequestError
  const data = err.response?.data as { traceId?: string } | undefined
  const server = serverMessage(data)
  // 兜底异常会在错误体里带追踪号，便于对着服务端日志定位。
  if (server) return data?.traceId ? server + tr('requestErrors.withTrace', [data.traceId]) : server
  // 服务端没有给出提示时补上状态码；axios 的英文兜底（Request failed with status code N）不直接展示。
  if (err.response) return fallback + tr('requestErrors.status', [err.response.status ?? '?'])
  // 无响应说明请求没到服务端，按超时/取消/网络故障分类，别把英文细节抛给用户。
  if (!axios.isAxiosError(error)) return err.message || fallback
  if (err.code === 'ECONNABORTED' || err.code === 'ETIMEDOUT') return tr('requestErrors.timeout')
  if (err.code === 'ERR_CANCELED') return fallback
  return tr('requestErrors.network')
}

// Blob downloads put the JSON error body on a Blob, which errorMessage cannot read.
export async function errorText(error: unknown, fallback = tr('http.k09e424')): Promise<string> {
  const data = (error as { response?: { data?: unknown } }).response?.data
  if (typeof Blob !== 'undefined' && data instanceof Blob) {
    try {
      const server = serverMessage(JSON.parse(await data.text()))
      if (server) return server
    } catch { /* A non-JSON body keeps the localized fallback. */ }
    return fallback
  }
  return errorMessage(error, fallback)
}
