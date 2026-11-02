import { test, expect, type APIRequestContext, type Page } from './fixtures'

// Round 24: browser Back re-reads the address on pages that stay mounted.

async function login(request: APIRequestContext) {
  const response = await request.post('/api/auth/login', { data: { userName: 'admin', password: 'admin123' } })
  expect(response.ok()).toBeTruthy()
  const body = await response.json()
  return { token: body.token as string, user: body.user }
}

async function sign(page: Page, session: { token: string; user: unknown }): Promise<void> {
  await page.addInitScript((data) => {
    localStorage.setItem('scada_token', data.token)
    localStorage.setItem('scada_user', JSON.stringify(data.user))
  }, session)
}

async function pushQuery(page: Page, path: string, query: Record<string, string>): Promise<void> {
  await page.evaluate(({ path, query }) => {
    const root = document.querySelector('#app') as { __vue_app__?: { config: { globalProperties: { $router: { push: (to: unknown) => Promise<void> } } } } } | null
    const router = root?.__vue_app__?.config.globalProperties.$router
    if (!router) throw new Error('router missing')
    return router.push({ path, query })
  }, { path, query })
}

test('alarm analytics reloads the previous query on browser back', async ({ page, request }) => {
  const session = await login(request)
  await sign(page, session)
  await page.route(url => url.pathname === '/api/areas', route => route.fulfill({ json: [] }))
  await page.route(url => url.pathname === '/api/alarm-rules', route => route.fulfill({ json: [] }))
  await page.route(url => url.pathname === '/api/alarm-analytics' || url.pathname === '/api/alarm-analytics/trend', route => {
    const current = new URL(route.request().url())
    const body = current.pathname.endsWith('/trend')
      ? { granularity: 'hour', points: [] }
      : { kpi: null, rate10: [], suggestions: [], thresholds: {} }
    return route.fulfill({ json: body })
  })
  await page.goto('/alarm-analytics?from=1700000000000&to=1700003600000')
  const context = page.locator('[data-test="aa-result-context"]')
  await expect(context).toBeVisible()
  const first = (await context.innerText()).trim()
  await pushQuery(page, '/alarm-analytics', { from: '1700086400000', to: '1700090000000' })
  await expect(context).not.toHaveText(first)
  const second = (await context.innerText()).trim()
  expect(second).not.toBe(first)
  await page.evaluate(() => history.back())
  await expect(context).toHaveText(first)
})

test('traceability reloads the previous SN on browser back', async ({ page, request }) => {
  const session = await login(request)
  await sign(page, session)
  await page.route(url => url.pathname === '/api/traceability/jobs', route => route.fulfill({ json: [] }))
  await page.route(url => url.pathname === '/api/devices', route => route.fulfill({ json: [] }))
  await page.route(url => url.pathname === '/api/tags', route => route.fulfill({ json: [] }))
  await page.route(url => url.pathname === '/api/traceability/records', route => route.fulfill({ json: { items: [], total: 0 } }))
  await page.goto('/traceability?sn=SN-AAA')
  const table = page.locator('[data-test="trace-records"]')
  await expect(table).toContainText('SN-AAA')
  await pushQuery(page, '/traceability', { sn: 'SN-BBB' })
  await expect(table).toContainText('SN-BBB')
  await expect(table).not.toContainText('SN-AAA')
  await page.evaluate(() => history.back())
  await expect(table).toContainText('SN-AAA')
  await expect(table).not.toContainText('SN-BBB')
})
