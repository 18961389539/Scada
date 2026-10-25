import { test, expect, type APIRequestContext } from '@playwright/test'

async function login(request: APIRequestContext) {
  const r = await request.post('/api/auth/login', { data: { userName: 'admin', password: 'admin123' } })
  expect(r.ok(), await r.text()).toBeTruthy()
  return (await r.json()) as { token: string }
}

// 趋势页易用性：空状态区分「没选点」与「没数据」、预置范围高亮、已选计数与清空、自动刷新开关。
test('trends usability: empty hints, preset highlight, selected count and auto refresh', async ({ page, request }) => {
  test.setTimeout(120000)
  const session = await login(request)
  const h = { Authorization: `Bearer ${session.token}` }
  const suffix = `${Date.now()}`
  const post = async (url: string, data: unknown) => {
    const r = await request.post(url, { headers: h, data })
    expect(r.ok(), await r.text()).toBeTruthy()
    return r.json()
  }

  // 设备停用 → 不采集 → 区间内无历史数据，正好验证空状态文案
  const device = await post('/api/devices', {
    name: `UX-TR-${suffix}`, protocol: 'ModbusTcp', host: '127.0.0.1', port: 1502,
    stationNumber: 1, byteOrder: 'ABCD', pollIntervalMs: 1000, timeoutMs: 3000, enabled: false, areaId: 1
  })
  const tag = await post('/api/tags', {
    deviceId: device.id, name: `UX-TR-A-${suffix}`, area: 'HoldingRegister', address: 10,
    dataType: 'Float', scale: 1, offset: 0, writable: false, archiveMode: 'OnChange', deadband: 0, archivePeriodSec: 600
  })

  await page.goto('/login')
  await page.getByPlaceholder(/用户名/).fill('admin')
  await page.getByPlaceholder(/密码/).fill('admin123')
  await page.keyboard.press('Enter')
  await expect(page).toHaveURL(/overview/)
  await page.goto('/trends')

  // 清空默认选择：此时应提示「先选择测点」而不是空白
  const empty = page.locator('[data-test="chart-empty"]')
  await page.locator('[data-test="clear-tags"]').click()
  await expect(page.locator('[data-test="selected-count"]')).toContainText('已选 0')
  await expect(empty).toContainText('请先选择测点')

  // 选中刚建的测点再查询：有测点但无数据，文案应切换
  await page.locator('[data-test="tag-picker"]').click()
  await page.getByRole('option', { name: new RegExp(tag.name) }).click()
  await page.keyboard.press('Escape')
  await expect(page.locator('[data-test="selected-count"]')).toContainText('已选 1')
  await page.getByRole('button', { name: '查询', exact: true }).click()
  await expect(empty).toContainText('该时间段没有历史数据')

  // 预置范围有选中态：点「24 小时」后按钮变主色
  const preset24 = page.getByRole('button', { name: '24 小时', exact: true })
  await preset24.click()
  await expect(preset24).toHaveClass(/el-button--primary/)

  // 自动刷新默认关闭，可切换
  const auto = page.locator('[data-test="auto-refresh"]')
  await expect(auto).not.toHaveClass(/is-checked/)
  await auto.click()
  await expect(auto).toHaveClass(/is-checked/)
})
