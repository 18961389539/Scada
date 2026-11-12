/** Accept only a single, positive integer from the URL query. */
export function positiveQueryId(value: unknown): number | undefined {
  if (typeof value !== 'string' || !/^[1-9]\d*$/.test(value)) return undefined
  const id = Number(value)
  return Number.isSafeInteger(id) ? id : undefined
}

/** 只接受本站绝对路径；协议相对地址（双斜杠开头）与反斜杠变体一律拒绝。 */
function isLocalPath(raw: string): boolean {
  if (!raw.startsWith('/')) return false
  if (raw.startsWith('//') || raw.startsWith('/\\') || raw.includes('\\') || raw.includes('://')) return false
  if (raw.length > 2048) return false
  if (/[\s\u0000-\u001f\u007f]/.test(raw)) return false
  return true
}

/**
 * Only same-origin paths may be used as a post-login / post-password-change redirect.
 *
 * `location.href` is a browser navigation primitive: `//evil.com`, `/\evil.com`
 * and their percent-encoded variants are normalised into absolute URLs and leave
 * the site. Keep the rules in one place so every entry point shares them.
 */
export function safeRedirect(raw: unknown, landing: string): string {
  if (typeof raw !== 'string' || !isLocalPath(raw)) return landing
  if (/%2f|%5c/i.test(raw)) return landing
  return raw
}

/**
 * 会话过期时要带回登录页的地址（path + query + hash）。
 *
 * 401/428 是整页跳转：不带上当前地址，用户重新登录后只能落回默认首页，
 * 之前选好的设备、时间范围和筛选条件全部要重填。
 * 登录页/改密页自身不参与回跳，否则刷新一次就会叠出 `login?redirect=login?…`。
 * 调用方负责在拼接 URL 时再做一次 encodeURIComponent。
 */
export function currentRedirectTarget(): string {
  const path = location.pathname + location.search + location.hash
  if (/^\/(?:login|change-password)(?:[?#]|$)/.test(path)) return ''
  return isLocalPath(path) ? path : ''
}
