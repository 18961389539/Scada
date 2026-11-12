import { expect, type Page } from '@playwright/test'

/**
 * 界面巡检采集：只依赖 DOM 事实，不绑定具体组件实现。
 *
 * 采集项对应交付验收里反复出现的人工检查点，改成断言后就不必每次发版前
 * 再逐页目视：无名称按钮、无标签字段、重复 id、整页横向溢出、缺少唯一标题。
 */
export interface UiAuditSnapshot {
  /** 根元素横向溢出像素；>1 视为整页出现横向滚动 */
  horizontalOverflow: number
  /** 可见但没有任何可访问名称的按钮 */
  unnamedButtons: string[]
  /** 可见但既无名称也无占位提示的表单控件 */
  unlabeledFields: string[]
  /** 仅有占位提示、没有真正标签的字段（棘轮项，当前不阻断） */
  placeholderOnlyFields: string[]
  /** 同一文档内重复的 id */
  duplicateIds: string[]
  h1Count: number
  mainCount: number
}

export interface UiAuditRow extends UiAuditSnapshot {
  path: string
  viewport: string
}

const MAX_DETAIL = 6

/**
 * 采集当前文档。`labelScope` 只收窄“无名称/无标签”的检查范围，
 * 重复 id、标题数、横向溢出始终按整篇文档统计——弹窗里的重复 id
 * 正是最容易漏掉的一类。
 */
export async function collectUiAudit(page: Page, labelScope = 'body'): Promise<UiAuditSnapshot> {
  return page.evaluate((selector) => {
    const scope = document.querySelector(selector) ?? document.body
    const visible = (element: Element): boolean => {
      const rect = element.getBoundingClientRect()
      const style = getComputedStyle(element)
      return rect.width > 0 && rect.height > 0 && style.visibility !== 'hidden' && style.display !== 'none'
    }
    const normalized = (value: string | null | undefined): string => (value ?? '').replace(/\s+/g, ' ').trim()
    /**
     * 读取元素里的可见文本。图标字体常把字形放在 `aria-hidden` 的 span 里，
     * 这类字符不会被读屏播报，也就不能当作按钮名称——否则真正的“无名称按钮”
     * 会被一个装饰性字符掩盖过去。
     */
    const textOf = (element: Element | null): string => {
      if (!element) return ''
      const walker = document.createTreeWalker(element, NodeFilter.SHOW_TEXT)
      const parts: string[] = []
      for (let node = walker.nextNode(); node; node = walker.nextNode()) {
        const parent = (node as Text).parentElement
        if (!parent) continue
        const hidden = parent.closest('[aria-hidden="true"]')
        if (hidden && element.contains(hidden)) continue
        parts.push(node.textContent ?? '')
      }
      return normalized(parts.join(' '))
    }
    const labelText = (element: Element | null): string => normalized(element?.textContent)
    const accessibleName = (element: Element): string => {
      const referenced = (element.getAttribute('aria-labelledby') ?? '')
        .split(/\s+/)
        .filter(Boolean)
        .map(id => labelText(document.getElementById(id)))
        .join(' ')
      const direct = normalized(
        referenced ||
        element.getAttribute('aria-label') ||
        element.getAttribute('title')
      )
      if (direct) return direct
      const content = textOf(element)
      if (content) return content
      const field = element as HTMLInputElement
      if (element.tagName === 'INPUT' && /^(submit|button|reset)$/i.test(field.type)) return normalized(field.value)
      if (element.tagName === 'IMG') return normalized(element.getAttribute('alt'))
      const labels = field.labels ? [...field.labels].map(label => textOf(label)).join(' ') : ''
      return normalized(labels)
    }
    const describe = (element: Element, kind: string): string => {
      const testId = element.getAttribute('data-test')
      const className = typeof element.className === 'string' ? element.className.slice(0, 60) : ''
      return `${kind}${testId ? `[data-test=${testId}]` : ''}${className ? ` .${className.split(/\s+/).filter(Boolean).slice(0, 2).join('.')}` : ''}`
    }

    const unnamedButtons = [...scope.querySelectorAll('button')]
      .filter(visible)
      .filter(element => !accessibleName(element))
      .map(element => describe(element, 'button'))

    const fields = [...scope.querySelectorAll('input, select, textarea')]
      .filter(visible)
      .filter(element => (element as HTMLInputElement).type !== 'hidden')
    const unlabeledFields = fields
      .filter(element => !accessibleName(element) && !element.getAttribute('placeholder'))
      .map(element => describe(element, `${element.tagName.toLowerCase()}[${(element as HTMLInputElement).type ?? ''}]`))
    const placeholderOnlyFields = fields
      .filter(element => !accessibleName(element) && !!element.getAttribute('placeholder'))
      .map(element => describe(element, `${element.tagName.toLowerCase()}[${(element as HTMLInputElement).type ?? ''}]`))

    const ids = [...document.querySelectorAll('[id]')].map(element => element.id).filter(Boolean)
    const duplicateIds = [...new Set(ids.filter((id, index) => ids.indexOf(id) !== index))]
    const root = document.scrollingElement ?? document.documentElement
    return {
      horizontalOverflow: Math.max(0, root.scrollWidth - root.clientWidth),
      unnamedButtons,
      unlabeledFields,
      placeholderOnlyFields,
      duplicateIds,
      h1Count: document.querySelectorAll('h1').length,
      mainCount: document.querySelectorAll('main, [role="main"]').length
    }
  }, labelScope)
}

/** 把采集结果转成可直接读的失败信息；空数组代表通过。 */
export function auditViolations(rows: UiAuditRow[]): string[] {
  const issues: string[] = []
  const list = (items: string[]): string => `${items.slice(0, MAX_DETAIL).join(' | ')}${items.length > MAX_DETAIL ? ` …(+${items.length - MAX_DETAIL})` : ''}`
  for (const row of rows) {
    const where = `${row.viewport} ${row.path}`
    if (row.horizontalOverflow > 1) issues.push(`${where}: 整页横向溢出 ${row.horizontalOverflow}px`)
    if (row.unnamedButtons.length) issues.push(`${where}: ${row.unnamedButtons.length} 个按钮没有可访问名称 → ${list(row.unnamedButtons)}`)
    if (row.unlabeledFields.length) issues.push(`${where}: ${row.unlabeledFields.length} 个字段没有标签 → ${list(row.unlabeledFields)}`)
    if (row.duplicateIds.length) issues.push(`${where}: 重复 id → ${list(row.duplicateIds)}`)
    if (row.h1Count !== 1) issues.push(`${where}: 期望 1 个 h1，实际 ${row.h1Count}`)
    // 页面里再嵌一层 main 会让读屏把内容切成两块主区域，HTML 本身也不允许
    if (row.mainCount !== 1) issues.push(`${where}: 期望 1 个 main 地标，实际 ${row.mainCount}`)
  }
  return issues
}

export function expectUiAuditClean(rows: UiAuditRow[], label = '界面巡检'): void {
  const issues = auditViolations(rows)
  expect(issues, `${label}发现 ${issues.length} 项问题：\n${issues.join('\n')}`).toEqual([])
}
