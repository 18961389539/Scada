import { test, expect, type Page, type APIRequestContext } from './fixtures'

const jobs = [{ id: 601, name: '检测任务甲', enabled: false, canScan: false, canConfigure: false,
  snSource: 'WebScan', status: { state: 'Idle' }, fields: [] }]
const record = (sn: string, id = 1) => ({ id, sn, jobId: 601, jobName: '检测任务甲',
  capturedAt: '2026-10-01T08:00:00Z', acknowledged: true, outcome: 'Passed', values: [] })

async function prepare(page: Page, request: APIRequestContext) {
  const login = await request.post('/api/auth/login', { data: { userName: 'admin', password: 'admin123' } })
  expect(login.ok()).toBeTruthy()
  await page.addInitScript(({ token, user }) => {
    localStorage.setItem('scada_token', token); localStorage.setItem('scada_user', JSON.stringify(user))
  }, await login.json())
  await page.route(url => url.pathname === '/api/traceability/jobs', route => route.fulfill({ json: jobs }))
  await page.route(url => url.pathname === '/api/devices' || url.pathname === '/api/tags', route => route.fulfill({ json: [] }))
  const queries: URLSearchParams[] = []
  await page.route(url => url.pathname === '/api/traceability/records', route => {
    const params = new URL(route.request().url()).searchParams
    queries.push(params)
    const currentPage = Number(params.get('page'))
    return route.fulfill({ json: { total: 101, items: [record(`${params.get('sn') ?? 'ALL'}-PAGE-${currentPage}`, currentPage)] } })
  })
  await page.goto('/traceability')
  await expect(page.locator('[data-test="trace-records"]')).toContainText('ALL-PAGE-1')
  return queries
}

test('traceability labels the actual result filters and requires a query after changing SN, job, outcome or dates', async ({ page, request }) => {
  const queries = await prepare(page, request)
  const context = page.locator('[data-test="trace-result-context"]')
  const pending = page.locator('[data-test="trace-pending-conditions"]')
  await expect(context).toContainText('全部 SN')
  await expect(context).toContainText('不限时间')
  await expect(context).toContainText('共 101 条')
  await expect(pending).toBeHidden()
  await page.locator('[data-test="trace-sn-query"]').fill('SN-A')
  await expect(pending).toBeVisible()
  await expect(page.locator('[data-test="trace-records"]')).toContainText('ALL-PAGE-1')
  await expect(context).toContainText('全部 SN')
  await expect(page.locator('[data-test="trace-pagination"] .btn-next')).toBeDisabled()
  await page.locator('[data-test="trace-job-filter"]').click()
  await page.getByRole('option', { name: '检测任务甲', exact: true }).click()
  await page.locator('[data-test="trace-outcome-filter"]').click()
  await page.getByRole('option', { name: '不合格', exact: true }).click()
  const dates = page.getByRole('combobox', { name: '时间范围', exact: true })
  await dates.nth(0).fill('2026-10-01 00:00:00')
  await dates.nth(1).fill('2026-10-02 00:00:00')
  await dates.nth(1).press('Tab')
  await page.locator('[data-test="trace-query"]').click()
  await expect(pending).toBeHidden()
  await expect(context).toContainText('SN-A')
  await expect(context).toContainText('检测任务甲')
  await expect(context).toContainText('不合格')
  await expect(context).toContainText('2026-10-01')
  expect(queries.at(-1)?.get('jobId')).toBe('601')
  expect(queries.at(-1)?.get('outcome')).toBe('Failed')
  expect(Number(queries.at(-1)?.get('to'))).toBeGreaterThan(Number(queries.at(-1)?.get('from')))
  await page.locator('[data-test="trace-pagination"] .btn-next').click()
  await expect(context).toContainText('第 2 页')
  expect(queries.at(-1)?.get('sn')).toBe('SN-A')
  expect(queries.at(-1)?.get('page')).toBe('2')
})

test('a failed query keeps the successful rows, result context and page until retry succeeds', async ({ page, request }) => {
  const queries = await prepare(page, request)
  await page.locator('[data-test="trace-pagination"] .btn-next').click()
  const context = page.locator('[data-test="trace-result-context"]')
  await expect(context).toContainText('第 2 页')
  let fails = true
  await page.route(url => url.pathname === '/api/traceability/records' && url.searchParams.get('sn') === 'SN-B', route => fails
    ? route.fulfill({ status: 500, json: { message: '追溯查询暂不可用' } })
    : route.fulfill({ json: { total: 1, items: [record('SN-B')] } }))
  await page.locator('[data-test="trace-sn-query"]').fill('SN-B')
  await page.locator('[data-test="trace-query"]').click()
  await expect(page.locator('[data-test="trace-query-error"]')).toContainText('追溯查询暂不可用')
  await expect(page.locator('[data-test="trace-query-error"]')).toContainText('保留上次成功查询')
  await expect(page.locator('[data-test="trace-records"]')).toContainText('ALL-PAGE-2')
  await expect(context).toContainText('第 2 页')
  await expect(context).toContainText('全部 SN')
  await expect(page.locator('[data-test="trace-pagination"] .btn-next')).toBeDisabled()
  await page.locator('[data-test="trace-sn-query"]').fill('')
  await expect(page.locator('[data-test="trace-pending-conditions"]')).toBeHidden()
  await page.locator('[data-test="trace-pagination"] .btn-next').click()
  await expect(context).toContainText('第 3 页')
  expect(queries.at(-1)?.has('sn')).toBe(false)
  expect(queries.at(-1)?.get('page')).toBe('3')
  await page.locator('[data-test="trace-sn-query"]').fill('SN-B')
  fails = false
  await page.locator('[data-test="trace-query"]').click()
  await expect(context).toContainText('SN-B')
  await expect(context).toContainText('第 1 页')
  await expect(page.locator('[data-test="trace-query-error"]')).toBeHidden()
})

for (const outcome of ['success', 'failure'] as const) {
  test(`a late traceability ${outcome} cannot overwrite a query for newer filters`, async ({ page, request }) => {
    await prepare(page, request)
    let release!: () => void
    const pending = new Promise<void>(resolve => { release = resolve })
    await page.route(url => url.pathname === '/api/traceability/records' && url.searchParams.get('sn') === 'OLD', async route => {
      await pending
      return route.fulfill(outcome === 'failure' ? { status: 500, json: { message: '过期查询失败' } }
        : { json: { total: 1, items: [record('OLD-LATE-RESULT')] } })
    })
    await page.locator('[data-test="trace-sn-query"]').fill('OLD')
    const started = page.waitForRequest(request => new URL(request.url()).searchParams.get('sn') === 'OLD')
    await page.locator('[data-test="trace-query"]').click(); await started
    try {
      await page.locator('[data-test="trace-sn-query"]').fill('NEW')
      await page.locator('[data-test="trace-query"]').click()
      await expect(page.locator('[data-test="trace-records"]')).toContainText('NEW-PAGE-1')
      const done = page.waitForResponse(response => new URL(response.url()).searchParams.get('sn') === 'OLD')
      release(); await (await done).finished()
      await page.evaluate(() => new Promise(requestAnimationFrame))
      await expect(page.locator('[data-test="trace-records"]')).toContainText('NEW-PAGE-1')
      await expect(page.locator('[data-test="trace-result-context"]')).toContainText('NEW')
      await expect(page.locator('[data-test="trace-query-error"]')).toBeHidden()
    } finally { release() }
  })
}
