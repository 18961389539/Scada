/** 文本写值的当前值若服务端没有就标明不可用，不要把数值当前值当成文本展示。 */
export function controlConfirmCurrentLabel(kind: 'number' | 'text', current: number | null | undefined, unavailable: string): string {
  if (kind === 'text' || current == null) return unavailable
  return String(current)
}

export function controlConfirmKind(target: number | string): 'number' | 'text' {
  return typeof target === 'string' ? 'text' : 'number'
}
