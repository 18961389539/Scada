import { test, expect, type APIRequestContext } from '@playwright/test'

async function login(request: APIRequestContext, userName: string, password: string) {
  const response = await request.post('/api/auth/login', { data: { userName, password } }); expect(response.ok(), await response.text()).toBeTruthy()
  const result = await response.json(); return { ...result, headers: { Authorization: `Bearer ${result.token}` } }
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

  await page.getByRole('tab', { name: /配置/ }).click()
  await expect(page.locator('[data-test="oee-lines"]').getByText(`E2E-L-${suffix}`)).toBeVisible()
  await page.getByRole('tab', { name: /产品/ }).click()
  await expect(page.locator('[data-test="oee-products"]').getByText(`E2E-P-${suffix}`)).toBeVisible()
  await page.getByRole('tab', { name: /班次/ }).click()
  await expect(page.locator('[data-test="oee-shifts"]').getByText(`E2E-S-${suffix}`)).toBeVisible()
  await page.getByRole('tab', { name: /停机原因/ }).click()
  await expect(page.locator('[data-test="oee-reasons"]').getByText(`故障-${suffix}`)).toBeVisible()

  const xlsx = await request.get(`/api/oee/export?format=xlsx&from=${from}&to=${now}&lineId=${line.id}`, { headers: admin })
  expect(xlsx.ok(), await xlsx.text()).toBeTruthy()

  await page.goto('/report-center')
  await page.getByRole('button', { name: /新建|新增|模板/ }).first().click()
  await page.locator('[data-test="report-oee-kpi-preset"]').click()
  await expect(page.locator('[data-test="report-oee-metric-0"]')).toBeVisible()
  await expect(page.locator('[data-test^="report-column-"]')).toHaveCount(6)
})
