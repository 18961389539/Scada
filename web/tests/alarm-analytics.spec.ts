import { test, expect, type APIRequestContext } from '@playwright/test'

async function login(request: APIRequestContext, userName: string, password: string) {
  const response = await request.post('/api/auth/login', { data: { userName, password } }); expect(response.ok(), await response.text()).toBeTruthy()
  const result = await response.json(); return { ...result, headers: { Authorization: `Bearer ${result.token}` } }
}

test('alarm analytics dashboard: KPIs, chattering, drill-down, thresholds, export, alarm KPI report template', async ({ page, request }) => {
  test.setTimeout(120000)
  const admin = (await login(request, 'admin', 'admin123')).headers, suffix = Date.now()
  const post = async (url: string, data: unknown) => { const r = await request.post(url, { headers: admin, data }); expect(r.ok(), await r.text()).toBeTruthy(); return r.json() }

  // 计算测点恒为 50、高限 10：停用/恢复规则 3 次，在 60 秒内产生 3 次报警（抖动）。
  const calc = await post('/api/calculated-tags', { name: `E2E-aa-${suffix}`, expression: '50', intervalMs: 200 })
  const rule = await post('/api/alarm-rules', { tagId: calc.id, type: 'High', threshold: 10, level: 'Warning', delaySec: 0, recoverDeadband: 0, enabled: true, message: `E2E分析-${suffix}` })
  const mine = async () => (await (await request.get(`/api/alarms?from=${suffix - 60000}&limit=5000`, { headers: admin })).json()).filter((x: any) => x.ruleId === rule.id)
  for (let i = 1; i <= 3; i++) {
    await expect.poll(async () => (await mine()).filter((x: any) => !x.endTs).length, { timeout: 20000 }).toBe(1)
    if (i < 3) {
      await post(`/api/alarm-rules/${rule.id}/out-of-service`, { outOfService: true, reason: 'E2E' })
      await expect.poll(async () => (await mine()).filter((x: any) => !x.endTs).length, { timeout: 20000 }).toBe(0)
      await post(`/api/alarm-rules/${rule.id}/out-of-service`, { outOfService: false, reason: 'E2E' })
    }
  }
  expect((await mine()).length).toBe(3)

  await page.goto('/login')
  await page.getByPlaceholder(/用户名/).fill('admin'); await page.getByPlaceholder(/密码/).fill('admin123'); await page.keyboard.press('Enter')
  await expect(page).toHaveURL(/overview/)
  await page.goto('/alarm-analytics')
  await expect(page.locator('[data-test="kpi-total"] .value')).not.toHaveText('0')
  await expect(page.locator('[data-test="aa-rate-chart"] canvas')).toBeVisible()
  await expect(page.locator('[data-test="aa-pareto-chart"] canvas')).toBeVisible()
  const topRow = page.locator('[data-test="kpi-top"]').getByRole('row').filter({ hasText: `E2E分析-${suffix}` })
  await expect(topRow).toBeVisible()

  // 抖动页签列出该规则，合理化建议给出恢复延时建议（仅建议）。
  await page.getByRole('tab', { name: /抖动/ }).click()
  await expect(page.locator('[data-test="aa-chatter"]').getByText(`E2E分析-${suffix}`)).toBeVisible()
  await page.getByRole('tab', { name: /合理化建议/ }).click()
  const suggestion = page.locator('[data-test="aa-suggestions"]').getByRole('row').filter({ hasText: `E2E分析-${suffix}` }).first()
  await expect(suggestion).toContainText('抖动')
  await expect(page.getByText('不会自动修改任何报警规则')).toBeVisible()

  // 下钻：点击 Top 表格行打开明细。
  await page.getByRole('tab', { name: '最频繁' }).click()
  await topRow.click()
  await expect(page.locator('[data-test="aa-drill-table"]').getByRole('row').filter({ hasText: `E2E分析-${suffix}` })).toHaveCount(3)
  await page.keyboard.press('Escape')

  // 按等级筛选：只看严重时该规则不出现。
  await page.locator('[data-test="aa-levels"]').click()
  await page.getByRole('option', { name: '严重' }).click()
  await page.keyboard.press('Escape')
  await page.locator('[data-test="aa-query"]').click()
  await expect(page.locator('[data-test="kpi-top"]').getByText(`E2E分析-${suffix}`)).toHaveCount(0)
  await page.locator('[data-test="aa-levels"]').click()
  await page.getByRole('option', { name: '严重' }).click()
  await page.keyboard.press('Escape')
  await page.locator('[data-test="aa-query"]').click()
  await expect(topRow).toBeVisible()

  // 导出 Excel。
  const [download] = await Promise.all([page.waitForEvent('download'), page.locator('[data-test="aa-export-xlsx"]').click()])
  expect(download.suggestedFilename()).toMatch(/\.xlsx$/)

  // 阈值：修改瞬时秒数并保存（记审计），再恢复。
  await page.locator('[data-test="aa-thresholds"]').click()
  const fleeting = page.locator('[data-test="th-fleetingSec"] input')
  await fleeting.fill('15'); await fleeting.blur()
  await page.locator('[data-test="th-save"]').click()
  await expect(page.getByText('阈值已保存')).toBeVisible()
  const saved = await (await request.get('/api/alarm-analytics/thresholds', { headers: admin })).json()
  expect(saved.thresholds.fleetingSec).toBe(15)
  const r = await request.put('/api/alarm-analytics/thresholds', { headers: admin, data: { thresholds: { ...saved.thresholds, fleetingSec: 10 }, revision: saved.revision } })
  expect(r.ok()).toBeTruthy()

  // 报表中心：报警 KPI 预设列 → 保存 → 生成（与其他报表一样归档/签名）。
  await page.goto('/report-center')
  await page.locator('[data-test="report-template-new"]').click()
  await page.locator('[data-test="report-alarm-kpi-preset"]').click()
  await expect(page.locator('[data-test="report-kpi-metric-0"]')).toBeVisible()
  await page.locator('[data-test="report-template-name"]').fill(`报警KPI-${suffix}`)
  await page.locator('[data-test="report-template-save"]').click()
  await expect(page.getByText(`报警KPI-${suffix}`)).toBeVisible()
  const templates = await (await request.get('/api/report-templates', { headers: admin })).json()
  const tpl = templates.find((x: any) => x.name === `报警KPI-${suffix}`)
  expect(tpl.columns.every((c: any) => c.kind === 'AlarmKpi')).toBe(true)
  const run = await post(`/api/report-templates/${tpl.id}/generate`, { at: Date.now() })
  const detail = await (await request.get(`/api/report-runs/${run.id}`, { headers: admin })).json()
  // 第 3 次触发被报警引擎标为抖动，不计入需报警数。
  expect(detail.table.summary.values[0]).toBeGreaterThanOrEqual(2)

  // 英文界面：仪表盘文案、评级与建议类型均已翻译。
  await page.goto('/alarm-analytics')
  await page.evaluate(() => localStorage.setItem('scada_locale', 'en-US'))
  await page.reload()
  await expect(page.getByRole('tab', { name: 'Most frequent' })).toBeVisible()
  await expect(page.locator('[data-test="kpi-rate10"]')).toContainText('Per operator / 10 min')
  await page.getByRole('tab', { name: /Rationalization/ }).click()
  await expect(page.locator('[data-test="aa-suggestions"]').getByText('Chattering').first()).toBeVisible()
  await page.evaluate(() => localStorage.setItem('scada_locale', 'zh-CN'))

  await request.delete(`/api/alarm-rules/${rule.id}`, { headers: admin })
})

test('failed analytics query shows the error, and an unacked standing alarm is not labeled stale', async ({ page, request }) => {
  const auth = await login(request, 'admin', 'admin123')
  await page.addInitScript(({ token, user }) => {
    localStorage.setItem('scada_token', token)
    localStorage.setItem('scada_user', JSON.stringify(user))
  }, auth)
  let fail = true
  const standing = { id: 7, source: 'rule:7', ruleId: 7, tagId: null, message: '未确认驻留', level: 'Warning', startTs: Date.now(), hours: 30, acked: false }
  const thresholds = {
    operatorPositions: 1, acceptablePer10Min: 1, manageablePer10Min: 2, floodStartPer10Min: 10, floodEndPer10Min: 5,
    chatterCount: 3, chatterWindowSec: 60, fleetingSec: 10, standingHours: 24, staleHours: 168,
    consequentialWindowSec: 5, consequentialMinCount: 2, consequentialMinRatio: 0.5, frequentPerDay: 10,
    targetLowPercent: 80, targetMediumPercent: 15, targetHighPercent: 5, topN: 10
  }
  const report = {
    kpi: {
      total: 1, annunciated: 1, standing: [standing], stale: [], floods: [], priority: [], top: [],
      chatteringSources: [], fleeting: [], fleetingTotal: 0, consequential: [], shelvedByRule: [],
      operatorPositions: 1, perOperator10Min: 0, perOperatorHour: 0, perOperatorDay: 0, peak10Min: 0,
      percentTimeInFlood: 0, avgAckSec: null, medianAckSec: null, avgClearSec: null, medianClearSec: null,
      shelveCount: 0, shelvedHours: 0, suppressed: 0, suppressedHours: 0, rating: 'acceptable', chattering: 0
    },
    detailFrom: Date.now(), usedRollups: false, truncated: false, rate10: [], suggestions: [], thresholds
  }
  await page.route((url) => url.pathname === '/api/alarm-analytics' || url.pathname === '/api/alarm-analytics/trend', (route) => {
    if (fail) return route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message: '分析服务不可用' }) })
    const path = new URL(route.request().url()).pathname
    const body = path.endsWith('/trend') ? { granularity: 'hour', points: [] } : report
    return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) })
  })
  await page.goto('/alarm-analytics')
  const empty = page.locator('[data-test="aa-empty"]')
  await expect(empty).toContainText('分析服务不可用')
  await expect(empty).not.toContainText('该时间范围内没有报警数据')

  fail = false
  await page.locator('[data-test="aa-query"]').click()
  await page.getByRole('tab', { name: /长期驻留/ }).click()
  const row = page.locator('[data-test="aa-standing"]').getByRole('row').filter({ hasText: '未确认驻留' })
  await expect(row).toContainText('否')
  await expect(row).not.toContainText('未确认（陈旧）')
})
