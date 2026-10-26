import { test, expect, type APIRequestContext, type Page } from '@playwright/test'
import type { ScreenSummary } from '../src/screens/model'

const screens: ScreenSummary[] = [
  { id: 101, name: 'Pump 2', revision: 1, publishedRevision: null, updatedTs: 1000, updatedBy: 'Engineer', hasDraftChanges: true },
  { id: 102, name: 'Pump 10', revision: 2, publishedRevision: 2, updatedTs: 2000, updatedBy: 'operator', hasDraftChanges: false },
  { id: 103, name: 'Boiler 1', revision: 3, publishedRevision: 2, updatedTs: 3000, updatedBy: 'ENGINEER', hasDraftChanges: true }
]

async function openScreens(page: Page, request: APIRequestContext): Promise<void> {
  const response = await request.post('/api/auth/login', { data: { userName: 'admin', password: 'admin123' } })
  expect(response.ok()).toBeTruthy()
  const auth = await response.json()
  await page.addInitScript(({ token, user }) => {
    localStorage.setItem('scada_token', token)
    localStorage.setItem('scada_user', JSON.stringify(user))
  }, auth)
  await page.route(url => url.pathname === '/api/screens', route => route.fulfill({ json: screens }))
  await page.goto('/screens')
  await expect(page.locator('[data-test="screens-count"]')).toHaveText('显示 3 / 3 个画面')
}

async function select(page: Page, testId: string, label: string): Promise<void> {
  await page.locator(`[data-test="${testId}"]`).click()
  await page.getByRole('option', { name: label, exact: true }).click()
}

test('screen catalog searches names and editors, filters publication state, and sorts names naturally', async ({ page, request }) => {
  await openScreens(page, request)
  const cards = page.locator('.screen-card h3')
  await expect(cards).toHaveText(['Boiler 1', 'Pump 10', 'Pump 2'])

  await page.getByRole('textbox', { name: '搜索画面名称或修改人' }).fill('  pump ENGINEER  ')
  await expect(cards).toHaveText(['Pump 2'])
  await expect(page.locator('[data-test="screens-count"]')).toHaveText('显示 1 / 3 个画面')
  await page.locator('[data-test="screens-reset"]').click()

  await select(page, 'screens-status', '有未发布修改')
  await expect(cards).toHaveText(['Boiler 1'])
  await expect(page.locator('[data-test="screen-card-103"] .el-tag')).toHaveText('有未发布修改')
  await select(page, 'screens-status', '已发布')
  await expect(cards).toHaveText(['Boiler 1', 'Pump 10'])
  await select(page, 'screens-status', '尚未发布')
  await expect(cards).toHaveText(['Pump 2'])
  await page.locator('[data-test="screens-reset"]').click()

  await select(page, 'screens-sort', '按名称排序')
  await expect(cards).toHaveText(['Boiler 1', 'Pump 2', 'Pump 10'])
  await page.getByRole('textbox', { name: '搜索画面名称或修改人' }).fill('missing-screen')
  await expect(page.locator('[data-test="screens-no-matches"]')).toContainText('没有符合筛选条件的画面')
  await page.locator('[data-test="screens-no-matches"]').getByRole('button', { name: '清除筛选' }).click()
  await expect(cards).toHaveCount(3)
  await expect(page.locator('[data-test="screen-card-103"] .el-tag')).toBeVisible()
  await expect.poll(() => page.locator('[data-test="screen-card-103"] .el-tag').evaluate(el => getComputedStyle(el).opacity)).toBe('1')
  await page.screenshot({ path: test.info().outputPath('screen-catalog-desktop.png'), animations: 'disabled' })
})

test('screen catalog reload recovers from a failure and keeps active filters during refresh', async ({ page, request }) => {
  await openScreens(page, request)
  await page.route(url => url.pathname === '/api/screens', route => route.fulfill({ status: 500, json: { message: '画面服务暂时不可用' } }))
  await page.reload()
  await expect(page.locator('[data-test="screens-load-error"]')).toContainText('画面服务暂时不可用')
  await expect(page.locator('[data-test="screens-no-matches"]')).toHaveCount(0)
  await page.route(url => url.pathname === '/api/screens', route => route.fulfill({ json: screens }))
  await page.getByRole('button', { name: '重新加载', exact: true }).click()
  await expect(page.locator('.screen-card')).toHaveCount(3)
  await expect(page.locator('[data-test="screens-load-error"]')).toHaveCount(0)
  await page.getByRole('textbox', { name: '搜索画面名称或修改人' }).fill('operator')
  await page.getByRole('button', { name: '刷新', exact: true }).click()
  await expect(page.locator('.screen-card h3')).toHaveText(['Pump 10'])
  await expect(page.getByRole('textbox', { name: '搜索画面名称或修改人' })).toHaveValue('operator')
})

test('screen catalog and command palette fit a narrow viewport with long screen names', async ({ page, request }) => {
  await page.setViewportSize({ width: 390, height: 844 })
  await openScreens(page, request)
  await page.route(url => url.pathname === '/api/screens', route => route.fulfill({ json: [{ ...screens[0], name: 'LongScreenName'.repeat(12) }] }))
  await page.getByRole('button', { name: '刷新', exact: true }).click()
  await expect(page.locator('.screen-card')).toHaveCount(1)
  await expect.poll(() => page.locator('.screen-card .el-tag').evaluate(el => getComputedStyle(el).opacity)).toBe('1')
  expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(390)
  await page.screenshot({ path: test.info().outputPath('screen-catalog-mobile.png'), animations: 'disabled' })

  await page.locator('[data-test="nav-search"]').click()
  const palette = page.locator('[data-test="command-palette"]')
  await expect(palette).toBeVisible()
  await expect(palette.locator('[data-test^="command-palette-item-"]')).toHaveCount(27)
  const bounds = await palette.boundingBox()
  expect(bounds!.x).toBeGreaterThanOrEqual(0)
  expect(bounds!.x + bounds!.width).toBeLessThanOrEqual(390)
  await page.screenshot({ path: test.info().outputPath('command-palette-mobile.png'), animations: 'disabled' })
})
