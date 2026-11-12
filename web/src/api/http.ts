import axios from 'axios'
import { tr } from '../i18n'
import { currentRedirectTarget } from '../nav/query'

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

// 401/428 是整页跳转：把当前地址带进 redirect，重新认证后能回到原页面（含查询参数与锚点），
// 而不是每次都退回默认首页、丢掉已选的设备和时间范围。认证页自身不参与回跳（见 currentRedirectTarget）。
function goToAuthPage(path: string, options?: { expired?: boolean }): void {
  const from = currentRedirectTarget()
  const query = new URLSearchParams()
  if (from) query.set('redirect', from)
  // 会话过期是被动登出：登录页要说明原因，否则用户只看到一张空白表单。
  if (options?.expired) query.set('expired', '1')
  const text = query.toString()
  location.href = text ? `${path}?${text}` : path
}

http.interceptors.response.use(
  (response) => response,
  (error) => {
    if (error.response?.status === 401) {
      localStorage.removeItem(TOKEN_KEY)
      localStorage.removeItem(USER_KEY)
      if (!location.pathname.startsWith('/login')) {
        goToAuthPage('/login', { expired: true })
      }
    }
    // 428 = 还没改出厂口令。路由守卫已经会拦，这里兜住「缓存的用户信息还没带标记」的情况。
    if (error.response?.status === 428 && !location.pathname.startsWith('/change-password')) {
      const raw = localStorage.getItem(USER_KEY)
      if (raw) {
        try {
          const stored = JSON.parse(raw) as { mustChangePassword?: boolean }
          stored.mustChangePassword = true
          localStorage.setItem(USER_KEY, JSON.stringify(stored))
        } catch { /* 缓存坏了就只跳转，改密页会再向服务端确认 */ }
      }
      goToAuthPage('/change-password')
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

export interface ItemPage<T> { items: T[]; more: boolean; moreKnown: boolean; next: number | null }

// 服务端用响应头标出还有没有更早的一页。旧的模拟响应没有这个头时，满页就当作还有。
export function readPage<T>(response: { data: T[]; headers: unknown }): ItemPage<T> {
  const text = (name: string) => {
    const headers = response.headers
    let value: unknown
    if (headers && typeof headers === 'object' && typeof (headers as { get?: unknown }).get === 'function') value = (headers as { get(name: string): unknown }).get(name)
    else if (headers && typeof headers === 'object') {
      const record = headers as Record<string, unknown>
      value = record[name] ?? record[name.toLowerCase()]
    }
    return value == null ? '' : String(Array.isArray(value) ? value[0] : value)
  }
  const moreText = text('x-scada-more')
  const nextText = text('x-scada-next')
  const next = nextText === '' ? null : Number(nextText)
  return { items: response.data, more: moreText === '1', moreKnown: moreText === '0' || moreText === '1', next: Number.isFinite(next) ? next : null }
}

export function continueFrom<T extends { id: number }>(page: ItemPage<T>, pageSize: number): { more: boolean; next: number | null } {
  const more = page.moreKnown ? page.more : page.items.length >= pageSize
  if (!more) return { more: false, next: null }
  const next = page.next ?? page.items[page.items.length - 1]?.id ?? null
  return { more: next != null, next }
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

// 下发控制命令和「保存配置」不是一回事，失败措辞不能共用。
// errorMessage() 对超时/断网的兜底是「请稍后重试」——保存配置这样提示没问题，
// 但命令可能已经写进现场设备了，只是回执没回来；照着「重试」做就会重复下发一次。
// 所以：服务端回了错误码 = 命令被拒绝、现场没被改；完全没有响应 = 结果未知。
export function commandErrorMessage(error: unknown, fallback = tr('http.k09e424')): string {
  const err = error as RequestError
  if (!err?.response) return tr('commandUx.outcomeUnknown')
  return errorMessage(error, fallback)
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
