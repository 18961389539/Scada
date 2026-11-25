/** 文本写值的当前值若服务端没有就标明不可用，不要把数值当前值当成文本展示。质量不是合格时写明通讯中断或质量无效。 */
export function controlConfirmCurrentLabel(kind: 'number' | 'text', current: number | null | undefined, unavailable: string, qualityReason?: string | null): string {
  if (kind === 'text' || current == null) return qualityReason || unavailable
  return String(current)
}

export function controlConfirmKind(target: number | string): 'number' | 'text' {
  return typeof target === 'string' ? 'text' : 'number'
}
