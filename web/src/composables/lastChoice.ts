// 按当前登录用户记住最近一次选择，仅保存在本机浏览器，不进入服务端配置。
const PREFIX = 'scada_last_'

function storageKey(name: string): string {
  let user = ''
  try {
    user = (JSON.parse(localStorage.getItem('scada_user') ?? '{}') as { userName?: string }).userName?.toLocaleLowerCase() ?? ''
  } catch {
    user = ''
  }
  return `${PREFIX}${user}:${name}`
}

export function readLastNumber(name: string): number | null {
  try {
    const raw = localStorage.getItem(storageKey(name))
    const value = raw === null ? Number.NaN : Number(raw)
    return Number.isFinite(value) ? value : null
  } catch {
    return null
  }
}

export function readLastText(name: string): string {
  try {
    return localStorage.getItem(storageKey(name)) ?? ''
  } catch {
    return ''
  }
}

export function rememberNumber(name: string, value: number | null | undefined): void {
  try {
    if (value == null) localStorage.removeItem(storageKey(name))
    else localStorage.setItem(storageKey(name), String(value))
  } catch {
    // 本地存储不可用时静默跳过，不影响保存流程。
  }
}

export function rememberText(name: string, value: string | null | undefined): void {
  try {
    if (!value) localStorage.removeItem(storageKey(name))
    else localStorage.setItem(storageKey(name), value)
  } catch {
    // 本地存储不可用时静默跳过，不影响保存流程。
  }
}
