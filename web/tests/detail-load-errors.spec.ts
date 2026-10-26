import { test, expect, type APIRequestContext, type Page } from '@playwright/test'

async function login(request: APIRequestContext) {
  const response = await request.post('/api/auth/login', { data: { userName: 'admin', password: 'admin123' } })
  expect(response.ok()).toBeTruthy()
  const body = await response.json()
  return { token: body.token as string, user: body.user, headers: { Authorization: `Bearer ${body.token}` } }
}

async function sign(page: Page, session: { token: string; user: unknown }): Promise<void> {
  await page.addInitScript((data) => {
    localStorage.setItem('scada_token', data.token)
    localStorage.setItem('scada_user', JSON.stringify(data.user))
  }, session)
}

const thresholds = {
  operatorPositions: 1, acceptablePer10Min: 1, manageablePer10Min: 2, floodStartPer10Min: 10, floodEndPer10Min: 5,
  chatterCount: 3, chatterWindowSec: 60, fleetingSec: 10, standingHours: 24, staleHours: 24,
  consequentialWindowSec: 5, consequentialMinCount: 2, consequentialMinRatio: 0.8, frequentPerDay: 10,
  targetLowPercent: 80, targetMediumPercent: 15, targetHighPercent: 5, topN: 10
}

test('alarm drill failure stays in the drawer and clears the previous count', async ({ page, request }) => {
  const session = await login(request)
  await sign(page, session)
  const report = {
    kpi: {
      from: 0, to: 1, asOf: 1, hours: 1,
      total: 3, annunciated: 3, shelved: 0, suppressed: 0, chattering: 0, operatorPositions: 1,
      perOperator10Min: 0, perOperatorHour: 0, perOperatorDay: 0, peak10Min: 1, peak10MinTs: null, rating: 'acceptable',
      pctBucketsAcceptable: 100, pctBucketsManageable: 0, pctBucketsDemanding: 0, pctBucketsFlood: 0,
      percentTimeInFlood: 0, floods: [],
      ackCount: 0, avgAckSec: null, medianAckSec: null, clearCount: 0, avgClearSec: null, medianClearSec: null,
      standing: [], stale: [], standingDuringWindow: 0,
      shelveCount: 0, shelvedHours: 0, shelvedByRule: [], suppressedHours: 0,
      priority: [
        { level: 'Info', band: 'low', count: 0, percent: 0, targetPercent: 80 },
        { level: 'Warning', band: 'medium', count: 3, percent: 100, targetPercent: 15 },
        { level: 'Critical', band: 'high', count: 0, percent: 0, targetPercent: 5 }
      ],
      top: [{ source: 'r1', ruleId: 1, tagId: 1, deviceId: null, message: '泵超限', level: 'Warning', count: 3, annunciated: 3, chatterEpisodes: 0, fleeting: 1, percent: 100, cumulativePercent: 100 }],
      chatteringSources: [], fleeting: [], fleetingTotal: 1, consequential: []
    },
    detailFrom: 0, usedRollups: false, truncated: false, rate10: [], suggestions: [], thresholds
  }
  let eventCalls = 0
  await page.route((url) => url.pathname === '/api/alarm-analytics', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(report) }))
  await page.route((url) => url.pathname === '/api/alarm-analytics/trend', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ granularity: 'hour', points: [] }) }))
  await page.route((url) => url.pathname === '/api/alarm-analytics/events', (route) => {
    eventCalls += 1
    if (eventCalls === 1) {
      return route.fulfill({
        status: 200, contentType: 'application/json',
        body: JSON.stringify({
          total: 4,
          items: [{ id: 9, ruleId: 1, tagId: 1, level: 'Warning', startTs: Date.now(), endTs: null, ackTs: null, ackUser: null, shelved: false, suppressed: false, chattering: false, message: '泵超限', source: 'r1' }]
        })
      })
    }
    return route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message: '明细不可用' }) })
  })
  await page.goto('/alarm-analytics')
  await page.locator('[data-test="kpi-fleeting"]').click()
  const drawer = page.locator('.el-drawer')
  await expect(drawer.locator('[data-test="aa-drill-total"]')).toContainText('共 4 条')
  await expect(drawer.locator('[data-test="aa-drill-table"]')).toContainText('泵超限')
  await page.keyboard.press('Escape')
  await expect(drawer).not.toBeVisible()
  await page.locator('[data-test="kpi-top"]').getByRole('row', { name: /泵超限/ }).click()
  await expect(drawer.locator('[data-test="aa-drill-error"]')).toContainText('明细不可用')
  await expect(drawer.locator('[data-test="aa-drill-total"]')).toContainText('共 0 条')
  await expect(drawer.getByText('暂无数据')).toHaveCount(0)
  await expect(drawer.locator('[data-test="aa-drill-table"]').getByText('泵超限')).toHaveCount(0)
})

test('OPC certificate load failure stays in the dialog and a refresh keeps the last list', async ({ page, request }) => {
  const session = await login(request)
  await sign(page, session)
  let fail = true
  const cert = {
    sha256: 'abc', thumbprint: 'abc', subject: 'CN=StubPump', issuer: 'CN=Stub',
    notBefore: '2020-01-01T00:00:00Z', notAfter: '2030-01-01T00:00:00Z', store: 'rejected'
  }
  await page.route((url) => url.pathname === '/api/opc/certificates', (route) => {
    if (route.request().method() !== 'GET') return route.continue()
    if (fail) return route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message: '证书不可用' }) })
    return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify([cert]) })
  })
  await page.goto('/config/devices')
  await page.locator('[data-test="device-certificates"]').click()
  const dialog = page.getByRole('dialog', { name: 'OPC UA 证书' })
  await expect(dialog.locator('[data-test="opc-cert-error"]')).toContainText('证书不可用')
  await expect(dialog.getByText('此证书库暂无证书')).toHaveCount(0)
  fail = false
  await dialog.getByRole('button', { name: '刷新证书', exact: true }).click()
  await expect(dialog.getByText('CN=StubPump')).toBeVisible()
  await expect(dialog.locator('[data-test="opc-cert-error"]')).toHaveCount(0)
  fail = true
  await dialog.getByRole('button', { name: '刷新证书', exact: true }).click()
  await expect(dialog.locator('[data-test="opc-cert-error"]')).toContainText('证书不可用')
  await expect(dialog.getByText('CN=StubPump')).toBeVisible()
})
