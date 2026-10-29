import { test, expect, type APIRequestContext, type Page } from '@playwright/test'
import { writeFileSync } from 'node:fs'
import type { ScreenDocument } from '../src/screens/model'

/**
 * 交付基线巡检：配置管理 / 报表 / 生产管理页面。
 * 只读巡检，不修改数据；输出 test-results/ui-delivery-audit.json 供验收引用。
 */
const PAGES = [
  { module: 'config', path: '/config/devices', name: '设备配置' },
  { module: 'config', path: '/config/gateways', name: '采集网关' },
  { module: 'config', path: '/config/hmi-library', name: '图库与模板' },
  { module: 'config', path: '/config/layouts', name: '运行布局与工作站' },
  { module: 'config', path: '/config/object-templates', name: '设备对象与模板' },
  { module: 'config', path: '/config/object-templates/1', name: '设备模板编辑' },
  { module: 'config', path: '/config/tags', name: '测点配置' },
  { module: 'config', path: '/config/project', name: '工程管理' },
  { module: 'config', path: '/config/calculations', name: '计算测点' },
  { module: 'config', path: '/config/oee', name: 'OEE 配置' },
  { module: 'config', path: '/config/rules', name: '报警规则' },
  { module: 'config', path: '/config/notifications', name: '报警通知' },
  { module: 'config', path: '/config/annunciators', name: '声光联动' },
  { module: 'config', path: '/config/external-api', name: '对外接口' },
  { module: 'report', path: '/reports', name: '历史报表查询' },
  { module: 'report', path: '/report-center', name: '报表归档与验签' },
  { module: 'production', path: '/recipes', name: '配方管理' },
  { module: 'production', path: '/traceability', name: '生产追溯' },
  { module: 'production', path: '/oee', name: 'OEE 生产统计' },
  { module: 'production', path: '/calc-jobs', name: '计算任务' }
]

async function createPublishedScreen(request: APIRequestContext): Promise<number> {
  const auth = await (await request.post('/api/auth/login', { data: { userName: 'admin', password: 'admin123' } })).json()
  const headers = { Authorization: `Bearer ${auth.token}` }
  const name = `交付巡检画面-${Date.now()}`
  const created = await request.post('/api/screens', { headers, data: { name } })
  expect(created.status(), await created.text()).toBe(201)
  const screen = await created.json()
  const document: ScreenDocument = { schemaVersion: 1, width: 1200, height: 720, background: '#101b2d', nodes: [] }
  const saved = await request.put(`/api/screens/${screen.id}`, { headers, data: { name, revision: screen.revision, document } })
  expect(saved.ok(), await saved.text()).toBeTruthy()
  const published = await request.post(`/api/screens/${screen.id}/publish`, { headers, data: { revision: (await saved.json()).revision } })
  expect(published.ok(), await published.text()).toBeTruthy()
  return screen.id as number
}

const VIEWPORTS = [
  { label: 'desktop-1600', width: 1600, height: 1000 },
  { label: 'laptop-1280', width: 1280, height: 800 },
  { label: 'tablet-768', width: 768, height: 1024 },
  { label: 'phone-390', width: 390, height: 844 }
]

async function stubTemplate(page: Page): Promise<void> {
  await page.route('**/api/object-templates/1', (route) => {
    if (route.request().method() !== 'GET') return route.continue()
    return route.fulfill({
      json: {
        name: '交付巡检模板',
        revision: 1,
        latestVersion: 1,
        versions: [],
        document: {
          tags: [],
          alarms: [],
          bindings: {},
          actionBindings: {},
          panel: { width: 800, height: 600, nodes: [] }
        }
      }
    })
  })
}

async function signIn(page: Page): Promise<void> {
  const response = await page.request.post('/api/auth/login', { data: { userName: 'admin', password: 'admin123' } })
  expect(response.ok()).toBeTruthy()
  const auth = await response.json()
  await page.addInitScript(({ token, user }) => {
    localStorage.setItem('scada_token', token)
    localStorage.setItem('scada_user', JSON.stringify(user))
  }, auth)
}

interface AuditResult {
  module: string
  name: string
  path: string
  viewport: string
  consoleErrors: string[]
  pageErrors: string[]
  httpErrors: string[]
  horizontalOverflow: number
  unnamedButtons: string[]
  unlabeledFields: string[]
  duplicateIds: string[]
  hasH1: boolean
  hasMain: boolean
  title: string
  loadMs: number
  domContentLoadedMs: number
  jsTransferBytes: number
}

async function audit(page: Page, item: (typeof PAGES)[number], viewport: string): Promise<AuditResult> {
  const consoleErrors: string[] = []
  const pageErrors: string[] = []
  const httpErrors: string[] = []
  const onConsole = (msg: { type: () => string; text: () => string }): void => {
    if (msg.type() === 'error') consoleErrors.push(msg.text().slice(0, 300))
  }
  const onPageError = (error: Error): void => { pageErrors.push(error.message.slice(0, 300)) }
  const onResponse = (response: { status: () => number; url: () => string }): void => {
    const status = response.status()
    if (status >= 400) httpErrors.push(`${status} ${response.url().slice(0, 160)}`)
  }
  page.on('console', onConsole)
  page.on('pageerror', onPageError)
  page.on('response', onResponse)
  try {
    await page.goto(item.path, { waitUntil: 'domcontentloaded' })
    await page.waitForTimeout(800)
    const dom = await page.evaluate(() => {
      const visible = (el: Element): boolean => {
        const rect = el.getBoundingClientRect()
        const style = getComputedStyle(el)
        return rect.width > 0 && rect.height > 0 && style.visibility !== 'hidden' && style.display !== 'none'
      }
      const accessibleName = (el: Element): string => {
        const labelledBy = el.getAttribute('aria-labelledby')
        if (labelledBy) {
          const text = labelledBy.split(/\s+/).map(id => document.getElementById(id)?.textContent?.trim() ?? '').join(' ')
          if (text) return text
        }
        return (el.getAttribute('aria-label') ?? el.getAttribute('title') ?? el.textContent ?? '').trim()
      }
      const unnamedButtons = [...document.querySelectorAll('button')]
        .filter(visible)
        .filter(el => !accessibleName(el))
        .map(el => el.className.slice(0, 80))
      const unlabeledFields = [...document.querySelectorAll('input, select, textarea')]
        .filter(visible)
        .filter(el => {
          const input = el as HTMLInputElement
          if (input.type === 'hidden') return false
          if (accessibleName(el) || el.getAttribute('placeholder')) return false
          if ([...(input.labels ?? [])].some(label => label.textContent?.trim())) return false
          const id = el.getAttribute('id')
          return !(id && document.querySelector(`label[for="${CSS.escape(id)}"]`))
        })
        .map(el => {
          const field = el as HTMLInputElement
          const dialog = el.closest('.el-dialog')?.querySelector('.el-dialog__title')?.textContent?.trim()
          const formItem = el.closest('.el-form-item')?.querySelector('.el-form-item__label')?.textContent?.trim()
          return `${el.tagName.toLowerCase()}[${field.type ?? ''}] role=${el.getAttribute('role') ?? '-'} dialog="${dialog ?? ''}" label="${formItem ?? ''}" name="${field.name ?? ''}" class="${field.className.slice(0, 40)}"`
        })
      const ids = [...document.querySelectorAll('[id]')].map(el => el.id).filter(Boolean)
      const duplicateIds = [...new Set(ids.filter((id, i) => ids.indexOf(id) !== i))]
      const root = document.scrollingElement ?? document.documentElement
      const navigation = performance.getEntriesByType('navigation')[0] as PerformanceNavigationTiming | undefined
      return {
        horizontalOverflow: Math.max(0, root.scrollWidth - root.clientWidth),
        unnamedButtons,
        unlabeledFields,
        duplicateIds,
        hasH1: !!document.querySelector('h1'),
        hasMain: !!document.querySelector('main, [role="main"]'),
        title: document.title,
        loadMs: Math.round(navigation?.loadEventEnd ?? 0),
        domContentLoadedMs: Math.round(navigation?.domContentLoadedEventEnd ?? 0),
        jsTransferBytes: performance.getEntriesByType('resource')
          .filter(entry => (entry as PerformanceResourceTiming).initiatorType === 'script')
          .reduce((total, entry) => total + ((entry as PerformanceResourceTiming).transferSize || 0), 0)
      }
    })
    return {
      module: item.module,
      name: item.name,
      path: item.path,
      viewport,
      consoleErrors,
      pageErrors,
      httpErrors,
      ...dom
    }
  } finally {
    page.off('console', onConsole)
    page.off('pageerror', onPageError)
    page.off('response', onResponse)
  }
}

test('delivery audit: config / report / production pages', async ({ page }, testInfo) => {
  test.setTimeout(300000)
  await signIn(page)
  await stubTemplate(page)
  const results: AuditResult[] = []
  for (const viewport of VIEWPORTS) {
    await page.setViewportSize({ width: viewport.width, height: viewport.height })
    for (const item of PAGES) {
      results.push(await audit(page, item, viewport.label))
      if (viewport.label === 'desktop-1600') {
        await page.screenshot({ path: `test-results/ui-audit/${item.path.replace(/\//g, '_') || 'root'}.png`, animations: 'disabled' })
      }
    }
  }
  writeFileSync('test-results/ui-delivery-audit.json', JSON.stringify(results, null, 2))
  await testInfo.attach('ui-delivery-audit.json', {
    body: JSON.stringify(results, null, 2),
    contentType: 'application/json'
  })
  const broken = results.filter(r => r.pageErrors.length || r.consoleErrors.length)
  expect(broken, JSON.stringify(broken, null, 2)).toHaveLength(0)
  const titles = results.filter(r => r.viewport === 'desktop-1600').map(r => r.title)
  expect(titles.every(title => title && title.includes('· SCADA')), JSON.stringify(titles, null, 2)).toBe(true)
  expect(new Set(titles).size, JSON.stringify(titles, null, 2)).toBe(titles.length)
})
test('delivery audit: runtime / system / full-screen pages', async ({ page, request }, testInfo) => {
  test.setTimeout(300000)
  const screenId = await createPublishedScreen(request)
  const pages = [
    { module: 'runtime', path: '/overview', name: '总览' },
    { module: 'runtime', path: '/realtime', name: '实时监视' },
    { module: 'runtime', path: '/screens', name: '工艺画面' },
    { module: 'runtime', path: `/screens/${screenId}`, name: '画面运行' },
    { module: 'runtime', path: `/config/screens/${screenId}`, name: '画面组态' },
    { module: 'runtime', path: '/control', name: '现场操作' },
    { module: 'runtime', path: '/trends', name: '趋势曲线' },
    { module: 'runtime', path: '/alarms', name: '报警' },
    { module: 'runtime', path: '/alarm-analytics', name: '报警分析' },
    { module: 'runtime', path: '/ops', name: '运维与高可用' },
    { module: 'runtime', path: '/system', name: '系统管理' },
    { module: 'runtime', path: '/console', name: '运行布局控制台' },
    { module: 'runtime', path: `/kiosk/screens/${screenId}`, name: '大屏展示' }
  ]
  await signIn(page)
  const results: AuditResult[] = []
  for (const viewport of VIEWPORTS) {
    await page.setViewportSize({ width: viewport.width, height: viewport.height })
    for (const item of pages) {
      results.push(await audit(page, item, viewport.label))
      if (viewport.label === 'desktop-1600') {
        await page.screenshot({ path: `test-results/ui-audit/runtime${item.path.replace(/\//g, '_')}.png`, animations: 'disabled' })
      }
    }
  }
  writeFileSync('test-results/ui-runtime-audit.json', JSON.stringify(results, null, 2))
  await testInfo.attach('ui-runtime-audit.json', { body: JSON.stringify(results, null, 2), contentType: 'application/json' })
  const broken = results.filter(r => r.pageErrors.length || r.consoleErrors.length)
  expect(broken, JSON.stringify(broken, null, 2)).toHaveLength(0)
  const titles = results.filter(r => r.viewport === 'desktop-1600').map(r => r.title)
  expect(titles.every(title => !!title), JSON.stringify(titles, null, 2)).toBe(true)
})

test('delivery audit: login page', async ({ page }) => {
  test.setTimeout(120000)
  const errors: string[] = []
  page.on('console', message => { if (message.type() === 'error') errors.push(message.text().slice(0, 300)) })
  page.on('pageerror', error => errors.push(error.message.slice(0, 300)))
  for (const viewport of VIEWPORTS) {
    await page.setViewportSize({ width: viewport.width, height: viewport.height })
    await page.goto('/login', { waitUntil: 'domcontentloaded' })
    await page.waitForTimeout(500)
    const overflow = await page.evaluate(() => {
      const root = document.scrollingElement ?? document.documentElement
      return Math.max(0, root.scrollWidth - root.clientWidth)
    })
    expect(overflow, `${viewport.label} horizontal overflow`).toBe(0)
  }
  expect(errors, JSON.stringify(errors, null, 2)).toHaveLength(0)
  await page.screenshot({ path: 'test-results/ui-audit/runtime_login.png', animations: 'disabled' })
})

test('delivery audit: key pages expose a visible heading and main region', async ({ page }) => {
  test.setTimeout(120000)
  await signIn(page)
  for (const path of ['/overview', '/realtime', '/alarms', '/screens', '/ops', '/system']) {
    await page.goto(path, { waitUntil: 'domcontentloaded' })
    await page.waitForTimeout(400)
    await expect(page.locator('h1'), path).toBeVisible()
    await expect(page.locator('main, [role="main"]').first(), path).toBeVisible()
  }
})
