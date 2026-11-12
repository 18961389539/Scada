import { test, expect, type Page } from './fixtures'
import { writeFileSync } from 'node:fs'
import { auditViolations, collectUiAudit, expectUiAuditClean, type UiAuditRow } from './ui-audit'

/**
 * 界面巡检门槛：`ui-delivery-audit.spec.ts` 负责全量记录与截图，
 * 这里只保留“发版前必须为 0”的硬指标，覆盖高频页面与新增弹窗。
 *
 * 报表仍在 test-results/ui-audit-gate.json；断言失败时直接给出页面、
 * 视口和具体元素，不需要再翻截图。
 */
const VIEWPORTS = [
  { label: 'desktop-1440', width: 1440, height: 900 },
  { label: 'phone-390', width: 390, height: 844 }
]

const PAGES = [
  '/overview',
  '/realtime',
  '/alarms',
  '/trends',
  '/screens',
  '/config/tags',
  '/config/devices',
  '/system'
]

const DIALOGS = [
  { path: '/config/tags', open: 'tag-create' },
  { path: '/config/devices', open: 'device-create' },
  { path: '/config/rules', open: 'rule-create' },
  { path: '/config/external-api', open: 'ext-add' }
]

async function signIn(page: Page): Promise<void> {
  const response = await page.request.post('/api/auth/login', { data: { userName: 'admin', password: 'admin123' } })
  expect(response.ok()).toBeTruthy()
  const auth = await response.json()
  await page.addInitScript(({ token, user }) => {
    localStorage.setItem('scada_token', token)
    localStorage.setItem('scada_user', JSON.stringify(user))
  }, auth)
}

test('ui audit gate: high-traffic pages stay clean at desktop and phone widths', async ({ page }, testInfo) => {
  test.setTimeout(240000)
  await signIn(page)
  const rows: UiAuditRow[] = []
  for (const viewport of VIEWPORTS) {
    await page.setViewportSize({ width: viewport.width, height: viewport.height })
    for (const path of PAGES) {
      await page.goto(path, { waitUntil: 'domcontentloaded' })
      await expect(page.locator('main, [role="main"]').first(), path).toBeVisible()
      await page.waitForTimeout(600)
      rows.push({ path, viewport: viewport.label, ...(await collectUiAudit(page)) })
    }
  }
  writeFileSync('test-results/ui-audit-gate.json', JSON.stringify(rows, null, 2))
  await testInfo.attach('ui-audit-gate.json', { body: JSON.stringify(rows, null, 2), contentType: 'application/json' })
  expectUiAuditClean(rows, '高频页面巡检')
})

test('ui audit gate: create dialogs expose names, labels and unique ids', async ({ page }, testInfo) => {
  test.setTimeout(180000)
  await signIn(page)
  await page.setViewportSize({ width: 1440, height: 900 })
  const rows: UiAuditRow[] = []
  for (const item of DIALOGS) {
    await page.goto(item.path, { waitUntil: 'domcontentloaded' })
    await expect(page.locator('main, [role="main"]').first(), item.path).toBeVisible()
    await page.locator(`[data-test="${item.open}"]`).click()
    const dialog = page.getByRole('dialog')
    await expect(dialog, `${item.path} ${item.open}`).toBeVisible()
    // 弹窗本身要能被读屏播报，否则表单字段再完整也定位不到
    await expect(dialog).toHaveAccessibleName(/.+/)
    await page.waitForTimeout(300)
    rows.push({ path: `${item.path} [${item.open}]`, viewport: 'desktop-1440', ...(await collectUiAudit(page, '[role="dialog"]')) })
    await page.keyboard.press('Escape')
    await expect(dialog).toBeHidden()
  }
  writeFileSync('test-results/ui-audit-dialogs.json', JSON.stringify(rows, null, 2))
  await testInfo.attach('ui-audit-dialogs.json', { body: JSON.stringify(rows, null, 2), contentType: 'application/json' })
  expectUiAuditClean(rows, '新增弹窗巡检')
})

test('ui audit gate: collector still detects the problems it claims to catch', async ({ page }) => {
  test.setTimeout(120000)
  await signIn(page)
  await page.goto('/overview', { waitUntil: 'domcontentloaded' })
  await expect(page.locator('main, [role="main"]').first()).toBeVisible()
  await page.waitForTimeout(400)
  const clean = await collectUiAudit(page)
  expect(auditViolations([{ path: '/overview', viewport: 'probe', ...clean }])).toEqual([])

  // 注入四类问题各一例：没有名称的按钮、没有标签的输入框、重复 id、超宽元素。
  // 这条用例是为了证明门槛真的在看页面，而不是把所有数组都采成空的。
  await page.evaluate(() => {
    const host = document.querySelector('main') ?? document.body
    const button = document.createElement('button')
    button.id = 'audit-probe-button'
    button.innerHTML = '<span aria-hidden="true">*</span>'
    host.append(button)
    const input = document.createElement('input')
    input.id = 'audit-probe-input'
    input.type = 'text'
    host.append(input)
    const duplicate = document.createElement('div')
    duplicate.id = 'audit-probe-button'
    host.append(duplicate)
    const wide = document.createElement('div')
    wide.style.cssText = 'position:absolute;top:0;left:0;width:4000px;height:1px'
    document.documentElement.append(wide)
  })
  const dirty = await collectUiAudit(page)
  expect(dirty.unnamedButtons.length, '未识别出无名称按钮').toBeGreaterThan(0)
  expect(dirty.unlabeledFields.length, '未识别出无标签字段').toBeGreaterThan(0)
  expect(dirty.duplicateIds, '未识别出重复 id').toContain('audit-probe-button')
  expect(dirty.horizontalOverflow, '未识别出横向溢出').toBeGreaterThan(1)
  expect(auditViolations([{ path: '/overview', viewport: 'probe', ...dirty }]).length).toBeGreaterThanOrEqual(4)
})
