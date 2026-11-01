import { test, expect, type APIRequestContext, type Page } from './fixtures'

// 批量确认是逐条串行的，期间页面所有按钮都被禁用。报警洪峰下可能几十上百条，
// 慢速网络里要跑几分钟——必须能中途停下，去处理新来的严重报警。
// 这里固化：停止入口、停止后不再下发、结果要说明还剩多少条、正常跑完仍按成功提示。

async function signIn(request: APIRequestContext) {
  const response = await request.post('/api/auth/login', { data: { userName: 'admin', password: 'admin123' } })
  expect(response.ok(), await response.text()).toBeTruthy()
  return (await response.json()) as { token: string; user: Record<string, unknown> }
}

const alarmCount = 8
const makeAlarms = () => Array.from({ length: alarmCount }, (_, i) => ({
  id: 200 + i, tagId: 8401, deviceId: 8301, ruleId: 900 + i, level: 'Warning', value: 500 + i, limit: 480,
  startTs: Date.now() - (i + 1) * 60_000, endTs: null, ackUser: null, ackTs: null,
  message: `出口压力高高限 ${i + 1}`, state: 'Active', shelved: false, suppressed: false
}))

async function openAlarms(page: Page, request: APIRequestContext, ackDelayMs: number) {
  const auth = await signIn(request)
  await page.addInitScript(({ token, user }) => {
    localStorage.setItem('scada_token', token)
    localStorage.setItem('scada_user', JSON.stringify(user))
  }, auth)
  const acked = new Set<number>()
  let ackCalls = 0
  const alarms = makeAlarms()
  await page.route('**/api/**', async route => {
    const url = new URL(route.request().url())
    const path = url.pathname
    if (/^\/api\/alarms\/\d+\/ack$/.test(path)) {
      ackCalls++
      if (ackDelayMs) await new Promise(resolve => setTimeout(resolve, ackDelayMs))
      acked.add(Number(path.split('/')[3]))
      return route.fulfill({ json: {} })
    }
    if (path === '/api/devices') return route.fulfill({ json: [{ id: 8301, name: '验证设备', areaId: 1, protocol: 'ModbusTcp', enabled: true }] })
    if (path === '/api/tags') return route.fulfill({ json: [{ id: 8401, name: '出口压力', deviceId: 8301, unit: 'kPa', dataType: 'Float', kind: 'Physical', address: 1, area: 'HoldingRegister', enabled: true }] })
    if (path === '/api/alarms/summary') return route.fulfill({
      json: { unack: alarms.length - acked.size, acked: acked.size, rtnUnack: 0, shelved: 0, suppressed: 0, chattering: 0, shelvedRules: 0, outOfServiceRules: 0, flood: { active: false } }
    })
    if (path === '/api/alarms/page') return route.fulfill({
      json: { items: alarms.map(a => ({ ...a, ackTs: acked.has(a.id) ? Date.now() : null })), total: alarms.length }
    })
    if (path === '/api/alarms') return route.fulfill({ json: alarms })
    return route.fulfill({ json: [] })
  })
  await page.goto('/alarms')
  await page.locator('.alarm-desktop-table tbody tr').first().waitFor()
  await page.locator('.alarm-desktop-table thead .el-checkbox').first().click()
  await expect(page.locator('[data-test="batch-ack"]')).toContainText(`(${alarmCount})`)
  return { calls: () => ackCalls, acked }
}

test('批量确认可以中途停止，并说明还剩多少条', async ({ page, request }) => {
  const { calls, acked } = await openAlarms(page, request, 700)
  await page.locator('[data-test="batch-ack"]').click()
  const box = page.locator('.el-message-box')
  await expect(box).toBeVisible()
  await box.locator('button.el-button--primary').click()

  // 进行中必须给出口，否则只能干等
  const stop = page.locator('[data-test="batch-stop"]')
  await expect(stop).toBeVisible()
  await expect(page.locator('[data-test="batch-progress"]')).toBeVisible()
  await stop.click()
  await expect(stop).toBeDisabled()

  await expect(page.locator('[data-test="alarm-batch-result"]')).toBeVisible({ timeout: 15_000 })
  const done = calls()
  expect(done, '停止后不应继续下发剩余条目').toBeLessThan(alarmCount)
  expect(acked.size, '已确认条数应与实际下发一致').toBe(done)

  // 剩下的必须说清楚，否则操作员不知道还有哪些没处理
  await expect(page.locator('[data-test="alarm-batch-stopped"]')).toContainText(`还有 ${alarmCount - done} 条未确认`)
  // 停止不等于失败，但也不该报成绿色的「全部成功」
  await expect(page.locator('.el-message--success')).toHaveCount(0)
  // 停止后页面必须恢复可操作
  await expect(page.locator('[data-test="ack-visible"]')).toBeEnabled()
  await expect(stop).toHaveCount(0)
})

test('不停止时照常跑完，结果仍是成功提示', async ({ page, request }) => {
  const { calls } = await openAlarms(page, request, 0)
  await page.locator('[data-test="batch-ack"]').click()
  await page.locator('.el-message-box button.el-button--primary').click()

  await expect(page.locator('[data-test="alarm-batch-result"]')).toBeVisible({ timeout: 15_000 })
  expect(calls()).toBe(alarmCount)
  await expect(page.locator('[data-test="alarm-batch-stopped"]')).toHaveCount(0)
  await expect(page.locator('.el-message--success')).toHaveCount(1)
})
