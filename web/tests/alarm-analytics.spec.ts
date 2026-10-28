import { test, expect, type APIRequestContext, type Page } from '@playwright/test'

async function login(request: APIRequestContext, userName: string, password: string) {
  const response = await request.post('/api/auth/login', { data: { userName, password } }); expect(response.ok(), await response.text()).toBeTruthy()
  const result = await response.json(); return { ...result, headers: { Authorization: `Bearer ${result.token}` } }
}

function reviewReport(from: number, to: number, count = 10) {
  const thresholds = { operatorPositions: 1, acceptablePer10Min: 1, manageablePer10Min: 2, floodStartPer10Min: 10, floodEndPer10Min: 5, chatterCount: 3, chatterWindowSec: 60, fleetingSec: 10, standingHours: 24, staleHours: 168, consequentialWindowSec: 5, consequentialMinCount: 2, consequentialMinRatio: 0.5, frequentPerDay: 10, targetLowPercent: 80, targetMediumPercent: 15, targetHighPercent: 5, topN: 10 }
  return { thresholds, detailFrom: from, usedRollups: false, truncated: false, suggestions: [], rate10: [{ startTs: from, count }], kpi: {
    from, to, total: count, annunciated: count, standing: [], stale: [], floods: [], priority: [{ level: 'Critical', count, targetPercent: 5 }], top: [{ source: 'rule:1', ruleId: 1, message: '测试报警源', level: 'Critical', count, annunciated: count, cumulativePercent: 100 }],
    chatteringSources: [], fleeting: [], fleetingTotal: 0, consequential: [], shelvedByRule: [], operatorPositions: 1, perOperator10Min: 1, perOperatorHour: 6, perOperatorDay: 144, peak10Min: count, percentTimeInFlood: 0,
    avgAckSec: null, medianAckSec: null, avgClearSec: null, medianClearSec: null, shelveCount: 0, shelvedHours: 0, suppressed: 0, suppressedHours: 0, rating: 'acceptable', chattering: 0
  } }
}
function gate() {
  let release!: () => void
  return { promise: new Promise<void>(resolve => { release = resolve }), release: () => release() }
}
async function prepareReview(page: Page, request: APIRequestContext) {
  const auth = await login(request, 'admin', 'admin123')
  await page.addInitScript(({ token, user }) => { localStorage.setItem('scada_token', token); localStorage.setItem('scada_user', JSON.stringify(user)) }, auth)
  await page.route(url => url.pathname === '/api/areas', route => route.fulfill({ json: [{ id: 1, name: '一车间' }] }))
  await page.route(url => url.pathname === '/api/alarm-rules', route => route.fulfill({ json: [{ id: 1, message: '测试报警源' }] }))
  await page.route(url => url.pathname === '/api/alarm-analytics' || url.pathname === '/api/alarm-analytics/trend', route => {
    const url = new URL(route.request().url()), from = Number(url.searchParams.get('from')), to = Number(url.searchParams.get('to'))
    return route.fulfill({ json: url.pathname.endsWith('/trend') ? { granularity: 'hour', points: [] } : reviewReport(from, to) })
  })
  await page.goto('/alarm-analytics')
  await expect(page.locator('[data-test="kpi-total"] .value')).toHaveText('10')
  await expect(page.locator('[data-test="aa-export-xlsx"]')).toBeEnabled()
}

for (const outcome of ['success', 'failure'] as const) {
  test(`other pages review ignores a late analytics ${outcome} after a new range wins`, async ({ page, request }) => {
    await prepareReview(page, request)
    const pending = gate()
    await page.route(url => url.pathname === '/api/alarm-analytics', async route => {
      const params = new URL(route.request().url()).searchParams, from = Number(params.get('from')), to = Number(params.get('to'))
      if (to - from < 2 * 86400000) {
        await pending.promise
        return route.fulfill(outcome === 'failure' ? { status: 500, json: { message: '旧分析不可用' } } : { json: reviewReport(from, to, 11) })
      }
      return route.fulfill({ json: reviewReport(from, to, 22) })
    })
    const started = page.waitForRequest(request => new URL(request.url()).pathname === '/api/alarm-analytics')
    await page.locator('[data-test="aa-query"]').click(); await started
    try {
      await page.getByRole('button', { name: '7 天', exact: true }).click()
      await expect(page.locator('[data-test="kpi-total"] .value')).toHaveText('22')
      const context = (await page.locator('[data-test="aa-result-context"]').textContent())!
      const done = page.waitForResponse(response => new URL(response.url()).pathname === '/api/alarm-analytics' && Number(new URL(response.url()).searchParams.get('to')) - Number(new URL(response.url()).searchParams.get('from')) < 2 * 86400000)
      pending.release(); await (await done).finished()
      await page.evaluate(() => new Promise(requestAnimationFrame))
      await expect(page.locator('[data-test="kpi-total"] .value')).toHaveText('22')
      await expect(page.locator('[data-test="aa-result-context"]')).toHaveText(context)
      await expect(page.locator('[data-test="aa-query-error"]')).toHaveCount(0)
    } finally { pending.release() }
  })
}

test('other pages review requires a successful matching analytics query before export and prevents duplicate downloads', async ({ page, request }) => {
  await prepareReview(page, request)
  const context = (await page.locator('[data-test="aa-result-context"]').textContent())!
  await page.locator('[data-test="aa-area"]').click()
  await page.getByRole('option', { name: '一车间', exact: true }).click()
  await expect(page.locator('[data-test="aa-pending-conditions"]')).toBeVisible()
  await expect(page.locator('[data-test="aa-result-context"]')).toHaveText(context)
  await expect(page.locator('[data-test="aa-export-xlsx"]')).toBeDisabled()
  await page.locator('[data-test="aa-query"]').click()
  await expect(page.locator('[data-test="aa-result-context"]')).toContainText('一车间')
  let fail = true
  await page.route(url => url.pathname === '/api/alarm-analytics', route => {
    if (fail) return route.fulfill({ status: 500, json: { message: '新分析不可用' } })
    const params = new URL(route.request().url()).searchParams
    return route.fulfill({ json: reviewReport(Number(params.get('from')), Number(params.get('to'))) })
  })
  await page.locator('[data-test="aa-query"]').click()
  await expect(page.locator('[data-test="aa-query-error"]')).toContainText('新分析不可用')
  await expect(page.locator('[data-test="kpi-total"] .value')).toHaveText('10')
  await expect(page.locator('[data-test="aa-export-xlsx"]')).toBeDisabled()
  fail = false
  await page.locator('[data-test="aa-query"]').click()
  await expect(page.locator('[data-test="aa-export-xlsx"]')).toBeEnabled()
  const pending = gate()
  let exports = 0
  await page.route(url => url.pathname === '/api/alarm-analytics/export', async route => {
    exports++
    expect(new URL(route.request().url()).searchParams.get('areaId')).toBe('1')
    await pending.promise
    return route.fulfill({ contentType: 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet', body: Buffer.from('analytics fixture') })
  })
  await page.locator('[data-test="aa-export-xlsx"]').click()
  try {
    await expect.poll(() => exports).toBe(1)
    await page.locator('[data-test="aa-export-pdf"]').dispatchEvent('click')
    expect(exports).toBe(1)
    const download = page.waitForEvent('download')
    pending.release(); await download
    await expect(page.locator('[data-test="aa-export-xlsx"]')).toBeEnabled()
  } finally { pending.release() }
})

test('other pages review locks threshold writes and retains failed drafts for retry', async ({ page, request }) => {
  await prepareReview(page, request)
  const thresholds = reviewReport(0, 1).thresholds, pending = gate()
  let writes = 0
  await page.route(url => url.pathname === '/api/alarm-analytics/thresholds', async route => {
    if (route.request().method() === 'GET') return route.fulfill({ json: { thresholds, defaults: thresholds, revision: 3 } })
    writes++
    expect(route.request().postDataJSON()).toEqual({ thresholds: { ...thresholds, fleetingSec: 15 }, revision: 3 })
    if (writes === 1) { await pending.promise; return route.fulfill({ status: 500, json: { message: '阈值暂不可保存' } }) }
    return route.fulfill({ json: { thresholds: { ...thresholds, fleetingSec: 15 }, revision: 4 } })
  })
  await page.locator('[data-test="aa-thresholds"]').click()
  const dialog = page.getByRole('dialog', { name: '分析阈值', exact: true })
  const field = page.locator('[data-test="th-fleetingSec"] input')
  await field.fill('15'); await field.blur()
  await page.locator('[data-test="th-save"]').click()
  try {
    await expect.poll(() => writes).toBe(1)
    await expect(field).toBeDisabled()
    await expect(dialog.getByRole('button', { name: '取消', exact: true })).toBeDisabled()
    await expect(dialog.getByRole('button', { name: '恢复默认', exact: true })).toBeDisabled()
    await page.locator('[data-test="th-save"]').dispatchEvent('click')
    await page.keyboard.press('Escape')
    await expect(dialog).toBeVisible()
    expect(writes).toBe(1)
    pending.release()
    await expect(dialog.locator('[data-test="th-error"]')).toContainText('阈值暂不可保存')
    await expect(field).toHaveValue('15'); await expect(field).toBeEnabled()
    await page.locator('[data-test="th-save"]').click()
    await expect(dialog).toBeHidden()
    expect(writes).toBe(2)
  } finally { pending.release() }
})

for (const outcome of ['success', 'failure'] as const) {
  test(`other pages review ignores a closed analytics drill ${outcome} after another drill opens`, async ({ page, request }) => {
    await prepareReview(page, request)
    const pending = gate()
    await page.route(url => url.pathname === '/api/alarm-analytics/events', async route => {
      const old = new URL(route.request().url()).searchParams.has('source')
      if (old) { await pending.promise; if (outcome === 'failure') return route.fulfill({ status: 500, json: { message: '旧明细不可用' } }) }
      return route.fulfill({ json: { total: 1, items: [{ id: old ? 1 : 2, message: old ? '旧报警明细' : '新报警明细', source: 'rule:1', level: 'Critical', startTs: Date.now(), endTs: null, ackTs: null }] } })
    })
    const started = page.waitForRequest(request => new URL(request.url()).pathname === '/api/alarm-analytics/events')
    await page.locator('[data-test="kpi-top"]').getByText('测试报警源', { exact: true }).click(); await started
    const drawer = page.locator('[data-test="aa-drill"]')
    try {
      await page.keyboard.press('Escape'); await expect(drawer).toBeHidden()
      await page.locator('[data-test="kpi-fleeting"]').click()
      await expect(drawer).toContainText('新报警明细')
      const done = page.waitForResponse(response => new URL(response.url()).pathname === '/api/alarm-analytics/events' && new URL(response.url()).searchParams.has('source'))
      pending.release(); await (await done).finished()
      await page.evaluate(() => new Promise(requestAnimationFrame))
      await expect(drawer).toContainText('新报警明细')
      await expect(drawer).not.toContainText('旧报警明细')
      await expect(drawer.locator('[data-test="aa-drill-error"]')).toHaveCount(0)
    } finally { pending.release() }
  })
}

test('other pages review cancels a late analytics download after leaving the page', async ({ page, request }) => {
  await prepareReview(page, request)
  const pending = gate(), downloads: string[] = []
  page.on('download', download => downloads.push(download.suggestedFilename()))
  await page.route(url => url.pathname === '/api/alarm-analytics/export', async route => {
    await pending.promise
    return route.fulfill({ body: 'late analytics fixture', contentType: 'application/octet-stream' })
  })
  const started = page.waitForRequest(request => new URL(request.url()).pathname === '/api/alarm-analytics/export')
  await page.locator('[data-test="aa-export-xlsx"]').click(); await started
  try {
    await page.getByRole('menuitem', { name: '总览', exact: true }).click()
    await expect(page).toHaveURL(/\/overview$/)
    const done = page.waitForResponse(response => new URL(response.url()).pathname === '/api/alarm-analytics/export')
    pending.release(); await (await done).finished()
    await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))))
    expect(downloads).toEqual([])
  } finally { pending.release() }
})

test('other pages review drills a lone critical priority using its level rather than its array index', async ({ page, request }) => {
  await prepareReview(page, request)
  await page.route(url => url.pathname === '/api/alarm-analytics/events', route => route.fulfill({ json: { total: 0, items: [] } }))
  const canvas = page.locator('[data-test="aa-priority-chart"] canvas').first()
  await canvas.scrollIntoViewIfNeeded()
  const box = (await canvas.boundingBox())!
  const position = { x: box.width / 2 + Math.min(box.width, box.height) * 0.23, y: box.height * 0.45 }
  await expect.poll(() => canvas.evaluate((element: HTMLCanvasElement, point) => {
    const pixel = element.getContext('2d')!.getImageData(Math.round(point.x * element.width / element.clientWidth), Math.round(point.y * element.height / element.clientHeight), 1, 1).data
    return pixel[0]! > 150 && pixel[1]! < 140 && pixel[2]! < 140
  }, position)).toBe(true)
  const pending = page.waitForRequest(request => new URL(request.url()).pathname === '/api/alarm-analytics/events')
  await canvas.click({ position })
  const query = new URL((await pending).url()).searchParams
  expect(query.get('levels')).toBe('2')
  await expect(page.getByRole('dialog', { name: '严重', exact: true })).toBeVisible()
})

for (const width of [1600, 390, 320]) {
  test(`other pages review analytics controls and overlays fit at ${width}px`, async ({ page, request }) => {
    await page.setViewportSize({ width, height: 900 })
    await prepareReview(page, request)
    const thresholds = reviewReport(0, 1).thresholds
    await page.route(url => url.pathname === '/api/alarm-analytics/thresholds', route => route.fulfill({ json: { thresholds, defaults: thresholds, revision: 1 } }))
    await page.route(url => url.pathname === '/api/alarm-analytics/events', route => route.fulfill({ json: { total: 0, items: [] } }))
    const fit = async (selector: string) => expect(async () => {
      for (const element of await page.locator(selector).all()) {
        const box = await element.boundingBox()
        expect(box, selector).not.toBeNull()
        expect(box!.x, selector).toBeGreaterThanOrEqual(-1)
        expect(box!.x + box!.width, selector).toBeLessThanOrEqual(width + 1)
      }
    }).toPass({ timeout: 5000 })
    await fit('main .toolbar .el-date-editor, main .toolbar .el-select, main .toolbar button')
    if (width < 640) for (const input of await page.locator('main .toolbar .el-range-input').all()) {
      expect(await input.evaluate((element: HTMLInputElement) => {
        const context = document.createElement('canvas').getContext('2d')!
        context.font = getComputedStyle(element).font
        return context.measureText(element.value).width <= element.clientWidth
      })).toBe(true)
    }
    for (const chart of ['aa-rate-chart', 'aa-trend-chart', 'aa-priority-chart', 'aa-pareto-chart']) {
      const canvas = page.locator(`[data-test="${chart}"] canvas`).first()
      await expect(canvas).toBeVisible()
      await expect.poll(() => canvas.evaluate((element: HTMLCanvasElement) => {
        const pixels = element.getContext('2d')!.getImageData(0, 0, element.width, element.height).data
        let visible = 0
        for (let index = 3; index < pixels.length; index += 4) if (pixels[index]! > 0) visible++
        return visible
      })).toBeGreaterThan(30)
    }
    await page.screenshot({ path: test.info().outputPath(`analytics-${width}.png`), fullPage: true, animations: 'disabled' })
    await page.locator('[data-test="aa-priority-chart"]').scrollIntoViewIfNeeded()
    await page.screenshot({ path: test.info().outputPath(`analytics-charts-${width}.png`), animations: 'disabled' })
    await page.locator('[data-test="aa-thresholds"]').click()
    const dialog = page.getByRole('dialog', { name: '分析阈值', exact: true })
    await expect(dialog).toBeVisible()
    await fit('.aa-threshold-dialog, .aa-threshold-dialog .el-form-item__label, .aa-threshold-dialog .el-input-number')
    await page.screenshot({ path: test.info().outputPath(`thresholds-${width}.png`), animations: 'disabled' })
    await dialog.getByRole('button', { name: '取消', exact: true }).click()
    await page.locator('[data-test="kpi-fleeting"]').click()
    await expect(page.locator('[data-test="aa-drill"]')).toBeVisible()
    await fit('[data-test="aa-drill"]')
    await page.screenshot({ path: test.info().outputPath(`analytics-drill-${width}.png`), animations: 'disabled' })
  })
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
  await page.locator('[data-test="report-center-templates"]').click()
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
