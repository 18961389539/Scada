import { test, expect, type APIRequestContext, type Page, type Route } from '@playwright/test'
import type { ReportResult } from '../src/types'

const device = { id: 901, name: '一号泵站', protocol: 'ModbusTcp', host: '127.0.0.1', port: 502, enabled: false }
const tags = [
  { id: 911, name: '出口压力', deviceId: device.id },
  { id: 912, name: '出口压力', deviceId: null }
].map(tag => ({ ...tag, kind: 'Physical', area: 'HoldingRegister', dataType: 'Float', archiveMode: 'OnChange', unit: 'MPa', writable: false }))
interface Query { type: string; from: number; to: number; tagIds: number[] }
function params(url: string): Query {
  const q = new URL(url).searchParams
  return { type: q.get('type')!, from: Number(q.get('from')), to: Number(q.get('to')), tagIds: q.get('tagIds')!.split(',').map(Number) }
}
function result(query: Query, value = 12): ReportResult {
  return { ...query, series: query.tagIds.map(tagId => ({ tagId, points: [{ ts: query.from, avg: value, min: value - 1, max: value + 1, count: 8 }] })) }
}
function gate() {
  let release!: () => void
  return { promise: new Promise<void>(resolve => { release = resolve }), release: () => release() }
}
async function prepare(page: Page, request: APIRequestContext) {
  const response = await request.post('/api/auth/login', { data: { userName: 'admin', password: 'admin123' } })
  expect(response.ok()).toBeTruthy()
  const auth = await response.json()
  await page.addInitScript(({ token, user }) => { localStorage.setItem('scada_token', token); localStorage.setItem('scada_user', JSON.stringify(user)) }, auth)
  await page.route(url => url.pathname === '/api/devices', route => route.fulfill({ json: [device] }))
  await page.route(url => url.pathname === '/api/tags', route => route.fulfill({ json: tags }))
  const queries: Query[] = []
  await page.route(url => url.pathname === '/api/reports', route => {
    const query = params(route.request().url()); queries.push(query)
    return route.fulfill({ json: result(query) })
  })
  return queries
}
async function open(page: Page) {
  await page.goto('/reports')
  await expect(page.locator('[data-test="reports-query"]')).toBeEnabled()
}
async function generate(page: Page) {
  await page.locator('[data-test="reports-query"]').click()
  await expect(page.locator('[data-test="reports-result-context"]')).toBeVisible()
  await expect(page.locator('[data-test="reports-export"]')).toBeEnabled()
}
async function shortcut(page: Page, text: string, monthly = false) {
  await page.getByPlaceholder(monthly ? '开始月份' : '开始日期', { exact: true }).click()
  await page.getByRole('button', { name: text, exact: true }).click()
  await page.getByPlaceholder(monthly ? '开始月份' : '开始日期', { exact: true }).blur()
}
async function toggleTag(page: Page, id: number) {
  await page.locator('[data-test="reports-tags"] .el-select__wrapper').click()
  await page.getByRole('option', { name: new RegExp(`#${id} ·`) }).click()
  await page.keyboard.press('Escape')
}
async function monthly(page: Page) {
  await page.locator('.el-radio-button').filter({ hasText: '月报表' }).click()
}

test('catalog loading blocks generation and can recover in place after a failure', async ({ page, request }) => {
  const queries = await prepare(page, request), pending = gate()
  let attempts = 0
  await page.route(url => url.pathname === '/api/tags', async route => {
    attempts++
    if (attempts === 1) { await pending.promise; return route.fulfill({ status: 500, json: { message: '测点目录暂时不可用' } }) }
    return route.fulfill({ json: tags })
  })
  await page.goto('/reports')
  try {
    await expect(page.locator('[data-test="reports-selection-status"]')).toContainText('正在加载')
    await expect(page.locator('[data-test="reports-query"]')).toBeDisabled()
    await monthly(page)
    pending.release()
    await expect(page.locator('[data-test="reports-catalog-error"]')).toContainText('测点目录暂时不可用')
    await page.locator('[data-test="reports-catalog-retry"]').click()
    await expect(page.locator('[data-test="reports-catalog-error"]')).toHaveCount(0)
    await expect(page.getByRole('radio', { name: '月报表', exact: true })).toBeChecked()
    await expect(page.locator('[data-test="reports-selection-status"]')).toHaveText('已选 2 / 2 个测点')
    await generate(page)
    expect(queries).toHaveLength(1); expect(queries[0].type).toBe('monthly')
    expect(attempts).toBe(2)
  } finally { pending.release() }
})

test('no available tags has an actionable empty state and cannot generate or export', async ({ page, request }) => {
  await prepare(page, request)
  await page.route(url => url.pathname === '/api/tags', route => route.fulfill({ json: [] }))
  await page.goto('/reports')
  await expect(page.locator('[data-test="reports-empty"]')).toContainText('当前没有可用测点')
  await expect(page.locator('[data-test="reports-query"]')).toBeDisabled()
  await expect(page.locator('[data-test="reports-export"]')).toBeDisabled()
})

test('draft filters keep the displayed context stable and require regeneration before export', async ({ page, request }) => {
  const queries = await prepare(page, request)
  await open(page); await generate(page)
  const context = page.locator('[data-test="reports-result-context"]'), original = (await context.textContent())!
  await toggleTag(page, 912)
  await expect(page.locator('[data-test="reports-pending-conditions"]')).toContainText('上次生成的结果')
  await expect(context).toHaveText(original)
  await expect(page.locator('[data-test="reports-export"]')).toBeDisabled()
  await expect(page.locator('[data-test="reports-selection-status"]')).toHaveText('已选 1 / 2 个测点')
  await shortcut(page, '昨天')
  await expect(context).toHaveText(original)
  await generate(page)
  expect(queries.at(-1)!.tagIds).toEqual([911])
  await expect(page.locator('[data-test="reports-table"]')).toContainText('#911 · 一号泵站 / 出口压力')
  await expect(page.locator('[data-test="reports-pending-conditions"]')).toHaveCount(0)
  await toggleTag(page, 911)
  await expect(page.locator('[data-test="reports-selection-status"]')).toContainText('请先选择测点')
  await expect(page.locator('[data-test="reports-query"]')).toBeDisabled()
  await expect(page.locator('[data-test="reports-export"]')).toBeDisabled()
  await expect(page.locator('[data-test="reports-table"] .el-table__row')).toHaveCount(1)
})

test('daily and monthly shortcuts include complete local calendar periods', async ({ page, request }) => {
  const queries = await prepare(page, request)
  await open(page)
  await shortcut(page, '近 7 天'); await generate(page)
  const expected = await page.evaluate(() => {
    const now = new Date(), start = new Date(now.getFullYear(), now.getMonth(), now.getDate() - 6)
    return { from: start.getTime(), to: new Date(now.getFullYear(), now.getMonth(), now.getDate() + 1).getTime() - 1 }
  })
  expect(queries[0]).toMatchObject(expected)
  await monthly(page)
  await shortcut(page, '近 3 个月', true); await generate(page)
  const months = await page.evaluate(() => {
    const now = new Date()
    return { from: new Date(now.getFullYear(), now.getMonth() - 2, 1).getTime(), to: new Date(now.getFullYear(), now.getMonth() + 1, 1).getTime() - 1 }
  })
  expect(queries[1]).toMatchObject({ type: 'monthly', ...months })
  await expect(page.locator('[data-test="reports-result-context"]')).toContainText('月报表')
  const picker = page.getByPlaceholder('开始月份', { exact: true }).locator('..')
  await picker.hover()
  await picker.locator('.el-range__close-icon').click()
  await expect(page.locator('[data-test="reports-selection-status"]')).toContainText('请选择时间范围')
  await expect(page.locator('[data-test="reports-query"]')).toBeDisabled()
  await expect(page.locator('[data-test="reports-export"]')).toBeDisabled()
  await expect(page.locator('[data-test="reports-result-context"]')).toContainText('月报表')
})

test('a failed generation preserves the successful report with its original type and dates', async ({ page, request }) => {
  await prepare(page, request)
  await open(page); await generate(page)
  const context = (await page.locator('[data-test="reports-result-context"]').textContent())!
  await page.route(url => url.pathname === '/api/reports', route => route.fulfill({ status: 500, json: { message: '新报表暂时不可用' } }))
  await monthly(page)
  await page.locator('[data-test="reports-query"]').click()
  await expect(page.locator('[data-test="reports-query-error"]')).toContainText('新报表暂时不可用')
  await expect(page.locator('[data-test="reports-result-context"]')).toHaveText(context)
  await expect(page.locator('[data-test="reports-result-context"]')).toContainText('日报表')
  await expect(page.locator('[data-test="reports-table"] .el-table__row')).toHaveCount(2)
  await expect(page.locator('[data-test="reports-export"]')).toBeDisabled()
  await page.screenshot({ path: test.info().outputPath('previous-result-after-failure.png'), animations: 'disabled' })
})

for (const outcome of ['success', 'failure'] as const) {
  test(`late ${outcome} from a previous filter cannot replace a newer report`, async ({ page, request }) => {
    await prepare(page, request)
    const pending = gate()
    let requests = 0
    await page.route(url => url.pathname === '/api/reports', async route => {
      const query = params(route.request().url())
      if (++requests === 1) {
        await pending.promise
        return outcome === 'failure' ? route.fulfill({ status: 500, json: { message: '过期请求失败' } }) : route.fulfill({ json: result(query, 111) })
      }
      return route.fulfill({ json: result(query, 222) })
    })
    await open(page)
    const first = page.waitForRequest(r => new URL(r.url()).pathname === '/api/reports')
    await page.locator('[data-test="reports-query"]').click(); await first
    try {
      await monthly(page)
      await generate(page)
      const context = (await page.locator('[data-test="reports-result-context"]').textContent())!
      const done = page.waitForResponse(r => new URL(r.url()).pathname === '/api/reports' && new URL(r.url()).searchParams.get('type') === 'daily')
      pending.release(); await (await done).finished()
      await page.evaluate(() => new Promise(requestAnimationFrame))
      await expect(page.locator('[data-test="reports-result-context"]')).toHaveText(context)
      await expect(page.locator('[data-test="reports-table"]')).toContainText('222.000')
      await expect(page.locator('[data-test="reports-table"]')).not.toContainText('111.000')
      await expect(page.locator('[data-test="reports-query-error"]')).toHaveCount(0)
      await expect(page.locator('[data-test="reports-export"]')).toBeEnabled()
      expect(requests).toBe(2)
    } finally { pending.release() }
  })
}

test('pending generation rejects repeated actions and keeps export disabled until completion', async ({ page, request }) => {
  await prepare(page, request)
  await open(page); await generate(page)
  const pending = gate(); let calls = 0
  await page.route(url => url.pathname === '/api/reports', async route => {
    calls++; await pending.promise; return route.fulfill({ json: result(params(route.request().url())) })
  })
  const started = page.waitForRequest(r => new URL(r.url()).pathname === '/api/reports')
  await page.locator('[data-test="reports-query"]').click(); await started
  try {
    await expect(page.locator('[data-test="reports-export"]')).toBeDisabled()
    await page.locator('[data-test="reports-query"]').dispatchEvent('click')
    await page.locator('[data-test="reports-query"]').dispatchEvent('click')
    expect(calls).toBe(1)
    pending.release()
    await expect(page.locator('[data-test="reports-export"]')).toBeEnabled()
  } finally { pending.release() }
})

test('export captures the displayed query, prevents duplicates, and retries with a descriptive filename', async ({ page, request }) => {
  const queries = await prepare(page, request), exports: Query[] = [], pending = gate()
  await page.route(url => url.pathname === '/api/reports/export', async route => {
    exports.push(params(route.request().url()))
    if (exports.length === 1) { await pending.promise; return route.fulfill({ status: 500, json: { message: '导出暂时不可用' } }) }
    return route.fulfill({ contentType: 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet', body: Buffer.from('export fixture') })
  })
  await open(page); await shortcut(page, '昨天'); await generate(page)
  const button = page.locator('[data-test="reports-export"]')
  const started = page.waitForRequest(r => new URL(r.url()).pathname === '/api/reports/export')
  await button.click(); await started
  try {
    await expect(button).toBeDisabled()
    await expect(page.locator('[data-test="reports-query"]')).toBeDisabled()
    await expect(page.getByRole('radio', { name: '月报表', exact: true })).toBeDisabled()
    await button.dispatchEvent('click'); await button.dispatchEvent('click')
    expect(exports).toEqual(queries)
    pending.release()
    await expect(page.locator('[data-test="reports-export-error"]')).toContainText('导出暂时不可用')
    await expect(button).toBeEnabled()
    const download = page.waitForEvent('download')
    await button.click()
    const file = await download
    const date = await page.evaluate(ts => { const d = new Date(ts); return `${d.getFullYear()}${String(d.getMonth() + 1).padStart(2, '0')}${String(d.getDate()).padStart(2, '0')}` }, queries[0].from)
    expect(file.suggestedFilename()).toBe(`report_daily_${date}_${date}.xlsx`)
    expect(exports).toEqual([queries[0], queries[0]])
    await expect(page.locator('[data-test="reports-export-error"]')).toHaveCount(0)
    await expect(page.locator('[data-test="reports-table"] .el-table__row')).toHaveCount(2)
  } finally { pending.release() }
})

test('time sorting uses timestamps instead of a missing field', async ({ page, request }) => {
  await prepare(page, request)
  await page.route(url => url.pathname === '/api/reports', route => {
    const query = params(route.request().url()), data = result({ ...query, tagIds: [911] })
    data.series[0].points.push({ ts: query.from + 3600_000, avg: 21, min: 20, max: 22, count: 16 })
    return route.fulfill({ json: data })
  })
  await open(page); await generate(page)
  const rows = page.locator('[data-test="reports-table"] .el-table__body-wrapper .el-table__row')
  await expect(rows.first().locator('td').last()).toHaveText('8')
  const sort = page.getByRole('button', { name: '按 时间 排序', exact: true })
  await sort.locator('.descending').click()
  await expect(rows.first().locator('td').last()).toHaveText('16')
})

test('leaving during export does not start a download in the next page', async ({ page, request }) => {
  await prepare(page, request)
  const pending = gate(), downloads: string[] = []
  page.on('download', file => downloads.push(file.suggestedFilename()))
  await page.route(url => url.pathname === '/api/reports/export', async (route: Route) => {
    await pending.promise
    return route.fulfill({ contentType: 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet', body: Buffer.from('export fixture') })
  })
  await open(page); await generate(page)
  const started = page.waitForRequest(r => new URL(r.url()).pathname === '/api/reports/export')
  await page.locator('[data-test="reports-export"]').click(); await started
  try {
    await page.getByRole('menuitem', { name: '总览', exact: true }).click()
    await expect(page).toHaveURL('/overview')
    const done = page.waitForResponse(r => new URL(r.url()).pathname === '/api/reports/export')
    pending.release(); await (await done).finished()
    await page.evaluate(() => new Promise(requestAnimationFrame))
    expect(downloads).toEqual([])
  } finally { pending.release() }
})
