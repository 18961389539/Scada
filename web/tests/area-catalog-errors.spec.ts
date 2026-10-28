import { test, expect, type Page } from '@playwright/test'

async function signIn(page: Page): Promise<void> {
  await page.goto('/login')
  await page.getByPlaceholder(/用户名/).fill('admin')
  await page.getByPlaceholder(/密码/).fill('admin123')
  await page.keyboard.press('Enter')
  await expect(page).toHaveURL(/overview/)
}

async function failAreas(page: Page): Promise<void> {
  await page.route((url) => url.pathname === '/api/areas', (route) => {
    if (route.request().method() !== 'GET') return route.continue()
    return route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message: '区域列表不可用' }) })
  })
}

test('device page keeps an area-catalog failure and does not offer create', async ({ page, request }) => {
  const login = await request.post('/api/auth/login', { data: { userName: 'admin', password: 'admin123' } })
  expect(login.ok()).toBeTruthy()
  const headers = { Authorization: `Bearer ${(await login.json()).token}` }
  const suffix = `${Date.now()}`
  const created = await request.post('/api/devices', {
    headers,
    data: {
      name: `AREA-${suffix}`, protocol: 'ModbusTcp', host: '127.0.0.1', port: 1502, stationNumber: 1,
      byteOrder: 'ABCD', pollIntervalMs: 1000, timeoutMs: 1000, enabled: false, areaId: 1
    }
  })
  expect(created.ok(), await created.text()).toBeTruthy()
  await signIn(page)
  await failAreas(page)
  await page.goto('/config/devices')
  await expect(page.locator('[data-test="devices-areas-error"]')).toContainText('区域列表不可用')
  await expect(page.locator('[data-test="device-create"]')).toHaveCount(0)
  await expect(page.getByText('默认区域', { exact: true })).toHaveCount(0)
  await expect(page.locator('tr', { hasText: `AREA-${suffix}` })).toContainText('#1')
})

test('recipe templates keep an area-catalog failure and do not offer create', async ({ page }) => {
  await signIn(page)
  await failAreas(page)
  await page.goto('/recipes')
  await expect(page.locator('[data-test="recipes-areas-error"]')).toContainText('区域列表不可用')
  await expect(page.locator('[data-test="template-new"]')).toHaveCount(0)
  await expect(page.getByText('默认区域', { exact: true })).toHaveCount(0)
})

test('control sequences keep an area-catalog failure and do not offer create', async ({ page }) => {
  await signIn(page)
  await failAreas(page)
  await page.goto('/control')
  await expect(page.locator('[data-test="control-areas-error"]')).toContainText('区域列表不可用')
  await expect(page.locator('[data-test="sequence-new"]')).toHaveCount(0)
  await expect(page.getByText('默认区域', { exact: true })).toHaveCount(0)
})

test('calc jobs keep an area-catalog failure and do not offer create', async ({ page }) => {
  await signIn(page)
  await failAreas(page)
  await page.goto('/calc-jobs')
  await expect(page.locator('[data-test="calc-job-areas-error"]')).toContainText('区域列表不可用')
  await expect(page.locator('[data-test="calc-job-new"]')).toHaveCount(0)
  await expect(page.getByText('默认区域', { exact: true })).toHaveCount(0)
})

test('report center keeps an area-catalog failure and does not offer create', async ({ page }) => {
  await signIn(page)
  await failAreas(page)
  await page.goto('/report-center')
  await page.locator('[data-test="report-center-templates"]').click()
  await expect(page.locator('[data-test="report-areas-error"]')).toContainText('区域列表不可用')
  await expect(page.locator('[data-test="report-template-new"]')).toHaveCount(0)
  await expect(page.getByText('默认区域', { exact: true })).toHaveCount(0)
})
