import { test, expect, type APIRequestContext, type Page, type WebSocketRoute } from '@playwright/test'

const device = { id: 901, name: '锅炉间', protocol: 'ModbusTcp', host: '127.0.0.1', port: 502, areaId: 1, gatewayId: null, enabled: false }
const tags = [
  { id: 911, name: 'Temp A' },
  { id: 912, name: 'Temp B' }
].map(tag => ({ deviceId: device.id, kind: 'Physical', area: 'HoldingRegister', dataType: 'Float', archiveMode: 'OnChange', unit: '℃', writable: false, description: '', ...tag }))
const baseTs = Date.parse('2026-10-26T08:00:00Z')
const values = (value: number, ts = baseTs) => tags.map(tag => ({ tagId: tag.id, value, ts, quality: 'Good' }))

interface HistoryQuery { tagIds: number[]; from: number; to: number }

async function prepare(page: Page, request: APIRequestContext) {
  const response = await request.post('/api/auth/login', { data: { userName: 'admin', password: 'admin123' } })
  expect(response.ok()).toBeTruthy()
  const auth = await response.json()
  await page.addInitScript(({ token, user }) => {
    localStorage.setItem('scada_token', token)
    localStorage.setItem('scada_user', JSON.stringify(user))
  }, auth)
  await page.route(url => url.pathname === '/api/devices', route => route.fulfill({ json: [device] }))
  await page.route(url => url.pathname === '/api/tags', route => route.fulfill({ json: tags }))
  await page.route(url => url.pathname === '/api/realtime', route => route.fulfill({ json: values(10) }))
  const queries: HistoryQuery[] = []
  const parse = (url: string): HistoryQuery => {
    const query = new URL(url).searchParams
    return { tagIds: query.get('tagIds')!.split(',').map(Number), from: Number(query.get('from')), to: Number(query.get('to')) }
  }
  await page.route(url => url.pathname === '/api/history', route => {
    const query = parse(route.request().url())
    queries.push(query)
    return route.fulfill({ json: { resolution: 'Raw', series: query.tagIds.map(tagId => ({ tagId, points: [
      { ts: query.from + (query.to - query.from) / 4, value: tagId, quality: 'Good' },
      { ts: query.to - (query.to - query.from) / 4, value: tagId + 1, quality: 'Good' }
    ] })) } })
  })
  await page.route(url => url.pathname === '/api/history/analysis', route => {
    const query = parse(route.request().url()), middle = (query.from + query.to) / 2
    return route.fulfill({ json: { from: query.from, to: query.to, resolution: 'Raw', tags: query.tagIds.map(tagId => ({
      tagId, samples: 2, goodSamples: 1, spanMs: query.to - query.from, goodMs: (query.to - query.from) / 2,
      coverage: 50, timeWeightedAvg: 12, min: 10, max: 20, integralHours: 3, qualityTruncated: false,
      quality: [{ from: query.from, to: middle, quality: 'Good' }, { from: middle, to: query.to, quality: 'Bad' }]
    })) } })
  })
  let socket: WebSocketRoute | undefined
  const subscriptionGate = { hold: false }
  const pendingSubscriptions: string[] = []
  const hubCalls: { target: string; arguments: number[][] }[] = []
  await page.routeWebSocket(/\/hubs\/realtime(?:\?|$)/, ws => {
    socket = ws
    ws.onMessage(message => {
      for (const part of String(message).split('\x1e').filter(Boolean)) {
        const frame = JSON.parse(part)
        if (frame.protocol) ws.send('{}\x1e')
        else if (frame.type === 1 && frame.invocationId) {
          hubCalls.push(frame)
          if (frame.target === 'Subscribe' && subscriptionGate.hold) pendingSubscriptions.push(frame.invocationId)
          else ws.send(JSON.stringify({ type: 3, invocationId: frame.invocationId }) + '\x1e')
        }
      }
    })
  })
  return {
    queries,
    subscriptionGate, pendingSubscriptions, hubCalls,
    releaseSubscriptions() {
      subscriptionGate.hold = false
      for (const invocationId of pendingSubscriptions.splice(0)) socket?.send(JSON.stringify({ type: 3, invocationId }) + '\x1e')
    },
    push(value: number, ts: number) {
      if (!socket) throw new Error('Realtime socket is not connected')
      socket.send(JSON.stringify({ type: 1, target: 'values', arguments: [values(value, ts)] }) + '\x1e')
    }
  }
}

async function realtimeReady(page: Page): Promise<void> {
  await page.goto('/realtime')
  await expect(page.locator('[data-test="realtime-connection"]')).toHaveText('推送已连接')
  await expect(page.getByRole('row').filter({ hasText: 'Temp A' }).locator('.value-strong')).toContainText('10.00')
}

async function prepareRuntime(page: Page, request: APIRequestContext) {
  const monitor = await prepare(page, request)
  await page.route(url => url.pathname === '/api/realtime', async route => route.fulfill({ json: values(10, await page.evaluate(() => Date.now())) }))
  await page.route(url => url.pathname === '/api/screens/900001', route => route.fulfill({ json: {
    id: 900001, name: '订阅刷新验收', revision: 1, publishedRevision: 1,
    document: { schemaVersion: 1, width: 800, height: 480, background: '#101b2d', nodes: [{
      id: 'reading', type: 'value', x: 20, y: 20, width: 240, height: 100, rotation: 0,
      text: '', color: '#ffffff', activeColor: '#34d399', unit: '', decimals: 1, min: 0, max: 100,
      activeThreshold: .5, tagId: 911, action: { type: 'none', screenId: null, tagId: null, value: 1 }
    }] }
  } }))
  return monitor
}

test('monitoring lifecycle releases a subscription that completes after leaving a screen', async ({ page, request }) => {
  const monitor = await prepareRuntime(page, request)
  monitor.subscriptionGate.hold = true
  await page.route(url => url.pathname === '/api/screens', route => route.fulfill({ json: [] }))
  await page.goto('/screens/900001')
  await expect.poll(() => monitor.pendingSubscriptions.length).toBeGreaterThan(0)
  await page.locator('[data-test="screen-back"]').click()
  await expect(page.locator('[data-testid="screen-runtime"]')).toHaveCount(0)
  monitor.releaseSubscriptions()
  await expect.poll(() => monitor.hubCalls.some(call => call.target === 'Unsubscribe' && call.arguments[0]?.includes(911))).toBe(true)
})

test('monitoring lifecycle refreshes without waiting for an obsolete snapshot', async ({ page, request }) => {
  await page.clock.install()
  await prepareRuntime(page, request)
  await page.goto('/screens/900001')
  const reading = page.locator('[data-node-id="reading"]')
  await expect(reading).toContainText('10.0')
  let releaseOld = () => {}
  const pending = new Promise<void>(resolve => { releaseOld = resolve })
  let calls = 0
  await page.route(url => url.pathname === '/api/realtime', async route => {
    const old = ++calls === 1
    if (old) await pending
    await route.fulfill({ json: values(old ? 99 : 30, await page.evaluate(() => Date.now())) })
  })
  await page.clock.runFor(6000)
  await expect.poll(() => calls).toBe(1)
  try {
    await page.getByRole('button', { name: '刷新', exact: true }).click()
    await expect(reading).toContainText('30.0')
    const oldResponse = page.waitForResponse(response => new URL(response.url()).pathname === '/api/realtime')
    releaseOld()
    await (await oldResponse).finished()
    await page.clock.runFor(100)
    await expect(reading).toContainText('30.0')
  } finally { releaseOld() }
})

test('monitoring lifecycle clears obsolete errors and hides versions after a failed screen load', async ({ page, request }) => {
  await prepareRuntime(page, request)
  await page.route(url => url.pathname === '/api/realtime', route => route.fulfill({ status: 500, json: { message: '旧画面采样失败' } }))
  await page.goto('/screens/900001')
  await expect(page.locator('[data-test="runtime-value-error"]')).toContainText('旧画面采样失败')
  await page.route(url => url.pathname === '/api/screens/900001', route => route.fulfill({ json: {
    id: 900001, name: '无绑定画面', publishedRevision: 2,
    document: { schemaVersion: 1, width: 800, height: 480, background: '#101b2d', nodes: [] }
  } }))
  await page.getByRole('button', { name: '刷新', exact: true }).click()
  await expect(page.locator('[data-test="runtime-version"]')).toContainText('2')
  await expect(page.locator('[data-test="runtime-value-error"]')).toHaveCount(0)
  await page.route(url => url.pathname === '/api/screens/900001', route => route.fulfill({ status: 404, json: { message: '画面已删除' } }))
  await page.getByRole('button', { name: '刷新', exact: true }).click()
  await expect(page.locator('[data-test="runtime-screen-error"]')).toContainText('画面已删除')
  await expect(page.locator('[data-test="runtime-version"]')).toHaveCount(0)
})

test('monitoring review keeps live screen pushes after replacing an overlapping subscription', async ({ page, request }) => {
  const monitor = await prepareRuntime(page, request)
  await page.goto('/screens/900001')
  const reading = page.locator('[data-node-id="reading"]')
  await expect(reading).toContainText('10.0')
  await expect(page.locator('.runtime-toolbar').getByText('实时连接', { exact: true })).toBeVisible()
  monitor.push(20, await page.evaluate(() => Date.now()) + 100)
  await expect(reading).toContainText('20.0')
  for (const value of [30, 40]) {
    await page.getByRole('button', { name: '刷新', exact: true }).click()
    await expect(reading).toContainText('10.0')
    monitor.push(value, await page.evaluate(() => Date.now()) + 100)
    await expect(reading).toContainText(`${value}.0`)
  }
})

test('paused readings ignore incoming pushes and late snapshots until resumed', async ({ page, request }) => {
  const monitor = await prepare(page, request)
  await realtimeReady(page)
  const row = page.getByRole('row').filter({ hasText: 'Temp A' })
  let release!: () => void
  const gate = new Promise<void>(resolve => { release = resolve })
  await page.route(url => url.pathname === '/api/realtime', async route => {
    await gate
    await route.fulfill({ json: values(30, baseTs + 200) })
  })
  const pending = page.waitForRequest(request => new URL(request.url()).pathname === '/api/realtime')
  await page.locator('[data-test="snapshot-refresh"]').click()
  await pending
  try {
    await page.locator('[data-test="freeze-toggle"]').click()
    await expect(page.locator('[data-test="realtime-frozen"]')).toContainText('当前显示的不是最新数据')
    await expect(page.locator('[data-test="snapshot-refresh"]')).toBeDisabled()
    monitor.push(20, baseTs + 100)
    const response = page.waitForResponse(response => new URL(response.url()).pathname === '/api/realtime')
    release()
    await (await response).finished()
    await page.evaluate(() => new Promise(requestAnimationFrame))
    await expect(row.locator('.value-strong')).toContainText('10.00')
    await page.route(url => url.pathname === '/api/realtime', route => route.fulfill({ json: values(40, baseTs + 300) }))
    await page.locator('[data-test="realtime-frozen"]').getByRole('button', { name: '继续刷新' }).click()
    await expect(row.locator('.value-strong')).toContainText('40.00')
    await expect(page.locator('[data-test="realtime-frozen"]')).toHaveCount(0)
  } finally { release() }
})

test('a late snapshot cannot replace a newer pushed reading', async ({ page, request }) => {
  const monitor = await prepare(page, request)
  await realtimeReady(page)
  const row = page.getByRole('row').filter({ hasText: 'Temp A' })
  let release!: () => void
  const gate = new Promise<void>(resolve => { release = resolve })
  await page.route(url => url.pathname === '/api/realtime', async route => {
    await gate
    await route.fulfill({ json: values(15, baseTs + 100) })
  })
  const pending = page.waitForRequest(request => new URL(request.url()).pathname === '/api/realtime')
  await page.locator('[data-test="snapshot-refresh"]').click()
  await pending
  try {
    monitor.push(77, baseTs + 300)
    await expect(row.locator('.value-strong')).toContainText('77.00')
    const response = page.waitForResponse(response => new URL(response.url()).pathname === '/api/realtime')
    release()
    await (await response).finished()
    await page.evaluate(() => new Promise(requestAnimationFrame))
    await expect(row.locator('.value-strong')).toContainText('77.00')
    monitor.push(66, baseTs + 200)
    await page.evaluate(() => new Promise(requestAnimationFrame))
    await expect(row.locator('.value-strong')).toContainText('77.00')
  } finally { release() }
})

test('realtime trend link selects that tag and keeps the context after refresh', async ({ page, request }) => {
  const monitor = await prepare(page, request)
  await realtimeReady(page)
  await page.getByRole('row').filter({ hasText: 'Temp B' }).locator('[data-test="realtime-trend"]').click()
  await expect(page).toHaveURL('/trends?tagId=912')
  await expect(page.locator('[data-test="selected-count"]')).toHaveText('已选 1')
  await expect(page.locator('[data-test="point-count"]')).toHaveText('2 点 / 1 测点')
  expect(monitor.queries.at(-1)!.tagIds).toEqual([912])
  await page.reload()
  await expect(page.locator('[data-test="point-count"]')).toHaveText('2 点 / 1 测点')
  expect(monitor.queries.at(-1)!.tagIds).toEqual([912])
})

test('clearing tags invalidates pending history and analysis without reviving old results', async ({ page, request }) => {
  await prepare(page, request)
  let releaseHistory!: () => void, releaseAnalysis!: () => void
  const historyGate = new Promise<void>(resolve => { releaseHistory = resolve })
  const analysisGate = new Promise<void>(resolve => { releaseAnalysis = resolve })
  await page.route(url => url.pathname === '/api/history/analysis', async route => { await analysisGate; await route.fallback() })
  const pendingAnalysis = page.waitForRequest(request => new URL(request.url()).pathname === '/api/history/analysis')
  await page.goto('/trends?tagId=911')
  await expect(page.locator('[data-test="point-count"]')).toBeVisible()
  await pendingAnalysis
  await page.route(url => url.pathname === '/api/history', async route => { await historyGate; await route.fallback() })
  const pendingHistory = page.waitForRequest(request => new URL(request.url()).pathname === '/api/history')
  await page.getByRole('button', { name: '查询', exact: true }).click()
  await pendingHistory
  try {
    await page.locator('[data-test="clear-tags"]').click()
    const responses = [
      page.waitForResponse(response => new URL(response.url()).pathname === '/api/history'),
      page.waitForResponse(response => new URL(response.url()).pathname === '/api/history/analysis')
    ]
    releaseHistory(); releaseAnalysis()
    await Promise.all((await Promise.all(responses)).map(response => response.finished()))
    await page.evaluate(() => new Promise(requestAnimationFrame))
    await expect(page.locator('[data-test="chart-empty"]')).toContainText('请先选择测点')
    await expect(page.locator('[data-test="point-count"]')).toHaveCount(0)
    await expect(page.locator('[data-test="history-analysis"]')).toHaveCount(0)
    await expect(page.locator('[data-test="playback-bar"]')).toHaveCount(0)
    await expect(page.locator('[data-test="trend-result-context"]')).toHaveCount(0)
    await expect(page.locator('[data-test="trend-export"]')).toBeDisabled()
  } finally { releaseHistory(); releaseAnalysis() }
})

test('changed selection is marked pending and cannot export or silently refresh the old curves', async ({ page, request }) => {
  const monitor = await prepare(page, request)
  await page.goto('/trends?tagId=911')
  await expect(page.locator('[data-test="point-count"]')).toHaveText('2 点 / 1 测点')
  await page.locator('[data-test="tag-picker"]').click()
  await page.getByRole('option', { name: '锅炉间 / Temp B', exact: true }).click()
  await page.keyboard.press('Escape')
  await expect(page.locator('[data-test="trend-pending-conditions"]')).toContainText('仍显示上次结果')
  await expect(page.locator('[data-test="trend-export"]')).toBeDisabled()
  await expect(page.locator('[data-test="point-count"]')).toHaveText('2 点 / 1 测点')
  const before = monitor.queries.length
  await page.locator('[data-test="auto-refresh"]').click()
  await page.evaluate(() => new Promise(requestAnimationFrame))
  expect(monitor.queries).toHaveLength(before)
  await page.getByRole('button', { name: '查询', exact: true }).click()
  await expect(page.locator('[data-test="point-count"]')).toHaveText('4 点 / 2 测点')
  await expect(page.locator('[data-test="trend-pending-conditions"]')).toHaveCount(0)
  await expect(page.locator('[data-test="trend-export"]')).toBeEnabled()
  expect(monitor.queries.at(-1)!.tagIds).toEqual([911, 912])
})

test('failed range queries preserve the displayed range, playback bounds and quality intervals', async ({ page, request }) => {
  await prepare(page, request)
  await page.goto('/trends?tagId=911')
  const context = page.locator('[data-test="trend-result-context"]')
  await expect(page.locator('[data-test="analysis-table"]')).toBeVisible()
  const displayed = await context.textContent()
  const slider = page.locator('[data-test="playback-slider"]').getByRole('slider')
  const start = await slider.getAttribute('aria-valuemin'), end = await slider.getAttribute('aria-valuemax')
  const quality = page.locator('[data-test="quality-911"] .quality-seg').first()
  await expect.poll(() => quality.evaluate(el => el.style.width)).toBe('50%')
  await page.route(url => url.pathname === '/api/history', route => route.fulfill({ status: 500, json: { message: '新时间范围暂时不可用' } }))
  await page.getByRole('button', { name: '6 小时', exact: true }).click()
  await expect(page.locator('[data-test="trend-query-error"]')).toContainText('新时间范围暂时不可用')
  await expect(context).toHaveText(displayed!)
  await expect(slider).toHaveAttribute('aria-valuemin', start!)
  await expect(slider).toHaveAttribute('aria-valuemax', end!)
  await expect(page.locator('[data-test="trend-export"]')).toBeDisabled()
  await expect(page.locator('[data-test="trend-pending-conditions"]')).toBeVisible()
  await expect.poll(() => quality.evaluate(el => el.style.width)).toBe('50%')
  await page.screenshot({ path: test.info().outputPath('trend-result-context.png'), animations: 'disabled' })
})

test('automatic refresh advances a relative time window to the current time', async ({ page, request }) => {
  const monitor = await prepare(page, request)
  await page.clock.install()
  await page.goto('/trends?tagId=911')
  await expect(page.locator('[data-test="point-count"]')).toBeVisible()
  await page.locator('[data-test="auto-refresh"]').click()
  await expect.poll(() => monitor.queries.length).toBeGreaterThanOrEqual(2)
  await expect(page.getByRole('button', { name: '查询', exact: true })).not.toHaveClass(/is-loading/)
  const before = monitor.queries.at(-1)!
  await page.clock.runFor(31_000)
  await expect.poll(() => monitor.queries.length).toBeGreaterThanOrEqual(3)
  const after = monitor.queries.at(-1)!
  expect(after.to).toBeGreaterThanOrEqual(before.to + 29_000)
  expect(after.to - after.from).toBe(3600_000)
})

test('an unavailable tag context shows a clear warning without selecting unrelated tags', async ({ page, request }) => {
  const monitor = await prepare(page, request)
  await page.goto('/trends?tagId=99999')
  await expect(page.locator('[data-test="trend-context-error"]')).toContainText('不存在或无查看权限')
  await expect(page.locator('[data-test="selected-count"]')).toHaveText('已选 0')
  await expect(page.locator('[data-test="point-count"]')).toHaveCount(0)
  expect(monitor.queries).toHaveLength(0)
})
