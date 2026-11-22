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
  /** 声明了 role="button" 却没有 tabindex 的自绘控件：鼠标能点，键盘 Tab 到不了 */
  roleButtonsWithoutTabindex: string[]
  /** tabindex 大于 0 会打乱整页 Tab 顺序 */
  positiveTabindex: string[]
  /** aria-hidden 容器里的可聚焦元素：读屏看不见，键盘却能 Tab 进去 */
  focusableInAriaHidden: string[]
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
      // 附带 outerHTML 片段：巡检报错必须能直接定位元素，否则只报"1 个 role=button"无法修复。
      const snippet = element.outerHTML.slice(0, 90).replace(/\s+/g, ' ')
      return `${kind}${testId ? `[data-test=${testId}]` : ''}${className ? ` .${className.split(/\s+/).filter(Boolean).slice(0, 2).join('.')}` : ''} <${snippet}>`
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

    // 键盘可达性：只查 DOM 能稳定判定的事实，避免绑定具体组件。
    // 原生 button 自带 tabindex；Element Plus 组件内部（如 el-input-number 的增减按钮）由组件自身
    // 接管键盘，不算我们漏了 tabindex——只查页面自绘的 role=button。
    const isElementPlusWidget = (element: Element): boolean =>
      /(^|\s)el-/.test(typeof element.className === 'string' ? element.className : '')
    const roleButtonsWithoutTabindex = [...scope.querySelectorAll('[role="button"]')]
      .filter(visible)
      .filter(element => element.tagName !== 'BUTTON' && !element.hasAttribute('tabindex'))
      .filter(element => !isElementPlusWidget(element))
      .map(element => describe(element, 'role=button'))
    const positiveTabindex = [...scope.querySelectorAll('[tabindex]')]
      .filter(visible)
      .filter(element => Number(element.getAttribute('tabindex')) > 0)
      .map(element => describe(element, `tabindex=${element.getAttribute('tabindex')}`))
    const focusableInAriaHidden = [...scope.querySelectorAll('[aria-hidden="true"]')]
      .filter(visible)
      .flatMap(hidden => [
        ...hidden.querySelectorAll('button, a[href], input, select, textarea, [tabindex]:not([tabindex="-1"])')
      ])
      .filter(visible)
      // Element Plus 的 tooltip/popover 会把内容放进 aria-hidden 容器，元素本身仍由组件管理焦点，
      // 与"我们的代码把可聚焦元素藏起来"不是一回事，按与 role=button 相同的规则排除。
      .filter(element => !isElementPlusWidget(element))
      .map(element => describe(element, 'aria-hidden-focusable'))

    const root = document.scrollingElement ?? document.documentElement
    return {
      horizontalOverflow: Math.max(0, root.scrollWidth - root.clientWidth),
      unnamedButtons,
      unlabeledFields,
      placeholderOnlyFields,
      roleButtonsWithoutTabindex,
      positiveTabindex,
      focusableInAriaHidden,
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
    if (row.placeholderOnlyFields.length) issues.push(`${where}: ${row.placeholderOnlyFields.length} 个字段只有 placeholder 没有可访问名称 → ${list(row.placeholderOnlyFields)}`)
    if (row.roleButtonsWithoutTabindex.length) issues.push(`${where}: ${row.roleButtonsWithoutTabindex.length} 个 role=button 缺少 tabindex，键盘无法到达 → ${list(row.roleButtonsWithoutTabindex)}`)
    if (row.positiveTabindex.length) issues.push(`${where}: ${row.positiveTabindex.length} 处 tabindex>0 打乱 Tab 顺序 → ${list(row.positiveTabindex)}`)
    if (row.focusableInAriaHidden.length) issues.push(`${where}: ${row.focusableInAriaHidden.length} 个可聚焦元素藏在 aria-hidden 容器里 → ${list(row.focusableInAriaHidden)}`)
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
