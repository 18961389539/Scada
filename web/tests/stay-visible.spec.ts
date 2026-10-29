import { test, expect, type APIRequestContext, type Page } from './fixtures'

async function login(request: APIRequestContext) {
  const response = await request.post('/api/auth/login', { data: { userName: 'admin', password: 'admin123' } })
  expect(response.ok()).toBeTruthy()
  const body = await response.json()
  return { token: body.token as string, user: body.user, headers: { Authorization: `Bearer ${body.token}` } }
}

async function sign(page: Page, session: { token: string; user: unknown }): Promise<void> {
  await page.addInitScript((data) => {
    localStorage.setItem('scada_token', data.token)
    localStorage.setItem('scada_user', JSON.stringify(data.user))
  }, session)
}

test('a wrong password stays on the login card', async ({ page }) => {
  await page.goto('/login')
  await page.getByPlaceholder('请输入用户名').fill('admin')
  await page.getByPlaceholder('请输入密码').fill('wrong-password')
  await page.getByRole('button', { name: /登 录/ }).click()
  await expect(page.locator('[data-test="login-error"]')).toContainText('用户名或密码错误')
  await expect(page).toHaveURL(/\/login/)
})

test('alarm actions with nothing to do stay on the page', async ({ page, request }) => {
  const session = await login(request)
  await sign(page, session)
  await page.route(url => url.pathname === '/api/alarms', route => route.fulfill({ json: [] }))
  await page.route(url => url.pathname === '/api/alarms/page', route => route.fulfill({ json: { items: [], total: 0, offset: 0, limit: 100 } }))
  await page.goto('/alarms')
  await expect(page.locator('[data-test="ack-visible"]')).toBeDisabled()
  await expect(page.locator('[data-test="alarm-result-count"]')).toContainText('可确认 0 条')
  const error = page.locator('[data-test="alarms-action-error"]')
  await expect(error).toHaveCount(0)
  await expect(page.getByRole('dialog')).not.toBeVisible()
  await page.locator('[data-test="silence-ann"]').click()
  await expect(error).toContainText('没有可消音的声光面板')
  await expect(page.getByRole('dialog')).not.toBeVisible()
})

test('opening the other console monitors explains a single display on the page', async ({ page, request }) => {
  const session = await login(request)
  const suffix = `${Date.now()}`.slice(-6)
  const headers = session.headers
  const created = await request.post('/api/screens', { headers, data: { name: `画面${suffix}` } })
  expect(created.status()).toBe(201)
  const screen = await created.json()
  const document = {
    schemaVersion: 1, width: 800, height: 480, background: '#101b2d',
    nodes: [{
      id: 'label-1', type: 'text', x: 40, y: 40, width: 160, height: 48, rotation: 0,
      text: '主画面', color: '#7394b8', activeColor: '#34d399', unit: '', decimals: 1,
      min: 0, max: 100, activeThreshold: 0.5, tagId: null,
      action: { type: 'none', screenId: null, tagId: null, value: 0 }
    }]
  }
  const saved = await request.put(`/api/screens/${screen.id}`, {
    headers, data: { name: `画面${suffix}`, revision: screen.revision, document }
  })
  expect(saved.ok(), await saved.text()).toBeTruthy()
  const published = await request.post(`/api/screens/${screen.id}/publish`, {
    headers, data: { revision: (await saved.json()).revision }
  })
  expect(published.ok(), await published.text()).toBeTruthy()
  const region = (id: string) => ({ id, kind: 'screen', screenId: screen.id, row: 1, col: 1, rowSpan: 12, colSpan: 12 })
  const layout = await request.post('/api/hmi/layouts', {
    headers,
    data: {
      name: `双屏${suffix}`,
      document: { monitors: [{ name: '主屏', columns: 12, rows: 12, regions: [region('main')] }, { name: '副屏', columns: 12, rows: 12, regions: [region('side')] }] }
    }
  })
  expect(layout.ok(), await layout.text()).toBeTruthy()
  const createdLayout = await layout.json()
  const publication = await request.post(`/api/hmi/layouts/${createdLayout.id}/publish`, { headers, data: { revision: createdLayout.revision, note: '显示器提示验收' } })
  expect(publication.ok(), await publication.text()).toBeTruthy()
  await sign(page, session)
  await page.goto(`/console/${createdLayout.id}`)
  await page.evaluate(() => {
    window.open = () => ({ moveTo() {}, resizeTo() {} }) as unknown as Window
    Object.defineProperty(window, 'getScreenDetails', { value: async () => ({ screens: [
      { label: '测试主屏', availLeft: 0, availTop: 0, availWidth: 1600, availHeight: 1000, isPrimary: true }
    ] }) })
  })
  await page.locator('[data-test="console-open-all"]').click()
  await expect(page.locator('[data-test="console-mapping-dialog"]')).toContainText('物理屏不足或定位权限不可用')
})
