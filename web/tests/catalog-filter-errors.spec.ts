import { test, expect, type APIRequestContext, type Page } from './fixtures'

async function signIn(page: Page): Promise<void> {
  await page.goto('/login')
  await page.getByPlaceholder(/用户名/).fill('admin')
  await page.getByPlaceholder(/密码/).fill('admin123')
  await page.keyboard.press('Enter')
  await expect(page).toHaveURL(/overview/)
}

async function login(request: APIRequestContext) {
  const response = await request.post('/api/auth/login', { data: { userName: 'admin', password: 'admin123' } })
  expect(response.ok()).toBeTruthy()
  return { Authorization: `Bearer ${(await response.json()).token}` }
}

test('alarm analytics keeps area and rule catalog failures on the page', async ({ page }) => {
  await signIn(page)
  await page.route((url) => url.pathname === '/api/areas' || url.pathname === '/api/alarm-rules', (route) => {
    if (route.request().method() !== 'GET') return route.continue()
    const message = route.request().url().includes('/api/alarm-rules') ? '报警规则不可用' : '区域列表不可用'
    return route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message }) })
  })
  await page.goto('/alarm-analytics')
  await expect(page.locator('[data-test="aa-areas-error"]')).toContainText('区域列表不可用')
  await expect(page.locator('[data-test="aa-rules-error"]')).toContainText('报警规则不可用')
  await page.locator('[data-test="aa-area"]').click()
  await expect(page.getByRole('option', { name: '默认区域' })).toHaveCount(0)
})

test('calculation editor keeps an area-catalog failure on the page', async ({ page }) => {
  await signIn(page)
  await page.route((url) => url.pathname === '/api/areas', (route) => {
    if (route.request().method() !== 'GET') return route.continue()
    return route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message: '区域列表不可用' }) })
  })
  await page.goto('/config/calculations')
  await expect(page.locator('[data-test="calc-areas-error"]')).toContainText('区域列表不可用')
  await expect(page.locator('[data-test="calc-new"]')).toBeVisible()
  await page.locator('[data-test="calc-new"]').click()
  await page.locator('[data-test="calc-area"]').click()
  await expect(page.getByRole('option', { name: '默认区域' })).toHaveCount(0)
})

test('notification routes keep an area-catalog failure on the page', async ({ page, request }) => {
  const headers = await login(request)
  const suffix = `${Date.now()}`
  const channel = await request.post('/api/notification-channels', {
    headers,
    data: {
      name: `AREA-CH-${suffix}`, type: 'Webhook', enabled: true,
      settings: { url: 'http://127.0.0.1:9/hook', port: 25, useSsl: false, timeoutMs: 5000 }, secret: null
    }
  })
  expect(channel.ok(), await channel.text()).toBeTruthy()
  const channelId = (await channel.json()).id
  const route = await request.post('/api/notification-routes', {
    headers,
    data: {
      name: `AREA-RT-${suffix}`, enabled: true, channelId, areaId: 1, minLevel: 'Warning', shiftId: null,
      recipients: '', onRaise: true, onRecover: false, escalateAfterMinutes: null
    }
  })
  expect(route.ok(), await route.text()).toBeTruthy()
  await signIn(page)
  await page.route((url) => url.pathname === '/api/areas', (route) => {
    if (route.request().method() !== 'GET') return route.continue()
    return route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message: '区域列表不可用' }) })
  })
  await page.goto('/config/notifications')
  await expect(page.locator('[data-test="notifications-areas-error"]')).toContainText('区域列表不可用')
  await expect(page.getByText('默认区域', { exact: true })).toHaveCount(0)
  await expect(page.locator('tr', { hasText: `AREA-RT-${suffix}` })).toContainText('#1')
  await expect(page.locator('[data-test="route-add"]')).toBeEnabled()
})
