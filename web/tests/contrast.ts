import type { Page } from './fixtures'

/**
 * 文本对比度采集器（WCAG 2.1 AA：普通文本 4.5:1，大文本 3:1）。
 *
 * 只做"可稳定判定"的检查，避免把误报混进基线：
 * - 排除禁用控件（WCAG 明确豁免）与有效 opacity < 0.9 的过渡态；
 * - 背景含渐变/图片、带 text-shadow 的元素记为 undetermined，不判违规；
 * - canvas / 伪元素里的文本无法读取，不参与统计。
 */
export interface ContrastViolation {
  selector: string
  text: string
  ratio: number
  required: number
  fontSize: number
  color: string
  background: string
}

export interface ContrastReport {
  checked: number
  undetermined: number
  violations: ContrastViolation[]
}

export async function collectContrast(page: Page): Promise<ContrastReport> {
  return page.evaluate(() => {
    type Rgba = [number, number, number, number]

    const parse = (value: string): Rgba | null => {
      if (!value || value === 'transparent' || value === 'rgba(0, 0, 0, 0)') return [0, 0, 0, 0]
      const scaled = value.startsWith('color(srgb ')
      const parts = value.match(/[\d.]+/g)?.map(Number)
      if (!parts || parts.length < 3) return null
      const factor = scaled ? 255 : 1
      return [parts[0]! * factor, parts[1]! * factor, parts[2]! * factor, parts[3] ?? 1]
    }
    const blend = (fg: Rgba, bg: Rgba): Rgba => {
      const alpha = fg[3]
      if (alpha >= 1) return fg
      return [fg[0] * alpha + bg[0] * (1 - alpha), fg[1] * alpha + bg[1] * (1 - alpha), fg[2] * alpha + bg[2] * (1 - alpha), 1]
    }
    const luminance = (color: Rgba): number => {
      const channel = (value: number): number => {
        const unit = value / 255
        return unit <= 0.04045 ? unit / 12.92 : ((unit + 0.055) / 1.055) ** 2.4
      }
      return 0.2126 * channel(color[0]) + 0.7152 * channel(color[1]) + 0.0722 * channel(color[2])
    }
    const contrast = (a: Rgba, b: Rgba): number => {
      const first = luminance(a), second = luminance(b)
      return (Math.max(first, second) + 0.05) / (Math.min(first, second) + 0.05)
    }
    const describe = (element: Element): string => {
      const testId = element.getAttribute('data-test')
      const className = typeof element.className === 'string' ? element.className.split(/\s+/).filter(Boolean).slice(0, 2).join('.') : ''
      return `${element.tagName.toLowerCase()}${testId ? `[data-test=${testId}]` : ''}${className ? `.${className}` : ''}`
    }
    const isVisible = (element: Element, style: CSSStyleDeclaration): boolean => {
      if (style.display === 'none' || style.visibility === 'hidden') return false
      const rect = element.getBoundingClientRect()
      return rect.width >= 1 && rect.height >= 1
    }
    const effectiveOpacity = (element: Element): number => {
      let opacity = 1
      for (let node: Element | null = element; node; node = node.parentElement) opacity *= Number(getComputedStyle(node).opacity)
      return opacity
    }
    const directText = (element: Element): string =>
      [...element.childNodes]
        .filter(node => node.nodeType === Node.TEXT_NODE && (node.textContent ?? '').trim())
        .map(node => (node.textContent ?? '').trim())
        .join(' ')

    const violations: { selector: string; text: string; ratio: number; required: number; fontSize: number; color: string; background: string }[] = []
    let checked = 0, undetermined = 0

    for (const element of [...document.body.querySelectorAll('*')]) {
      const text = directText(element)
      if (!text) continue
      const style = getComputedStyle(element)
      if (!isVisible(element, style)) continue
      // 禁用态对比度不受 WCAG 约束；EP 的 .is-disabled 与原生 disabled 一并排除。
      if (element.closest('button:disabled, input:disabled, select:disabled, textarea:disabled, [aria-disabled="true"], .is-disabled')) continue
      // 阴影与半透明过渡态无法稳定换算出真实前景色，记为无法判定而不是违规。
      if (style.textShadow && style.textShadow !== 'none') { undetermined++; continue }
      if (effectiveOpacity(element) < 0.9) { undetermined++; continue }
      const foreground = parse(style.color)
      if (!foreground) { undetermined++; continue }

      const layers: Rgba[] = []
      let undecided = false
      for (let node: Element | null = element; node; node = node.parentElement) {
        const nodeStyle = getComputedStyle(node)
        if (nodeStyle.backgroundImage !== 'none' && /url\(|gradient\(/.test(nodeStyle.backgroundImage)) { undecided = true; break }
        const color = parse(nodeStyle.backgroundColor)
        if (color && color[3] > 0) layers.push(color)
        if (color && color[3] >= 1) break
      }
      if (undecided) { undetermined++; continue }

      let background: Rgba = [255, 255, 255, 1]
      if (layers.length && layers[layers.length - 1]![3] >= 1) background = layers[layers.length - 1]!
      for (let index = layers.length - 2; index >= 0; index--) background = blend(layers[index]!, background)

      const fontSize = Number.parseFloat(style.fontSize)
      const weight = Number.parseInt(style.fontWeight, 10) || 400
      const large = fontSize >= 24 || (fontSize >= 18.66 && weight >= 700)
      const required = large ? 3 : 4.5
      const ratio = contrast(blend(foreground, background), background)
      checked++
      if (ratio + 0.01 < required) {
        violations.push({
          selector: describe(element),
          text: text.slice(0, 60),
          ratio: Math.round(ratio * 100) / 100,
          required,
          fontSize,
          color: style.color,
          background: `rgb(${Math.round(background[0])}, ${Math.round(background[1])}, ${Math.round(background[2])})`
        })
      }
    }
    return { checked, undetermined, violations }
  })
}