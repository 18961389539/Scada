import { test, expect, type APIRequestContext, type Page } from './fixtures'
import { openNavigationGroup } from './nav-catalog'

async function login(request: APIRequestContext, userName: string, password: string) {
  const response = await request.post('/api/auth/login', { data: { userName, password } }); expect(response.ok(), await response.text()).toBeTruthy()
  const result = await response.json(); return { ...result, headers: { Authorization: `Bearer ${result.token}` } }
}

function reviewReport(from: number, to: number, good = 90) {
  const point = { from, to, label: '测试日期', oee: 0.8, availability: 0.9, performance: 0.95, quality: 0.94, good, total: 100 }
  return { kpi: { ...point, plannedMs: 3600000, runMs: 3000000, downMs: 600000, faultMs: 600000, reject: 10, scrap: 0, scrapRate: 0.1, mtbfHours: 1, mttrHours: 0.1, faultCount: 1, productionVsTargetPercent: 90, target: 100, performanceOver100: false, performanceClamped: false,
    downtimePareto: [{ code: 'FAULT', name: '故障停机', category: 'Availability', isPlanned: false, hours: 0.1, count: 1, percent: 100, cumulativePercent: 100 }], byShift: [], byProduct: [], byDay: [point] }, trend: [point], detailFrom: from, usedRollups: false,
    thresholds: { targetOee: 0.85, targetAvailability: 0.9, targetPerformance: 0.95, targetQuality: 0.99, clampPerformance: true, topN: 10 } }
}
function gate() {
  let release!: () => void
  return { promise: new Promise<void>(resolve => { release = resolve }), release: () => release() }
}
async function prepareReview(page: Page, request: APIRequestContext) {
  const auth = await login(request, 'admin', 'admin123')
  await page.addInitScript(({ token, user }) => { localStorage.setItem('scada_token', token); localStorage.setItem('scada_user', JSON.stringify(user)) }, auth)
  const catalogs: Record<string, unknown> = {
    '/api/areas': [{ id: 1, name: '一车间' }, { id: 2, name: '二车间' }],
    '/api/oee/lines': [{ id: 1, name: '一号产线', areaId: 1 }, { id: 2, name: '二号产线', areaId: 2 }],
    '/api/oee/products': [], '/api/oee/shifts': []
  }
  await page.route(url => url.pathname in catalogs, route => route.fulfill({ json: catalogs[new URL(route.request().url()).pathname] }))
  await page.route(url => url.pathname === '/api/oee/analytics', route => {
    const params = new URL(route.request().url()).searchParams
    return route.fulfill({ json: reviewReport(Number(params.get('from')), Number(params.get('to'))) })
  })
  await page.goto('/oee')
  await expect(page.locator('[data-test="kpi-good"] .value')).toHaveText('90')
  await expect(page.locator('[data-test="oee-export-xlsx"]')).toBeEnabled()
  return catalogs
}

for (const outcome of ['success', 'failure'] as const) {
  test(`other pages review ignores a late OEE query ${outcome} after new conditions win`, async ({ page, request }) => {
    await prepareReview(page, request)
    const pending = gate()
    await page.route(url => url.pathname === '/api/oee/analytics', async route => {
      const params = new URL(route.request().url()).searchParams, from = Number(params.get('from')), to = Number(params.get('to'))
      if (to - from < 2 * 86400000) {
        await pending.promise
        return route.fulfill(outcome === 'failure' ? { status: 500, json: { message: '旧统计不可用' } } : { json: reviewReport(from, to, 11) })
      }
      return route.fulfill({ json: reviewReport(from, to, 22) })
    })
    const started = page.waitForRequest(request => new URL(request.url()).pathname === '/api/oee/analytics')
    await page.locator('[data-test="oee-query"]').click(); await started
    try {
      await page.getByRole('button', { name: '7 天', exact: true }).focus(); await page.keyboard.press('Enter')
      await page.locator('[data-test="oee-query"]').click()
      await expect(page.locator('[data-test="kpi-good"] .value')).toHaveText('22')
      const context = (await page.locator('[data-test="oee-result-context"]').textContent())!
      const done = page.waitForResponse(response => new URL(response.url()).pathname === '/api/oee/analytics' && Number(new URL(response.url()).searchParams.get('to')) - Number(new URL(response.url()).searchParams.get('from')) < 2 * 86400000)
      pending.release(); await (await done).finished(); await page.evaluate(() => new Promise(requestAnimationFrame))
      await expect(page.locator('[data-test="kpi-good"] .value')).toHaveText('22')
      await expect(page.locator('[data-test="oee-result-context"]')).toHaveText(context)
      await expect(page.locator('[data-test="oee-query-error"]')).toHaveCount(0)
    } finally { pending.release() }
  })
}

test('other pages review exports only the successful matching OEE result and locks duplicate downloads', async ({ page, request }) => {
  await prepareReview(page, request)
  await page.locator('[data-test="oee-line"]').click(); await page.getByRole('option', { name: '一号产线', exact: true }).click()
  await expect(page.locator('[data-test="oee-pending-conditions"]')).toBeVisible()
  await expect(page.locator('[data-test="oee-export-xlsx"]')).toBeDisabled()
  await page.locator('[data-test="oee-query"]').click()
  await expect(page.locator('[data-test="oee-result-context"]')).toContainText('一号产线')
  const pending = gate()
  let exports = 0
  await page.route(url => url.pathname === '/api/oee/export', async route => {
    exports++
    expect(new URL(route.request().url()).searchParams.get('lineId')).toBe('1')
    await pending.promise
    return route.fulfill({ body: 'OEE fixture', contentType: 'application/octet-stream' })
  })
  await page.locator('[data-test="oee-export-xlsx"]').click()
  try {
    await expect.poll(() => exports).toBe(1)
    await page.locator('[data-test="oee-export-pdf"]').dispatchEvent('click')
    expect(exports).toBe(1)
    const download = page.waitForEvent('download'); pending.release()
    expect((await download).suggestedFilename()).toBe('oee.xlsx')
    await expect(page.locator('[data-test="oee-export-xlsx"]')).toBeEnabled()
  } finally { pending.release() }
})

test('other pages review clears an OEE line outside the selected area and retries failed catalogs', async ({ page, request }) => {
  await prepareReview(page, request)
  await page.locator('[data-test="oee-line"]').click(); await page.getByRole('option', { name: '一号产线', exact: true }).click()
  await page.locator('[data-test="oee-area"]').click(); await page.getByRole('option', { name: '二车间', exact: true }).click()
  const next = page.waitForRequest(request => new URL(request.url()).pathname === '/api/oee/analytics')
  await page.locator('[data-test="oee-query"]').click()
  const params = new URL((await next).url()).searchParams
  expect(params.get('areaId')).toBe('2'); expect(params.has('lineId')).toBe(false)
  await expect(page.locator('[data-test="oee-result-context"]')).toContainText('全部产线')
  let fail = true
  await page.route(url => url.pathname === '/api/oee/lines', route => route.fulfill(fail ? { status: 500, json: { message: '产线目录不可用' } } : { json: [{ id: 2, areaId: 2, name: '重试产线' }] }))
  await page.reload()
  await expect(page.locator('[data-test="oee-meta-error"]')).toContainText('产线目录不可用')
  fail = false
  await page.locator('[data-test="oee-meta-retry"]').click()
  await expect(page.locator('[data-test="oee-meta-error"]')).toHaveCount(0)
  await page.locator('[data-test="oee-line"]').click()
  await expect(page.getByRole('option', { name: '重试产线', exact: true })).toBeVisible()
})

test('other pages review locks OEE downtime confirmation and exposes failed writes for retry', async ({ page, request }) => {
  await prepareReview(page, request)
  const event = { id: 101, lineId: 1, lineName: '一号产线', startTs: Date.now() - 60000, endTs: Date.now(), state: 'Fault', source: 'manual', confirmed: false }
  await page.route(url => url.pathname === '/api/oee/reasons', route => route.fulfill({ json: [] }))
  await page.route(url => url.pathname === '/api/oee/downtimes', route => route.fulfill({ json: [event] }))
  const pending = gate()
  let writes = 0
  await page.route(url => url.pathname === '/api/oee/downtimes/101/confirm', async route => {
    writes++
    if (writes === 1) { await pending.promise; return route.fulfill({ status: 500, json: { message: '停机确认暂不可用' } }) }
    event.confirmed = true
    return route.fulfill({ json: {} })
  })
  await page.goto('/production/events')
  await expect(page.getByRole('tab', { name: '产线', exact: true })).toHaveCount(0)
  const button = page.locator('[data-test="oee-dt-confirm"]')
  await button.evaluate(element => { element.dispatchEvent(new MouseEvent('click', { bubbles: true })); element.dispatchEvent(new MouseEvent('click', { bubbles: true })) })
  try {
    await expect.poll(() => writes).toBe(1)
    await expect(button).toHaveClass(/is-loading/)
    pending.release()
    await expect(page.locator('[data-test="oee-action-error"]')).toContainText('停机确认暂不可用')
    await button.click()
    await expect(button).toHaveCount(0)
    expect(writes).toBe(2)
  } finally { pending.release() }
})

for (const kind of ['line', 'product', 'shift', 'reason', 'dt', 'corr', 'th'] as const) {
  test(`other pages review locks OEE ${kind} drafts before validation and preserves failed writes`, async ({ page, request }) => {
    const catalogs = await prepareReview(page, request)
    const thresholds = reviewReport(0, 1).thresholds
    await page.route(url => url.pathname === '/api/oee/reasons' || url.pathname === '/api/oee/downtimes', route => route.fulfill({ json: catalogs[new URL(route.request().url()).pathname] ?? [] }))
    await page.route(url => url.pathname === '/api/oee/thresholds', route => route.fulfill({ json: { thresholds, revision: 1, defaults: thresholds } }))
    const operational = kind === 'dt' || kind === 'corr'
    await page.goto(operational ? '/production/events' : '/config/oee')
    if (!operational) await expect(page.locator('[data-test="oee-lines"]').getByText('一号产线')).toBeVisible()
    const pending = gate(), paths = { line: 'lines', product: 'products', shift: 'shifts', reason: 'reasons', dt: 'downtimes', corr: 'corrections', th: 'thresholds' }
    let writes = 0
    await page.route(url => url.pathname === `/api/oee/${paths[kind]}`, async route => {
      if (route.request().method() === 'GET') return route.fallback()
      writes++
      if (writes === 1) { await pending.promise; return route.fulfill({ status: 500, json: { message: 'OEE 保存暂不可用' } }) }
      if (['line', 'product', 'shift', 'reason'].includes(kind)) {
        const path = `/api/oee/${paths[kind]}`
        catalogs[path] = [...(catalogs[path] as object[] ?? []), { ...route.request().postDataJSON(), id: 99 }]
      }
      return route.fulfill({ json: {} })
    })
    if (kind === 'th') await page.locator('[data-test="oee-thresholds"]').click()
    else {
      if (kind !== 'line' && !operational) await page.getByRole('tab', { name: { product: '产品', shift: '班次', reason: '停机原因', dt: '停机事件', corr: '停机事件' }[kind], exact: true }).click()
      await page.locator(`[data-test="oee-add-${kind === 'dt' ? 'downtime' : kind === 'corr' ? 'correction' : kind}"]`).click()
    }
    const dialog = page.locator('.el-dialog:visible')
    const field = kind === 'th' ? dialog.getByRole('spinbutton').first() : kind === 'dt' ? dialog.getByRole('textbox', { name: '备注', exact: true }) : page.locator(`[data-test="oee-${kind}-${kind === 'corr' ? 'reason' : 'name'}"]`)
    const draft = kind === 'th' ? '0.82' : '核对草稿'
    await field.fill(draft); await field.blur()
    if (kind === 'reason') await page.locator('[data-test="oee-reason-code"]').fill('REVIEW')
    const submit = page.locator(`[data-test="oee-${kind}-save"]`)
    await submit.evaluate(element => { element.dispatchEvent(new MouseEvent('click', { bubbles: true })); element.dispatchEvent(new MouseEvent('click', { bubbles: true })) })
    try {
      await expect.poll(() => writes).toBe(1)
      await expect(field).toBeDisabled()
      await expect(dialog.getByRole('button', { name: '取消', exact: true })).toBeDisabled()
      await page.keyboard.press('Escape'); await expect(dialog).toBeVisible()
      expect(writes).toBe(1)
      pending.release()
      await expect(dialog.locator('[data-test="oee-form-error"]')).toContainText('OEE 保存暂不可用')
      await expect(field).toBeEnabled(); await expect(field).toHaveValue(draft)
      await submit.click(); await expect(dialog).toBeHidden()
      expect(writes).toBe(2)
      if (['line', 'product', 'shift', 'reason'].includes(kind)) await expect(page.locator(`[data-test="oee-${paths[kind]}"]`).getByText(draft, { exact: true })).toBeVisible()
    } finally { pending.release() }
  })
}

test('other pages review retains OEE result context on query failure and safely rejects a cleared range', async ({ page, request }) => {
  await prepareReview(page, request)
  const context = (await page.locator('[data-test="oee-result-context"]').textContent())!
  let queries = 0
  await page.route(url => url.pathname === '/api/oee/analytics', route => { queries++; return route.fulfill({ status: 500, json: { message: '本次统计不可用' } }) })
  await page.locator('[data-test="oee-query"]').click()
  await expect(page.locator('[data-test="oee-query-error"]')).toContainText('本次统计不可用')
  await expect(page.locator('[data-test="kpi-good"] .value')).toHaveText('90')
  await expect(page.locator('[data-test="oee-result-context"]')).toHaveText(context)
  await expect(page.locator('[data-test="oee-export-xlsx"]')).toBeDisabled()
  const picker = page.locator('main .toolbar .el-date-editor')
  await picker.hover(); await picker.locator('.el-range__close-icon').click()
  await page.locator('[data-test="oee-query"]').click()
  await expect(page.locator('[data-test="oee-query-error"]')).toContainText('时间')
  await expect(page.locator('[data-test="oee-export-pdf"]')).toBeDisabled()
  expect(queries).toBe(1)
})

test('other pages review cancels a late OEE download after leaving the page', async ({ page, request }) => {
  await prepareReview(page, request)
  const pending = gate(), downloads: string[] = []
  page.on('download', download => downloads.push(download.suggestedFilename()))
  await page.route(url => url.pathname === '/api/oee/export', async route => { await pending.promise; return route.fulfill({ body: 'late OEE', contentType: 'application/octet-stream' }) })
  const started = page.waitForRequest(request => new URL(request.url()).pathname === '/api/oee/export')
  await page.locator('[data-test="oee-export-xlsx"]').click(); await started
  try {
    await openNavigationGroup(page, 'monitoring')
    await page.getByRole('menuitem', { name: '总览', exact: true }).click(); await expect(page).toHaveURL(/\/overview$/)
    const done = page.waitForResponse(response => new URL(response.url()).pathname === '/api/oee/export')
    pending.release(); await (await done).finished()
    await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))))
    expect(downloads).toEqual([])
  } finally { pending.release() }
})

for (const width of [1600, 390, 320]) {
  test(`other pages review OEE controls and charts fit at ${width}px`, async ({ page, request }) => {
    await page.setViewportSize({ width, height: 900 })
    await prepareReview(page, request)
    const fit = async (selector: string) => expect(async () => {
      for (const element of await page.locator(selector).all()) {
        const box = await element.boundingBox()
        expect(box, selector).not.toBeNull()
        expect(box!.x, selector).toBeGreaterThanOrEqual(-1)
        expect(box!.x + box!.width, selector).toBeLessThanOrEqual(width + 1)
      }
    }).toPass({ timeout: 5000 })
    await fit('main .toolbar .el-select, main .toolbar .el-date-editor, main .toolbar button, main .oee-actions button')
    for (const action of await page.locator('main .oee-actions button').all()) for (const control of await page.locator('main .toolbar .el-select, main .toolbar .el-date-editor, main .toolbar button').all()) {
      const actionBox = (await action.boundingBox())!, controlBox = (await control.boundingBox())!
      expect(actionBox.x >= controlBox.x + controlBox.width || controlBox.x >= actionBox.x + actionBox.width || actionBox.y >= controlBox.y + controlBox.height || controlBox.y >= actionBox.y + actionBox.height).toBe(true)
    }
    if (width < 768) for (const input of await page.locator('main .toolbar .el-range-input').all()) {
      expect(await input.evaluate((element: HTMLInputElement) => {
        const context = document.createElement('canvas').getContext('2d')!
        context.font = getComputedStyle(element).font
        return context.measureText(element.value).width <= element.clientWidth
      })).toBe(true)
    }
    for (const chart of ['oee-trend-chart', 'oee-pareto-chart']) {
      const canvas = page.locator(`[data-test="${chart}"] canvas`).first()
      await expect.poll(() => canvas.evaluate((element: HTMLCanvasElement) => {
        const pixels = element.getContext('2d')!.getImageData(0, 0, element.width, element.height).data
        let visible = 0
        for (let index = 3; index < pixels.length; index += 4) if (pixels[index]! > 0) visible++
        return visible
      })).toBeGreaterThan(30)
      await expect.poll(() => canvas.evaluate((element: HTMLCanvasElement) => {
        const context = element.getContext('2d')!
        const edges = [context.getImageData(0, 0, 1, element.height).data, context.getImageData(element.width - 1, 0, 1, element.height).data]
        return edges.every(pixels => {
          for (let index = 3; index < pixels.length; index += 4) if (pixels[index]! > 0) return false
          return true
        })
      })).toBe(true)
    }
    await page.screenshot({ path: test.info().outputPath(`oee-${width}.png`), fullPage: true, animations: 'disabled' })
    await page.locator('[data-test="oee-trend-chart"]').scrollIntoViewIfNeeded()
    await page.screenshot({ path: test.info().outputPath(`oee-charts-${width}.png`), animations: 'disabled' })
    await page.route(url => url.pathname === '/api/oee/reasons' || url.pathname === '/api/oee/downtimes', route => route.fulfill({ json: [] }))
    await page.goto('/config/oee')
    await page.locator('[data-test="oee-add-line"]').click()
    const dialog = page.getByRole('dialog', { name: '产线', exact: true })
    await expect(dialog).toBeVisible()
    await fit('.el-dialog:visible, .el-dialog:visible .el-form-item__label, .el-dialog:visible .el-input, .el-dialog:visible .el-select, .el-dialog:visible .el-input-number')
    await page.screenshot({ path: test.info().outputPath(`oee-line-${width}.png`), animations: 'disabled' })
  })
}

test('OEE dashboard and config: line/product/shift/reason, correction, KPIs, export, report preset', async ({ page, request }) => {
  test.setTimeout(120000)
  const admin = (await login(request, 'admin', 'admin123')).headers, suffix = Date.now()
  const post = async (url: string, data: unknown) => { const r = await request.post(url, { headers: admin, data }); expect(r.ok(), await r.text()).toBeTruthy(); return r.json() }
  const areas = await (await request.get('/api/areas', { headers: admin })).json()
  const areaId = areas[0].id

  const product = await post('/api/oee/products', { name: `E2E-P-${suffix}`, idealCycleSec: 60, enabled: true })
  const line = await post('/api/oee/lines', { name: `E2E-L-${suffix}`, areaId, idealCycleSec: 60, productId: product.id, enabled: true, stateMapJson: '{}' })
  await post('/api/oee/shifts', { name: `E2E-S-${suffix}`, areaId, startMinute: 0, endMinute: 1439, days: 127, enabled: true })
  const reason = await post('/api/oee/reasons', { code: `E${suffix}`.slice(0, 12), name: `故障-${suffix}`, category: 'Availability', isPlanned: false, enabled: true })
  const now = Date.now(), from = now - 3600000
  await post('/api/oee/downtimes', { lineId: line.id, startTs: from + 600000, endTs: from + 1200000, reasonId: reason.id, state: 'Fault', confirmed: true })
  await post('/api/oee/corrections', { lineId: line.id, productId: product.id, periodFrom: from, periodTo: now, goodDelta: 40, rejectDelta: 2, scrapDelta: 0, reason: 'E2E' })

  await page.goto('/login')
  await page.getByPlaceholder(/用户名/).fill('admin'); await page.getByPlaceholder(/密码/).fill('admin123'); await page.keyboard.press('Enter')
  await expect(page).toHaveURL(/overview/)
  await page.goto('/oee')
  await expect(page.locator('[data-test="oee-query"]')).toBeVisible()
  await page.locator('[data-test="oee-query"]').click()
  await expect(page.locator('[data-test="kpi-oee"] .value')).not.toHaveText('—', { timeout: 20000 })
  await expect(page.locator('[data-test="oee-trend-chart"]')).toBeVisible()
  await expect(page.locator('[data-test="oee-pareto-chart"]')).toBeVisible()
  await expect(page.locator('[data-test="kpi-good"] .value')).not.toHaveText('0')

  await page.goto('/config/oee')
  await expect(page.locator('[data-test="oee-lines"]').getByText(`E2E-L-${suffix}`)).toBeVisible()
  await page.getByRole('tab', { name: /产品/ }).click()
  await expect(page.locator('[data-test="oee-products"]').getByText(`E2E-P-${suffix}`)).toBeVisible()
  await page.getByRole('tab', { name: /班次/ }).click()
  await expect(page.locator('[data-test="oee-shifts"]').getByText(`E2E-S-${suffix}`)).toBeVisible()
  await page.getByRole('tab', { name: /停机原因/ }).click()
  const reasonRow = page.locator('[data-test="oee-reasons"]').getByRole('row').filter({ hasText: `故障-${suffix}` })
  await expect(reasonRow).toBeVisible()
  await expect(reasonRow).toContainText('可用率')
  await expect(reasonRow).not.toContainText('Availability')
  await expect(reasonRow).toContainText('否')
  await page.locator('[data-test="oee-add-reason"]').click()
  await page.locator('[data-test="oee-reason-category"]').click()
  await expect(page.getByRole('option', { name: '可用率', exact: true })).toBeVisible()
  await expect(page.getByRole('option', { name: '表现率', exact: true })).toBeVisible()
  await expect(page.getByRole('option', { name: 'Availability', exact: true })).toHaveCount(0)
  await page.keyboard.press('Escape')
  await page.locator('.el-dialog:visible').getByRole('button', { name: '取消', exact: true }).click()
  await page.goto('/production/events')
  const dtRow = page.locator('[data-test="oee-downtimes"]').getByRole('row').filter({ hasText: `E2E-L-${suffix}` })
  await expect(dtRow).toContainText('故障')
  await expect(dtRow).toContainText('手动')
  await expect(dtRow).not.toContainText('Fault')

  const xlsx = await request.get(`/api/oee/export?format=xlsx&from=${from}&to=${now}&lineId=${line.id}`, { headers: admin })
  expect(xlsx.ok(), await xlsx.text()).toBeTruthy()

  await page.goto('/report-center')
  await page.locator('[data-test="report-center-templates"]').click()
  await page.getByRole('button', { name: /新建|新增|模板/ }).first().click()
  await page.locator('[data-test="report-oee-kpi-preset"]').click()
  await expect(page.locator('[data-test="report-oee-metric-0"]')).toBeVisible()
  await expect(page.locator('.col-row')).toHaveCount(6)
})
