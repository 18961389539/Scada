import { test, expect, type APIRequestContext, type Page, loginApi } from './fixtures'

// 「趋势」链接是报警页里最常用的下钻：现场看到报警，第一反应是把曲线调出来看前后走势。
// 以前只带 tagId，落到趋势页的默认区间（最近 1 小时）——几小时前的报警点开就是空图。
// 这里固化：链接必须带上报警发生前后那一段，且区间能在趋势页真的生效。

const startTs = Date.now() - 3 * 3600_000 // 三小时前，早已超出默认 1 小时窗口

async function open(page: Page, request: APIRequestContext) {
  const auth = await loginApi(request)
  await page.addInitScript(({ token, user }) => {
    localStorage.setItem('scada_token', token)
    localStorage.setItem('scada_user', JSON.stringify(user))
  }, auth)
  const ranges: { from: number; to: number }[] = []
  await page.route('**/api/**', route => route.fulfill({ json: [] }))
  await page.route(url => url.pathname === '/api/history', route => {
    const params = new URL(route.request().url()).searchParams
    ranges.push({ from: Number(params.get('from')), to: Number(params.get('to')) })
    return route.fulfill({ json: [] })
  })
  await page.route(url => url.pathname === '/api/alarms/page', route => route.fulfill({
    json: {
      items: [{ id: 601, tagId: 8401, deviceId: 8301, ruleId: 901, level: 'Warning', value: 500, startTs, endTs: null, ackUser: null, ackTs: null, message: '出口压力高高限', state: 'Active', shelved: false, suppressed: false }],
      total: 1
    }
  }))
  await page.route(url => url.pathname === '/api/alarms/summary', route => route.fulfill({
    json: { unack: 1, acked: 0, rtnUnack: 0, shelved: 0, suppressed: 0, chattering: 0, shelvedRules: 0, outOfServiceRules: 0, flood: { active: false } }
  }))
  await page.route(url => url.pathname === '/api/devices', route => route.fulfill({ json: [{ id: 8301, name: '验证设备', areaId: 1, protocol: 'ModbusTcp', enabled: true }] }))
  await page.route(url => url.pathname === '/api/tags', route => route.fulfill({
    json: [{ id: 8401, name: '出口压力', deviceId: 8301, unit: 'kPa', dataType: 'Float', kind: 'Physical', address: 1, area: 'HoldingRegister', enabled: true }]
  }))
  await page.goto('/alarms')
  await page.locator('.alarm-desktop-table tbody tr').first().waitFor()
  return ranges
}

test('报警行点「趋势」时把报警前后那一段带过去', async ({ page, request }) => {
  const ranges = await open(page, request)
  await page.locator('[data-test="alarm-trend"]').first().click()
  await expect(page).toHaveURL(/\/trends\?/)
  const url = new URL(page.url())
  const from = Number(url.searchParams.get('from')), to = Number(url.searchParams.get('to'))
  expect(from, '应往前留出上升沿').toBe(startTs - 10 * 60_000)
  expect(to, '持续中的报警取到当前时刻').toBeGreaterThan(startTs)
  expect(url.searchParams.get('tagId')).toBe('8401')

  // 区间要真的作用到请求上，而不是只挂在地址栏
  await expect.poll(() => ranges.length, { timeout: 15_000 }).toBeGreaterThan(0)
  expect(ranges[0].from).toBe(from)
  expect(ranges[0].to).toBe(to > Date.now() ? ranges[0].to : to)
  expect(ranges[0].to - ranges[0].from).toBeGreaterThan(10 * 60_000)
})
