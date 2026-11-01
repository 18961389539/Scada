import { test, expect, type APIRequestContext, type Page } from './fixtures'

// 报警值是判断严重程度的直接依据。只显示「500.00」而不带单位时，
// 一屏里压力、温度、流量混排，操作员得靠报警文案去猜量级；开关量再套两位小数
// （1.00）还会让人以为它是模拟量。这里固化：模拟量带单位、开关量显示 0/1。

async function signIn(request: APIRequestContext) {
  const response = await request.post('/api/auth/login', { data: { userName: 'admin', password: 'admin123' } })
  expect(response.ok(), await response.text()).toBeTruthy()
  return (await response.json()) as { token: string; user: Record<string, unknown> }
}

const tags = [
  { id: 8401, name: '出口压力', deviceId: 8301, unit: 'kPa', dataType: 'Float', kind: 'Physical', address: 1, area: 'HoldingRegister', enabled: true },
  { id: 8402, name: '泵运行', deviceId: 8301, unit: '', dataType: 'Bool', kind: 'Physical', address: 2, area: 'Coil', enabled: true }
]

const alarms = [
  { id: 401, tagId: 8401, deviceId: 8301, ruleId: 901, level: 'Warning', value: 500, limit: 480, startTs: Date.now() - 60_000, endTs: null, ackUser: null, ackTs: null, message: '出口压力高高限', state: 'Active', shelved: false, suppressed: false },
  { id: 402, tagId: 8402, deviceId: 8301, ruleId: 902, level: 'Critical', value: 1, limit: 0, startTs: Date.now() - 30_000, endTs: null, ackUser: null, ackTs: null, message: '泵停机', state: 'Active', shelved: false, suppressed: false }
]

async function openAlarms(page: Page, request: APIRequestContext) {
  const auth = await signIn(request)
  await page.addInitScript(({ token, user }) => {
    localStorage.setItem('scada_token', token)
    localStorage.setItem('scada_user', JSON.stringify(user))
  }, auth)
  await page.route('**/api/**', route => route.fulfill({ json: [] }))
  await page.route(url => url.pathname === '/api/alarms/page', route => route.fulfill({ json: { items: alarms, total: alarms.length } }))
  await page.route(url => url.pathname === '/api/alarms/summary', route => route.fulfill({
    json: { unack: alarms.length, acked: 0, rtnUnack: 0, shelved: 0, suppressed: 0, chattering: 0, shelvedRules: 0, outOfServiceRules: 0, flood: { active: false } }
  }))
  await page.route(url => url.pathname === '/api/devices', route => route.fulfill({ json: [{ id: 8301, name: '验证设备', areaId: 1, protocol: 'ModbusTcp', enabled: true }] }))
  await page.route(url => url.pathname === '/api/tags', route => route.fulfill({ json: tags }))
  await page.goto('/alarms')
  await page.locator('.alarm-desktop-table tbody tr').first().waitFor()
  await page.waitForTimeout(300)
}

test('模拟量报警值带单位，开关量报警显示 0/1 而不是两位小数', async ({ page, request }) => {
  await openAlarms(page, request)
  const rows = page.locator('.alarm-desktop-table tbody tr')
  // 单位来自测点目录（异步加载），用轮询断言而不是固定等待
  await expect(rows.nth(0)).toContainText('500.00 kPa')
  await expect(rows.nth(1)).toContainText('泵停机')
  const boolCell = await rows.nth(1).innerText()
  expect(boolCell, '开关量不该显示 1.00').not.toContain('1.00')
  expect(boolCell).toMatch(/(^|\s)1(\s|$)/)
})

test('确认弹窗里也能看到报警值，不用回头看列表', async ({ page, request }) => {
  await openAlarms(page, request)
  await page.locator('.alarm-desktop-table tbody tr').nth(0).locator('[data-test="ack"]').click()
  await expect(page.locator('[data-test="ack-dialog-value"]')).toContainText('500.00 kPa')
})
