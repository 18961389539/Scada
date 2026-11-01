import { test, expect, type APIRequestContext, type Page } from './fixtures'

// 现场大量使用 1024×768 的触摸一体机/壁挂屏。表格里的行选择框只有 12×12、
// 文字链接只有一行高（约 16px），是鼠标尺寸：批量确认的第一步（勾选报警）就点不中。
// 这里固化「触控下够大、桌面不受影响」，避免以后调样式把它调回去。

async function signIn(request: APIRequestContext) {
  const response = await request.post('/api/auth/login', { data: { userName: 'admin', password: 'admin123' } })
  expect(response.ok(), await response.text()).toBeTruthy()
  return (await response.json()) as { token: string; user: Record<string, unknown> }
}

async function openAlarms(page: Page, request: APIRequestContext) {
  const auth = await signIn(request)
  await page.addInitScript(({ token, user }) => {
    localStorage.setItem('scada_token', token)
    localStorage.setItem('scada_user', JSON.stringify(user))
  }, auth)
  const now = Date.now()
  const alarms = Array.from({ length: 4 }, (_, i) => ({
    id: 300 + i, tagId: 8401, deviceId: 8301, ruleId: 400 + i, level: 'Warning', value: 500 + i, limit: 480,
    startTs: now - (i + 1) * 60_000, endTs: null, ackUser: null, ackTs: null,
    message: `出口压力高高限 ${i + 1}`, state: 'Active', shelved: false, suppressed: false
  }))
  await page.route('**/api/**', route => route.fulfill({ json: [] }))
  await page.route(url => url.pathname === '/api/alarms/page', route => route.fulfill({ json: { items: alarms, total: alarms.length } }))
  await page.route(url => url.pathname === '/api/alarms/summary', route => route.fulfill({
    json: { unack: alarms.length, acked: 0, rtnUnack: 0, shelved: 0, suppressed: 0, chattering: 0, shelvedRules: 0, outOfServiceRules: 0, flood: { active: false } }
  }))
  await page.route(url => url.pathname === '/api/devices', route => route.fulfill({
    json: [{ id: 8301, name: '验证设备', areaId: 1, protocol: 'ModbusTcp', enabled: true }]
  }))
  await page.route(url => url.pathname === '/api/tags', route => route.fulfill({
    json: [{ id: 8401, name: '出口压力', deviceId: 8301, unit: 'kPa', dataType: 'Float', kind: 'Physical', address: 1, area: 'HoldingRegister', enabled: true }]
  }))
  await page.goto('/alarms')
  await page.locator('.alarm-desktop-table tbody tr').first().waitFor()
  // 等首屏布局落定：表格列宽与行高会随字体加载再调整一次
  await page.waitForTimeout(400)
}

test.describe('触控设备（1024×768 触摸屏）', () => {
  test.use({ hasTouch: true, viewport: { width: 1024, height: 768 } })

  test('行选择框与趋势链接点得中', async ({ page, request }) => {
    await openAlarms(page, request)

    const checkbox = page.locator('.alarm-desktop-table tbody tr').first().locator('.el-checkbox')
    const box = await checkbox.boundingBox()
    expect(box, '行选择框应当渲染').toBeTruthy()
    expect(box!.width, '行选择框可点宽度').toBeGreaterThanOrEqual(32)
    expect(box!.height, '行选择框可点高度').toBeGreaterThanOrEqual(32)

    // 点扩出来的边缘（内层方框之外），这是手指实际会落到的位置。
    // 用元素内相对坐标：Playwright 会校验该点是否真的命中此元素，被遮挡会直接失败。
    await checkbox.click({ position: { x: 3, y: box!.height / 2 } })
    await expect(page.locator('[data-test="batch-ack"]')).toContainText('(1)')

    const trend = page.locator('[data-test="alarm-trend"]').first()
    const trendBox = await trend.boundingBox()
    expect(trendBox!.height, '趋势链接可点高度').toBeGreaterThanOrEqual(32)
  })
})

test.describe('鼠标桌面（1920×1080）', () => {
  test.use({ hasTouch: false, viewport: { width: 1920, height: 1080 } })

  test('触控规则不放大桌面尺寸', async ({ page, request }) => {
    await openAlarms(page, request)

    const box = await page.locator('.alarm-desktop-table tbody tr').first().locator('.el-checkbox').boundingBox()
    expect(box!.width, '桌面复选框保持紧凑').toBeLessThanOrEqual(20)
    expect(box!.height).toBeLessThanOrEqual(20)

    const trendBox = await page.locator('[data-test="alarm-trend"]').first().boundingBox()
    expect(trendBox!.height, '桌面文字链接不应被撑高').toBeLessThan(28)
  })
})
