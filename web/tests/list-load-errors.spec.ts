import { test, expect, type Page } from './fixtures'

async function signIn(page: Page): Promise<void> {
  await page.goto('/login')
  await page.getByPlaceholder(/用户名/).fill('admin')
  await page.getByPlaceholder(/密码/).fill('admin123')
  await page.keyboard.press('Enter')
  await expect(page).toHaveURL(/overview/)
}

async function expectLoadError(page: Page, path: string, apiPath: string, message: string, testId: string, absent: string[]): Promise<void> {
  await page.route((url) => url.pathname === apiPath, (route) => {
    if (route.request().method() !== 'GET') return route.continue()
    return route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message }) })
  })
  await page.goto(path)
  await expect(page.locator(`[data-test="${testId}"]`)).toContainText(message)
  for (const text of absent) await expect(page.getByText(text, { exact: true })).toHaveCount(0)
}

test('alarm list keeps a load failure on the page', async ({ page }) => {
  await signIn(page)
  await expectLoadError(page, '/alarms', '/api/alarms', '报警列表不可用', 'alarms-load-error', ['暂无报警'])
})

test('device list keeps a load failure on the page', async ({ page }) => {
  await signIn(page)
  await expectLoadError(page, '/config/devices', '/api/devices', '设备列表不可用', 'devices-load-error', ['暂无设备'])
})

test('tag list keeps a load failure on the page', async ({ page }) => {
  await signIn(page)
  await expectLoadError(page, '/config/tags', '/api/tags', '测点列表不可用', 'tags-load-error', ['暂无测点'])
})

test('alarm rule list keeps a load failure on the page', async ({ page }) => {
  await signIn(page)
  await expectLoadError(page, '/config/rules', '/api/alarm-rules', '报警规则不可用', 'rules-load-error', ['暂无报警规则'])
})

test('control list keeps a load failure on the page', async ({ page }) => {
  await signIn(page)
  await expectLoadError(page, '/control', '/api/control/requests', '控制请求不可用', 'control-load-error', ['没有复核申请'])
})

test('recipe list keeps a load failure on the page', async ({ page }) => {
  await signIn(page)
  await expectLoadError(page, '/recipes', '/api/recipes', '配方列表不可用', 'recipes-load-error', ['尚无配方', '尚无下发记录'])
})
