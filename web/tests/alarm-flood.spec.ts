import { test, expect, type APIRequestContext } from './fixtures'
import { startStandaloneServer, type StandaloneServer } from './standalone-server'

async function login(request: APIRequestContext, baseUrl: string, userName: string, password: string) {
  const response = await request.post(`${baseUrl}/api/auth/login`, { data: { userName, password } })
  expect(response.ok(), await response.text()).toBeTruthy()
  const result = await response.json()
  return { ...result, headers: { Authorization: `Bearer ${result.token}` } }
}

// 报警洪水治理：10 分钟内超过阈值即进入洪水，最早一条标为首出，低等级自动抑制而 Critical 仍发声。
// 用独立实例跑：洪水会留下十几条不可删除的报警事件，共享服务器上会把后面的用例（KPI Top、等级筛选、空状态）冲掉。
let server: StandaloneServer
let base: string

test.beforeAll(async () => {
  server = await startStandaloneServer(18591)
  base = server.baseUrl
})

test.afterAll(async () => {
  await server?.stop()
})

test('alarm flood: per-area hysteresis, first-out marking, auto suppression and UI banner', async ({ page, request }) => {
  test.setTimeout(150000)
  const admin = (await login(request, base, 'admin', 'admin123')).headers
  const suffix = Date.now()
  const post = async (url: string, data: unknown) => {
    const r = await request.post(base + url, { headers: admin, data })
    expect(r.ok(), await r.text()).toBeTruthy()
    return r.json()
  }

  // 14 条计算测点恒为 50，高限 10 立即报警：默认阈值 10/10 分钟，足以触发洪水
  const critical = await post('/api/calculated-tags', { name: `E2E-flood-crit-${suffix}`, expression: '50', intervalMs: 200 })
  await post('/api/alarm-rules', { tagId: critical.id, type: 'High', threshold: 10, level: 'Critical', delaySec: 0, recoverDeadband: 0, enabled: true, message: `E2E洪水严重-${suffix}` })
  const lowIds: number[] = []
  for (let i = 0; i < 13; i++) {
    const calc = await post('/api/calculated-tags', { name: `E2E-flood-${i}-${suffix}`, expression: '50', intervalMs: 200 })
    await post('/api/alarm-rules', { tagId: calc.id, type: 'High', threshold: 10, level: i % 2 ? 'Info' : 'Warning', delaySec: 0, recoverDeadband: 0, enabled: true, message: `E2E洪水低阶-${i}-${suffix}` })
    lowIds.push(calc.id)
  }

  const summary = async () => (await request.get(`${base}/api/alarms/summary`, { headers: admin })).json()
  await expect.poll(async () => (await summary()).floodGuard?.anyInFlood, { timeout: 60000, message: '等待进入洪水' }).toBe(true)

  const guard = (await summary()).floodGuard
  expect(guard.autoSuppress).toBe(true)
  expect(guard.firstOut).toBe(true)
  expect(guard.suppressMaxLevel).toBe('Warning')
  const area = guard.areas.find((x: any) => x.inFlood)
  expect(area.count).toBeGreaterThan(area.threshold)
  expect(area.threshold).toBe(10)
  expect(area.exitThreshold).toBe(5)
  expect(area.areaId).toBeNull()
  expect(area.areaName).toBeNull()
  expect(area.peak).toBeGreaterThanOrEqual(area.count)
  expect(area.floodStartTs).toBeGreaterThan(0)
  expect(area.suppressedCount).toBeGreaterThan(0)
  expect(area.firstOutEventId).toBeGreaterThan(0)
  expect(area.windowEndTs - area.windowStartTs).toBe(600_000)

  const live = await (await request.get(`${base}/api/alarms?live=true&limit=1000`, { headers: admin })).json()
  const flood = live.filter((x: any) => (x.suppressReason ?? '').startsWith('报警洪水'))
  expect(flood.length).toBeGreaterThan(0)
  const firstOut = live.filter((x: any) => x.firstOut)
  expect(firstOut.length).toBe(1)
  expect(firstOut[0].id).toBe(area.firstOutEventId)
  expect(firstOut[0].suppressed).toBe(false)
  // Critical 永不因洪水静音；首出只看时间最早，与等级无关
  const criticalAlarm = live.find((x: any) => x.message === `E2E洪水严重-${suffix}`)
  expect(criticalAlarm.suppressed).toBe(false)
  // 被抑制的都是 Warning/Info
  expect(flood.every((x: any) => x.level !== 'Critical')).toBe(true)
  expect(flood.every((x: any) => x.endTs === null)).toBe(true)

  // 时间线记录首出与抑制动作
  const timeline = await (await request.get(`${base}/api/alarms/${area.firstOutEventId}/timeline`, { headers: admin })).json()
  expect(JSON.stringify(timeline)).toContain('洪水首出')
  const suppressedTimeline = await (await request.get(`${base}/api/alarms/${flood[0].id}/timeline`, { headers: admin })).json()
  expect(JSON.stringify(suppressedTimeline)).toContain('报警洪水')

  // UI：报警页顶部横幅说明治理策略，首出行有标记，抑制筛选能看到被压制的报警
  await page.goto(`${base}/login`)
  await page.getByPlaceholder(/用户名/).fill('admin')
  await page.getByPlaceholder(/密码/).fill('admin123')
  await page.keyboard.press('Enter')
  await expect(page).toHaveURL(/overview/)
  await page.goto(`${base}/alarms`)

  const banner = page.locator('[data-test="flood-banner"]')
  await expect(banner).toBeVisible()
  await expect(banner).toContainText('报警洪水治理')
  await expect(banner).toContainText('未分区')
  await expect(banner).toContainText('已自动抑制')
  await expect(banner).toContainText(`首出 #${area.firstOutEventId}`)

  const firstRow = page.getByRole('row').filter({ hasText: firstOut[0].message })
  await expect(firstRow.locator('[data-test="first-out"]')).toBeVisible()
  await page.locator('[data-test="filter-suppressed"]').click()
  await expect(page.getByRole('row').filter({ hasText: 'E2E洪水低阶-' }).first()).toBeVisible()
  expect(lowIds.length).toBe(13)
})
