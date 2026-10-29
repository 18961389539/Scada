import { test, expect, type APIRequestContext, type Page } from '@playwright/test'
import type { AlarmEvent, AuthUser, TagValue } from '../src/types'
import type { ScreenDocument, ScreenNode } from '../src/screens/model'

const screenId = 930001
const startTs = Date.now()
const alarm = (id: number, level: AlarmEvent['level'], overrides: Partial<AlarmEvent> = {}): AlarmEvent => ({
  id, level, tagId: 911, deviceId: 901, ruleId: id, value: 1, startTs: startTs - id * 1000,
  endTs: null, ackTs: null, ackUser: null, message: `报警 ${id}`, ...overrides
})
const states = [
  { value: 0, label: '停机', color: '#b6c4d5' },
  { value: 1, label: '运行', color: '#dce6f0' },
  { value: 2, label: '故障', color: '#ff8e8e' }
]
const reading = (id: string, tagId: number, x: number, y: number, extra: Partial<ScreenNode> = {}): ScreenNode => ({
  id, tagId, x, y, type: 'value', width: 280, height: 80, rotation: 0, text: id,
  color: '#b6c4d5', activeColor: '#34d399', unit: '', decimals: 1, min: 0, max: 100,
  activeThreshold: .5, action: { type: 'none', tagId: null, screenId: null, value: 0 }, ...extra
})
const document: ScreenDocument = {
  schemaVersion: 1, width: 800, height: 300, background: '#101b2d',
  nodes: [
    reading('state', 911, 24, 24, { text: '运行状态', states, decimals: 0 }),
    reading('state-kpi', 911, 648, 24, { type: 'kpi', text: '运行状态 KPI', width: 128, states, decimals: 0 }),
    reading('count', 912, 336, 24, { text: '累计总产量', decimals: 0, unit: '件' }),
    reading('pressure', 913, 24, 128, { text: '压装压力（实际）', decimals: 2, unit: 'MPa' }),
    reading('material', 914, 336, 128, { text: '缺料等待', decimals: 0, states: [
      { value: 0, label: '未等待', color: '#b6c4d5' }, { value: 1, label: '等待中', color: '#ffd166' }
    ] })
  ]
}

async function fixture(page: Page, request: APIRequestContext, options: { viewer?: boolean; locale?: string } = {}) {
  const response = await request.post('/api/auth/login', { data: { userName: 'admin', password: 'admin123' } })
  expect(response.ok()).toBeTruthy()
  const auth = await response.json() as { token: string; user: AuthUser }
  if (options.viewer) auth.user = { ...auth.user, role: 'Viewer', access: { global: ['View'], areas: [] } }
  await page.addInitScript(({ auth, locale }) => {
    localStorage.setItem('scada_token', auth.token)
    localStorage.setItem('scada_user', JSON.stringify(auth.user))
    localStorage.setItem('scada_locale', locale)
  }, { auth, locale: options.locale ?? 'zh-CN' })
  const samples: TagValue[] = [
    { tagId: 911, value: 1, quality: 'Good', ts: startTs },
    { tagId: 912, value: 1234567, quality: 'Good', ts: startTs },
    { tagId: 913, value: 6.25, quality: 'Good', ts: startTs },
    { tagId: 914, value: 0, quality: 'Good', ts: startTs }
  ]
  const state = {
    alarms: [
      alarm(4, 'Info'), alarm(3, 'Warning', { ackTs: startTs, ackUser: 'operator' }),
      alarm(2, 'Critical'), alarm(1, 'Critical', { endTs: startTs })
    ],
    samples, ageMs: 0, alarmFailure: false, acknowledgements: [] as number[],
    pageQueries: [] as { live: string | null; state: string | null; from: string | null; to: string | null; limit: number }[]
  }
  const layout = {
    id: screenId, name: '运行布局验收', revision: 1, updatedTs: startTs, updatedBy: 'admin',
    document: { monitors: [{ name: '生产与报警', rows: 12, columns: 12, regions: [
      { id: 'main', kind: 'screen', screenId, row: 1, col: 1, rowSpan: 8, colSpan: 12 },
      { id: 'alarms', kind: 'alarmList', title: '自定义区域名称', row: 9, col: 1, rowSpan: 4, colSpan: 12 }
    ] }] }
  }
  await page.route(url => url.pathname === '/api/hmi/layouts', route => route.fulfill({ json: [layout] }))
  await page.route(url => url.pathname === '/api/screens', route => route.fulfill({ json: [
    { id: screenId, name: '装配线验收', revision: 1, publishedRevision: 1, hasDraftChanges: false, updatedTs: startTs, updatedBy: 'admin' }
  ] }))
  await page.route(url => url.pathname === `/api/screens/${screenId}`, route => route.fulfill({ json: {
    id: screenId, name: '装配线验收', revision: 1, publishedRevision: 1, document
  } }))
  await page.route(url => url.pathname === '/api/devices', route => route.fulfill({ json: [
    { id: 901, name: '装配线', areaId: 1, protocol: 'ModbusTcp', host: '127.0.0.1', port: 1502, enabled: false, pollIntervalMs: 1000 }
  ] }))
  const tagNodes = [...new Map(document.nodes.map(node => [node.tagId, node])).values()]
  await page.route(url => url.pathname === '/api/tags', route => route.fulfill({ json: tagNodes.map(node => ({
    id: node.tagId, name: node.text, deviceId: 901, kind: 'Physical', area: 'HoldingRegister', dataType: node.id === 'material' ? 'Bool' : 'Float',
    unit: node.unit, writable: false, archiveMode: 'None', description: ''
  })) }))
  await page.route(url => url.pathname === '/api/realtime', route => route.fulfill({ json: state.samples.map(sample => ({ ...sample, ts: Date.now() - state.ageMs })) }))
  await page.routeWebSocket(/\/hubs\/realtime(?:\?|$)/, socket => {
    socket.onMessage(message => {
      for (const part of String(message).split('\x1e').filter(Boolean)) {
        const frame = JSON.parse(part)
        if (frame.protocol) socket.send('{}\x1e')
        else if (frame.type === 1 && frame.invocationId) socket.send(JSON.stringify({ type: 3, invocationId: frame.invocationId }) + '\x1e')
      }
    })
  })
  await page.route(url => url.pathname === '/api/alarms', route => state.alarmFailure
    ? route.fulfill({ status: 503, json: { message: '报警明细不可用' } })
    : route.fulfill({ json: state.alarms }))
  await page.route(url => url.pathname === '/api/alarms/page', route => {
    const query = new URL(route.request().url()).searchParams
    const filter = query.get('state'), offset = Number(query.get('offset') ?? 0), limit = Number(query.get('limit') ?? 100)
    expect([null, 'active']).toContain(filter)
    state.pageQueries.push({ live: query.get('live'), state: filter, from: query.get('from'), to: query.get('to'), limit })
    const items = filter === 'active' ? state.alarms.filter(alarm => alarm.endTs == null) : state.alarms
    return route.fulfill({ json: { items: items.slice(offset, offset + limit), total: items.length, offset, limit, truncated: false } })
  })
  await page.route(url => url.pathname === '/api/alarms/summary', route => route.fulfill({ json: {
    unack: state.alarms.filter(row => row.ackTs == null && row.endTs == null).length,
    acked: state.alarms.filter(row => row.ackTs != null && row.endTs == null).length,
    rtnUnack: state.alarms.filter(row => row.ackTs == null && row.endTs != null).length,
    suppressed: 0, shelved: 0, flood: false
  } }))
  await page.route(/\/api\/alarms\/\d+\/ack$/, route => {
    state.acknowledgements.push(Number(new URL(route.request().url()).pathname.split('/').at(-2)))
    return route.fulfill({ status: 503, json: { message: '确认服务不可用' } })
  })
  return state
}

test('alarm panel labels severity and actual scope without changing priority or custom region names', async ({ page, request }) => {
  await fixture(page, request)
  await page.goto(`/console/${screenId}`)
  const panel = page.locator('[data-test="alarm-list"]')
  await expect(panel.locator('.region-head')).toContainText('活动与待确认报警')
  await expect(panel.locator('.region-head')).toContainText('自定义区域名称')
  await expect(panel.getByRole('columnheader')).toHaveText(['级别', '状态', '发生时间', '报警内容', '操作'])
  const rows = panel.locator('.alarm-row')
  await expect(rows).toHaveCount(4)
  await expect(rows.locator('.alarm-level')).toHaveText(['严重', '严重', '警告', '提示'])
  await expect(rows.locator('.alarm-state')).toHaveText(['已恢复·待确认', '活动·未确认', '活动·已确认', '活动·未确认'])
  await expect(rows.locator('.alarm-content')).toHaveText(['报警 1', '报警 2', '报警 3', '报警 4'])
  await expect(rows.nth(2).getByRole('button')).toHaveCount(0)
  await expect(rows.nth(2)).toContainText('已确认')
})

test('alarm limit is informational and the full workbench remains keyboard reachable', async ({ page, request }) => {
  const state = await fixture(page, request)
  state.alarms = Array.from({ length: 205 }, (_, i) => alarm(i + 1, i < 5 ? 'Critical' : 'Warning', { endTs: i < 2 ? startTs : null }))
  await page.goto(`/console/${screenId}`)
  const panel = page.locator('[data-test="alarm-list"]')
  await expect(panel.locator('.alarm-row')).toHaveCount(200)
  const limit = panel.locator('[data-test="alarm-truncated"]')
  await expect(limit).toContainText('200')
  await expect(limit).not.toHaveClass(/alarm-error/)
  const all = panel.getByRole('link', { name: '查看全部报警', exact: true })
  await expect(all).toHaveAttribute('href', '/alarms?state=live')
  await all.focus()
  await expect(all).toBeFocused()
  await page.keyboard.press('Enter')
  await expect(page).toHaveURL(/\/alarms\?state=live$/)
  await expect(page.locator('[data-test="filter-live"]')).toHaveAttribute('aria-pressed', 'true')
  await expect(page.locator('.alarm-panel .el-table__body-wrapper .el-table__row')).toHaveCount(100)
  await expect(page.locator('[data-test="alarm-result-count"]')).toContainText('205')
  expect(state.pageQueries.at(-1)).toEqual({ live: 'true', state: null, from: null, to: null, limit: 100 })
  await page.locator('[data-test="filter-active"]').click()
  await expect(page.locator('[data-test="alarm-result-count"]')).toContainText('203')
  await expect(page.locator('[data-test="filter-active"]')).toHaveAttribute('aria-pressed', 'true')
})

test('alarm failures stay explicit and stale records cannot be acknowledged', async ({ page, request }) => {
  const state = await fixture(page, request)
  await page.goto(`/console/${screenId}`)
  const rows = page.locator('[data-test="alarm-list"] .alarm-row')
  await rows.first().getByRole('button').click()
  await expect(page.locator('[data-test="console-ack-error"]')).toContainText('确认服务不可用')
  expect(state.acknowledgements).toEqual([1])
  await expect(rows.first().locator('.alarm-state')).toHaveText('已恢复·待确认')
  state.alarmFailure = true
  await page.locator('[data-test="console-refresh"]').click()
  await expect(page.locator('[data-test="alarm-list"] [data-test="console-alarm-error"]')).toContainText('报警明细不可用')
  await expect(rows).toHaveCount(4)
  for (const button of await rows.getByRole('button').all()) await expect(button).toBeDisabled()
  expect(state.acknowledgements).toEqual([1])
})

test('read-only users see alarm states but no acknowledgement actions', async ({ page, request }) => {
  await fixture(page, request, { viewer: true })
  await page.goto(`/console/${screenId}`)
  const panel = page.locator('[data-test="alarm-list"]')
  await expect(panel.locator('.alarm-row')).toHaveCount(4)
  await expect(panel.getByRole('button')).toHaveCount(0)
  await expect(panel.getByRole('link', { name: '查看全部报警' })).toBeVisible()
})

test('empty alarm results and read failures remain distinct', async ({ page, request }) => {
  const state = await fixture(page, request)
  state.alarms = []
  await page.goto(`/console/${screenId}`)
  const panel = page.locator('[data-test="alarm-list"]')
  await expect(panel.locator('.alarm-empty')).toHaveText('暂无活动或待确认报警')
  state.alarmFailure = true
  await page.reload()
  await expect(panel.locator('[data-test="console-alarm-error"]')).toContainText('报警明细不可用')
  await expect(panel.locator('.alarm-empty')).toHaveCount(0)
})

for (const viewport of [{ width: 1366, height: 768 }, { width: 1920, height: 1080 }, { width: 390, height: 844 }]) {
  test(`alarm columns and persistent actions remain usable at ${viewport.width}`, async ({ page, request }) => {
    await page.setViewportSize(viewport)
    const state = await fixture(page, request)
    state.alarms = Array.from({ length: 40 }, (_, i) => alarm(i + 1, 'Critical', { message: `长报警内容-${i}-${'工艺异常'.repeat(60)}` }))
    await page.goto(`/console/${screenId}`)
    const panel = page.locator('[data-test="alarm-list"]')
    const scroll = panel.getByRole('region', { name: '报警明细，可用方向键滚动' })
    await expect(panel.locator('.alarm-row')).toHaveCount(40)
    await scroll.focus()
    await page.keyboard.press('End')
    await expect.poll(() => scroll.evaluate(element => element.scrollTop)).toBeGreaterThan(0)
    await expect(panel.getByRole('link', { name: '查看全部报警' })).toBeInViewport()
    await expect(panel.getByRole('columnheader', { name: '操作', exact: true })).toBeInViewport()
    if (viewport.width < 800) {
      await page.keyboard.press('ArrowRight')
      await expect.poll(() => scroll.evaluate(element => element.scrollLeft)).toBeGreaterThan(0)
    }
    expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(viewport.width)
  })
}

test('value readouts map configured states and retain raw unknown codes', async ({ page, request }) => {
  const state = await fixture(page, request)
  for (const [value, label] of [[0, '停机'], [1, '运行'], [2, '故障'], [9, '未知状态']] as const) {
    state.samples[0].value = value
    await page.goto(`/console/${screenId}`)
    await expect(page.locator('[data-node-id="state"] > text').first()).toHaveText(`${label} (${value})`)
    await expect(page.locator('[data-node-id="state-kpi"] > g > text').last()).toHaveText(`${label} (${value})`)
    await expect(page.locator('[data-node-id="state"]')).toHaveAttribute('data-state', value === 9 ? 'unknown' : `state-${value}`)
  }
  await expect(page.locator('[data-node-id="material"] > text').first()).toHaveText('未等待 (0)')
  state.samples[3].value = 1
  await page.reload()
  await expect(page.locator('[data-node-id="material"] > text').first()).toHaveText('等待中 (1)')
})

test('mapped readouts fail closed for bad quality and expired samples', async ({ page, request }) => {
  const state = await fixture(page, request)
  for (const failure of ['bad', 'stale']) {
    state.samples[0].quality = failure === 'bad' ? 'Bad' : 'Good'
    state.ageMs = failure === 'stale' ? 60000 : 0
    await page.goto(`/console/${screenId}`)
    await expect(page.locator('[data-node-id="state"] > text').first()).toHaveText('--')
    await expect(page.locator('[data-node-id="state"]')).toHaveAttribute('data-state', 'unknown')
    await expect(page.locator('[data-node-id="state-kpi"] > g > text').last()).toHaveText('--')
  }
})

test('count precision, measurement precision and zero values remain configuration driven', async ({ page, request }) => {
  const state = await fixture(page, request)
  const writes: string[] = []
  page.on('request', request => {
    const path = new URL(request.url()).pathname
    if (path.startsWith('/api/') && !['GET', 'HEAD', 'OPTIONS'].includes(request.method())) writes.push(path)
  })
  await page.goto(`/console/${screenId}`)
  await expect(page.locator('[data-node-id="count"] > text').first()).toHaveText('1234567 件')
  await expect(page.locator('[data-node-id="pressure"] > text').first()).toHaveText('6.25 MPa')
  state.samples[1].value = 0
  await page.reload()
  await expect(page.locator('[data-node-id="count"] > text').first()).toHaveText('0 件')
  expect(writes).toEqual([])
})

test('production charts keep large counter labels inside the SVG and identify their series', async ({ page, request }) => {
  await fixture(page, request)
  const chartDocument: ScreenDocument = {
    ...document, width: 1600, height: 440,
    nodes: [
      ...document.nodes,
      reading('trend', 912, 24, 288, { type: 'chart-line', tagId: null, tags: [912], width: 912, height: 132 }),
      reading('compare', 912, 960, 288, { type: 'chart-bar', tagId: null, tags: [912], width: 616, height: 132 })
    ]
  }
  await page.route(url => url.pathname === `/api/screens/${screenId}`, route => route.fulfill({ json: {
    id: screenId, name: '装配线验收', revision: 1, publishedRevision: 1, document: chartDocument
  } }))
  const chartEnd = Math.floor((startTs - 60000) / 60000) * 60000 + 35000
  await page.route(url => url.pathname === '/api/history', route => route.fulfill({ json: {
    series: [{ tagId: 912, points: [{ ts: chartEnd - 60000, value: 1200000 }, { ts: chartEnd, value: 1234567 }] }]
  } }))
  for (const width of [1366, 1920]) {
    await page.setViewportSize({ width, height: width === 1366 ? 768 : 1080 })
    await page.goto(`/console/${screenId}`)
    for (const id of ['compare', 'trend']) {
      const chart = page.locator(`[data-node-id="${id}"] [data-chart]`)
      await expect(chart).not.toHaveAttribute('data-unavailable', 'true')
      const svg = chart.locator('svg')
      await expect(svg).toHaveCount(1)
      const clipped = await svg.evaluate(element => {
        if (!(element instanceof SVGSVGElement)) throw new Error('Chart must render an SVG viewport.')
        const matrix = element.getScreenCTM()
        if (!matrix) throw new Error('Chart viewport must be rendered.')
        const origin = new DOMPoint(0, 0).matrixTransform(matrix)
        const corner = new DOMPoint(element.width.baseVal.value, element.height.baseVal.value).matrixTransform(matrix)
        return [...element.querySelectorAll('text')].map(text => {
          const box = text.getBoundingClientRect()
          return {
            text: text.textContent, anchor: text.getAttribute('text-anchor'),
            left: box.left - origin.x, right: box.right - corner.x,
            top: box.top - origin.y, bottom: box.bottom - corner.y
          }
        }).filter(box => box.left < -1 || box.right > 1 || box.top < -1 || box.bottom > 1)
      })
      expect(clipped, `${width}px ${id} chart labels must not be clipped`).toEqual([])
      await expect(chart).toContainText('累计总产量')
      await expect(chart).not.toContainText('#912')
    }
  }
})

test('alarm panel operational labels are also available in English', async ({ page, request }) => {
  await fixture(page, request, { locale: 'en-US' })
  await page.goto(`/console/${screenId}`)
  const panel = page.locator('[data-test="alarm-list"]')
  await expect(panel.locator('.region-head')).toContainText('Active and pending alarms')
  await expect(panel.getByRole('columnheader')).toHaveText(['Severity', 'State', 'Started at', 'Alarm message', 'Action'])
  await expect(panel.getByRole('link', { name: 'View all alarms', exact: true })).toBeVisible()
  await expect(panel.locator('.alarm-row').first().locator('.alarm-state')).toHaveText('Returned, awaiting acknowledgement')
  await panel.getByRole('link', { name: 'View all alarms', exact: true }).click()
  await expect(page.getByRole('button', { name: 'Active & pending', exact: true })).toHaveAttribute('aria-pressed', 'true')
})
