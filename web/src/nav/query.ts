/** Accept only a single, positive integer from the URL query. */
export function positiveQueryId(value: unknown): number | undefined {
  if (typeof value !== 'string' || !/^[1-9]\d*$/.test(value)) return undefined
  const id = Number(value)
  return Number.isSafeInteger(id) ? id : undefined
}

/**
 * Only same-origin paths may be used as a post-login / post-password-change redirect.
 *
 * `location.href` is a browser navigation primitive: `//evil.com`, `/\evil.com`
 * and their percent-encoded variants are normalised into absolute URLs and leave
 * the site. Keep the rules in one place so every entry point shares them.
 */
export function safeRedirect(raw: unknown, landing: string): string {
  if (typeof raw !== 'string' || !raw.startsWith('/')) return landing
  if (raw.startsWith('//') || raw.startsWith('/\\') || raw.includes('\\') || raw.includes('://')) return landing
  if (raw.length > 2048 || /%2f|%5c/i.test(raw)) return landing
  if (/[\s\u0000-\u001f\u007f]/.test(raw)) return landing
  return raw
}
