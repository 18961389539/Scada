import { test, expect, type APIRequestContext } from '@playwright/test'

async function login(request: APIRequestContext, userName: string, password: string) {
  const response = await request.post('/api/auth/login', { data: { userName, password } })
  expect(response.ok(), await response.text()).toBeTruthy()
  const result = await response.json()
  return { ...result, headers: { Authorization: `Bearer ${result.token}` } }
}

// 历史分析与回放：时间加权统计/质量区间、接口限制与区域授权、趋势页分析表与回放光标。
test('history analysis: weighted stats, quality intervals, limits, area authorization and playback UI', async ({ page, request }) => {
  test.setTimeout(120000)
  const admin = (await login(request, 'admin', 'admin123')).headers
  const suffix = Date.now()
  const post = async (url: string, data: unknown) => {
    const r = await request.post(url, { headers: admin, data })
    expect(r.ok(), await r.text()).toBeTruthy()
    return r.json()
  }

  // 计算测点持续产出 50，归档后即为可分析的原始历史
  const calc = await post('/api/calculated-tags', { name: `E2E-hist-${suffix}`, expression: '50', intervalMs: 200 })
  await expect.poll(async () => {
    const r = await (await request.get(`/api/history?tagIds=${calc.id}`, { headers: admin })).json()
    return r.series?.[0]?.points?.length ?? 0
  }, { timeout: 30000, message: '等待计算测点归档出历史' }).toBeGreaterThan(0)

  const to = Date.now()
  const from = to - 3600_000
  const externalResponse = await request.get(`/api/v1/history?tagIds=${calc.id}&from=${from}&to=${to}`, { headers: admin })
  expect(externalResponse.ok(), await externalResponse.text()).toBeTruthy()
  const external = await externalResponse.json()
  expect(external.pointLimit).toBe(20000)
  expect(typeof external.downsampled).toBe('boolean')
  expect(external.series.reduce((count: number, item: { points: unknown[] }) => count + item.points.length, 0)).toBeLessThanOrEqual(20000)
  const analysis = await (await request.get(`/api/history/analysis?tagIds=${calc.id}&from=${from}&to=${to}`, { headers: admin })).json()
  expect(analysis.resolution).toBe('Raw')
  expect(analysis.from).toBe(from)
  expect(analysis.to).toBe(to)
  const row = analysis.tags[0]
  expect(row.tagId).toBe(calc.id)
  expect(row.samples).toBeGreaterThan(0)
  expect(row.goodSamples).toBe(row.samples)
  expect(row.coverage).toBeGreaterThan(0)
  expect(row.timeWeightedAvg).toBeCloseTo(50, 3)
  expect(row.min).toBeCloseTo(50, 3)
  expect(row.max).toBeCloseTo(50, 3)
  expect(row.integralHours).toBeGreaterThan(0)
  expect(row.quality[0].quality).toBe('Good')
  expect(row.qualityTruncated).toBe(false)

  // 没有采样的区间：给出跨度与空统计，而不是报错
  const empty = await (await request.get(`/api/history/analysis?tagIds=${calc.id}&from=${from - 7200_000}&to=${from - 3600_000}`, { headers: admin })).json()
  expect(empty.tags[0].samples).toBe(0)
  expect(empty.tags[0].timeWeightedAvg).toBeNull()
  expect(empty.tags[0].quality).toEqual([])

  // 限制：单次 20 个测点、区间 24 小时、时间顺序、必须给测点
  const many = Array.from({ length: 21 }, (_, i) => calc.id + i).join(',')
  expect((await request.get(`/api/history/analysis?tagIds=${many}`, { headers: admin })).status()).toBe(400)
  expect((await request.get(`/api/history/analysis?tagIds=${calc.id}&from=${to - 25 * 3600_000}&to=${to}`, { headers: admin })).status()).toBe(400)
  expect((await request.get(`/api/history/analysis?tagIds=${calc.id}&from=${to}&to=${from}`, { headers: admin })).status()).toBe(400)
  expect((await request.get('/api/history/analysis', { headers: admin })).status()).toBe(400)

  // 区域授权：只授权 A 区的操作员不能分析 B 区测点
  const areaA = await post('/api/areas', { name: `E2E-HA-${suffix}` })
  const areaB = await post('/api/areas', { name: `E2E-HB-${suffix}` })
  const deviceBody = (name: string, areaId: number) => ({
    name, protocol: 'ModbusTcp', host: '127.0.0.1', port: 1502, stationNumber: 1, byteOrder: 'ABCD',
    pollIntervalMs: 1000, timeoutMs: 500, enabled: false, areaId
  })
  const devB = await post('/api/devices', deviceBody(`E2E-HB-dev-${suffix}`, areaB.id))
  const tagB = await post('/api/tags', {
    deviceId: devB.id, name: `E2E-HB-tag-${suffix}`, area: 'HoldingRegister', address: 0, dataType: 'Float',
    scale: 1, offset: 0, writable: false, archiveMode: 'None', deadband: 0, archivePeriodSec: 600
  })
  const role = await post('/api/roles', { name: `E2E历史员-${suffix}`, permissions: ['View'] })
  const user = await post('/api/users', { userName: `e2e-hist-${suffix}`, password: 'secret123', displayName: 'A区查看', role: 'None', enabled: true })
  expect((await request.put(`/api/users/${user.id}/assignments`, { headers: admin, data: { assignments: [{ roleId: role.id, areaId: areaA.id }] } })).ok()).toBeTruthy()
  const scoped = await login(request, user.userName, 'secret123')
  expect((await request.get(`/api/history/analysis?tagIds=${tagB.id}`, { headers: scoped.headers })).status()).toBe(403)
  expect((await request.get(`/api/history/analysis?tagIds=${calc.id}`, { headers: scoped.headers })).status()).toBe(403)

  // UI：趋势页给出分析表、质量条与回放光标
  await page.goto('/login')
  await page.getByPlaceholder(/用户名/).fill('admin')
  await page.getByPlaceholder(/密码/).fill('admin123')
  await page.keyboard.press('Enter')
  await expect(page).toHaveURL(/overview/)
  await page.goto('/trends')
  await page.locator('[data-test="clear-tags"]').click()
  await page.locator('[data-test="tag-picker"]').click()
  await page.getByRole('option', { name: new RegExp(calc.name) }).click()
  await page.keyboard.press('Escape')
  await page.getByRole('button', { name: '查询', exact: true }).click()

  const analysisPanel = page.locator('[data-test="history-analysis"]')
  await expect(analysisPanel).toBeVisible()
  await expect(page.locator('[data-test="analysis-table"]')).toContainText('50')
  await expect(page.locator(`[data-test="quality-${calc.id}"]`)).toBeVisible()

  const playback = page.locator('[data-test="playback-bar"]')
  await expect(playback).toBeVisible()
  // 光标默认在区间起点，此时还没有采样：取值条要明确说明，而不是显示空白
  const values = page.locator('[data-test="playback-values"]')
  await expect(values).toContainText('该时刻之前无数据')

  // 点击图表最右端定位到区间末尾，取值条给出该时刻的保持值
  const chart = page.locator('[data-test="trend-chart"]')
  const box = await chart.boundingBox()
  expect(box).toBeTruthy()
  await chart.click({ position: { x: box!.width - 6, y: box!.height / 2 } })
  await expect(values).toContainText('50')

  const cursor = page.locator('[data-test="cursor-time"]')
  // 先复位到区间起点，作为回放推进的基准（togglePlay 在末尾会回起点，不能拿点击后的值当基准）
  await page.locator('[data-test="playback-reset"]').click()
  const start = await cursor.textContent()
  // 1× 下光标在几百毫秒里几乎不动，暂停值和复位值会同秒；切 300× 让它真正推进几分钟
  await page.locator('[data-test="playback-speed"]').click()
  await page.getByRole('option', { name: '300×', exact: true }).click()
  await page.locator('[data-test="playback-toggle"]').click()
  await expect.poll(async () => cursor.textContent(), { timeout: 5000, message: '回放光标应随播放前进' }).not.toBe(start)
  await page.locator('[data-test="playback-toggle"]').click()
  const paused = await cursor.textContent()
  expect(paused).not.toBe(start)
  await page.waitForTimeout(400)
  expect(await cursor.textContent()).toBe(paused)
  await page.locator('[data-test="playback-reset"]').click()
  expect(await cursor.textContent()).toBe(start)
})
