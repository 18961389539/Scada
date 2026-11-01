import { test, expect, type APIRequestContext, type Page } from './fixtures'

// 现场动作失败时，措辞必须区分两种情况：
//   1) 服务端回了错误码 —— 命令被拒绝，现场没被改，可以直接改条件重试；
//   2) 没有响应（超时/断网/连接重置）—— 命令可能已经生效，只是回执没回来。
// 两者混用「请稍后重试」会诱导操作员重复下发一次。这里固化措辞。

async function signIn(request: APIRequestContext) {
  const response = await request.post('/api/auth/login', { data: { userName: 'admin', password: 'admin123' } })
  expect(response.ok(), await response.text()).toBeTruthy()
  return (await response.json()) as { token: string; user: Record<string, unknown> }
}

async function mockApiFallback(page: Page) {
  await page.route('**/api/**', route => route.fulfill({ json: [] }))
}

const writableTag = {
  id: 8401, name: '出口压力设定', deviceId: 8301, unit: 'kPa', dataType: 'Float', kind: 'Physical',
  address: 1, area: 'HoldingRegister', enabled: true, writable: true, minValue: 0, maxValue: 1000
}

async function openWriteDialog(page: Page, request: APIRequestContext) {
  const auth = await signIn(request)
  await page.addInitScript(({ token, user }) => {
    localStorage.setItem('scada_token', token)
    localStorage.setItem('scada_user', JSON.stringify(user))
  }, auth)
  await mockApiFallback(page)
  await page.route(url => url.pathname === '/api/devices', route => route.fulfill({
    json: [{ id: 8301, name: '验证设备', areaId: 1, protocol: 'ModbusTcp', enabled: true, host: '127.0.0.1', port: 502, slaveId: 1 }]
  }))
  await page.route(url => url.pathname === '/api/tags', route => route.fulfill({ json: [writableTag] }))
  await page.route(url => url.pathname === '/api/realtime', route => route.fulfill({
    json: [{ tagId: 8401, name: writableTag.name, deviceId: 8301, value: 300, ts: Date.now(), quality: 'Good' }]
  }))
  await page.route(url => url.pathname === '/api/control/check', route => route.fulfill({ json: { refusals: [], requireConfirm: false } }))
  await page.goto('/realtime')
  await page.locator('[data-test="realtime-write"]').first().click()
  await expect(page.locator('[data-test="write-number"] input')).toBeVisible()
  await page.locator('[data-test="write-number"] input').fill('450')
  return page.locator('.el-dialog__footer button.el-button--primary')
}

test('写值超时：提示结果未知，不提示「稍后重试」', async ({ page, request }) => {
  const send = await openWriteDialog(page, request)
  await page.route(url => url.pathname === '/api/commands', route => route.abort('timedout'))
  await send.click()

  const error = page.locator('[data-test="realtime-write-error"]')
  await expect(error).toContainText('操作结果未知')
  await expect(error).not.toContainText('稍后重试')
  // 弹窗必须留着：操作员要靠上面的当前值、量程核对现场，并决定是否重发
  await expect(page.locator('[data-test="write-number"] input')).toBeVisible()
})

test('预检查失败：明确说明命令没有下发', async ({ page, request }) => {
  const send = await openWriteDialog(page, request)
  await page.route(url => url.pathname === '/api/control/check', route => route.fulfill({ status: 500, json: { message: '控制服务繁忙' } }))
  await send.click()

  await expect(page.locator('[data-test="realtime-write-error"]')).toContainText('命令未下发')
})

test('服务端拒绝：直接展示拒绝原因，不套用「结果未知」', async ({ page, request }) => {
  const send = await openWriteDialog(page, request)
  await page.route(url => url.pathname === '/api/commands', route => route.fulfill({
    json: { success: false, message: '超出该测点的量程上限', pending: false, readBack: null, commandId: null }
  }))
  await send.click()

  const error = page.locator('[data-test="realtime-write-error"]')
  await expect(error).toContainText('量程上限')
  await expect(error).not.toContainText('结果未知')
})
