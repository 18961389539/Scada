import { test, expect, type APIRequestContext, type Page, loginApi } from './fixtures'

// 测点/设备/报警规则三个台账页的批量操作是逐条串行的。一次勾几百条时，
// 中途发现勾错了（比如把生产测点当成测试测点）必须能停下，而不是关页面或等它跑完。
// 这里固化三件事：进行中给得出「停止」、停止后不再继续下发、结果说清还剩多少条。

const total = 8

async function prepare(page: Page, request: APIRequestContext) {
  const auth = await loginApi(request)
  await page.addInitScript(({ token, user }) => {
    localStorage.setItem('scada_token', token)
    localStorage.setItem('scada_user', JSON.stringify(user))
  }, auth)
}

const devices = () => Array.from({ length: total }, (_, i) => ({
  id: 900 + i, name: `验证设备${i + 1}`, areaId: 1, protocol: 'ModbusTcp', enabled: true, host: '127.0.0.1', port: 502
}))

const tags = () => Array.from({ length: total }, (_, i) => ({
  id: 700 + i, name: `出口压力${i + 1}`, deviceId: 900, unit: 'kPa', dataType: 'Float',
  kind: 'Physical', address: i, area: 'HoldingRegister', enabled: true
}))

const rules = () => Array.from({ length: total }, (_, i) => ({
  id: 500 + i, tagId: 700, level: 'Warning', condition: 'Above', threshold: 480,
  enabled: true, message: `出口压力高高限${i + 1}`, delayMs: 0, deadband: 0
}))

/** 记录写请求次数，并按 delay 延迟返回，让「进行中」有稳定的观察窗口。 */
function countWrites(page: Page, delay: number, pattern: RegExp) {
  const state = { calls: 0 }
  return { state, handler: async (route: import('@playwright/test').Route) => {
    state.calls++
    if (delay) await new Promise(resolve => setTimeout(resolve, delay))
    await route.fulfill({ json: {} })
  }, pattern }
}

test('测点批量删除可以中途停止', async ({ page, request }) => {
  await prepare(page, request)
  const writes = countWrites(page, 800, /^\/api\/tags\/\d+$/)
  await page.route('**/api/**', async route => {
    const { pathname, method } = { pathname: new URL(route.request().url()).pathname, method: route.request().method() }
    if (method === 'DELETE' && writes.pattern.test(pathname)) return writes.handler(route)
    if (pathname === '/api/tags') return route.fulfill({ json: tags() })
    if (pathname === '/api/devices') return route.fulfill({ json: devices().slice(0, 1) })
    return route.fulfill({ json: [] })
  })
  await page.goto('/config/tags')
  await page.locator('.el-table__body tbody tr').first().waitFor()
  await page.locator('.el-table__header .el-checkbox').first().click()
  await expect(page.locator('[data-test="tag-batch-delete"]')).toBeVisible()
  await page.locator('[data-test="tag-batch-delete"]').click()
  await page.locator('.el-message-box button.el-button--primary').click()

  const stop = page.locator('[data-test="tag-batch-stop"]')
  await expect(stop).toBeVisible()
  await stop.click()
  await expect(stop).toBeDisabled()

  await expect(page.locator('.el-message--info')).toContainText('已停止', { timeout: 15_000 })
  const done = writes.state.calls
  expect(done, '停止后不应继续下发').toBeLessThan(total)
  await expect(stop).toHaveCount(0)
  await expect(page.locator('[data-test="tag-batch-progress"]')).toHaveCount(0)
})

test('设备批量禁用可以中途停止', async ({ page, request }) => {
  await prepare(page, request)
  const writes = countWrites(page, 800, /^\/api\/devices\/\d+$/)
  await page.route('**/api/**', async route => {
    const { pathname, method } = { pathname: new URL(route.request().url()).pathname, method: route.request().method() }
    if (method === 'PUT' && writes.pattern.test(pathname)) return writes.handler(route)
    if (pathname === '/api/devices') return route.fulfill({ json: devices() })
    return route.fulfill({ json: [] })
  })
  await page.goto('/config/devices')
  await page.locator('.el-table__body tbody tr').first().waitFor()
  await page.locator('.el-table__header .el-checkbox').first().click()
  await page.locator('[data-test="device-batch-disable"]').click()

  const stop = page.locator('[data-test="device-batch-stop"]')
  await expect(stop).toBeVisible()
  await stop.click()
  await expect(page.locator('.el-message--info')).toContainText('已停止', { timeout: 15_000 })
  expect(writes.state.calls, '停止后不应继续下发').toBeLessThan(total)
  await expect(stop).toHaveCount(0)
})

test('报警规则批量删除可以中途停止', async ({ page, request }) => {
  await prepare(page, request)
  const writes = countWrites(page, 800, /^\/api\/alarm-rules\/\d+$/)
  await page.route('**/api/**', async route => {
    const { pathname, method } = { pathname: new URL(route.request().url()).pathname, method: route.request().method() }
    if (method === 'DELETE' && writes.pattern.test(pathname)) return writes.handler(route)
    if (pathname === '/api/alarm-rules') return route.fulfill({ json: rules() })
    if (pathname === '/api/tags') return route.fulfill({ json: tags().slice(0, 1) })
    if (pathname === '/api/devices') return route.fulfill({ json: devices().slice(0, 1) })
    if (pathname === '/api/alarm-shelves') return route.fulfill({ json: [] })
    return route.fulfill({ json: [] })
  })
  await page.goto('/config/rules')
  await page.locator('.el-table__body tbody tr').first().waitFor()
  await page.locator('.el-table__header .el-checkbox').first().click()
  await page.locator('[data-test="rule-batch-delete"]').click()
  await page.locator('.el-message-box button.el-button--primary').click()

  const stop = page.locator('[data-test="rule-batch-stop"]')
  await expect(stop).toBeVisible()
  await stop.click()
  await expect(page.locator('.el-message--info')).toContainText('已停止', { timeout: 15_000 })
  expect(writes.state.calls, '停止后不应继续下发').toBeLessThan(total)
  await expect(stop).toHaveCount(0)
})
