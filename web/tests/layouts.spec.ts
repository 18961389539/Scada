import { test, expect, type APIRequestContext, type Page } from '@playwright/test'
import type { HmiLayout } from '../src/api/hmiLayouts'

async function login(request: APIRequestContext) {
  const r = await request.post('/api/auth/login', { data: { userName: 'admin', password: 'admin123' } }); expect(r.ok()).toBeTruthy()
  const result = await r.json(); return { ...result, headers: { Authorization: `Bearer ${result.token}` } }
}
const node = (id: string, text: string, action: object, x = 40) => ({ id, type: 'button', x, y: 40, width: 200, height: 60, rotation: 0, text, color: '#7394b8', activeColor: '#34d399', unit: '', decimals: 1, min: 0, max: 100, activeThreshold: 0.5, tagId: null, action })
const none = { type: 'none', screenId: null, tagId: null, value: 0 }

test('multi-screen layout: workstation assignment, regions with navigation history, faceplate windows, monitors and new window', async ({ page, request }) => {
  test.setTimeout(90000)
  const auth = await login(request), h = auth.headers, u = `${Date.now()}`.slice(-6)
  async function screen(name: string, nodes: object[]) {
    const s = await (await request.post('/api/screens', { headers: h, data: { name } })).json()
    const saved = await (await request.put(`/api/screens/${s.id}`, { headers: h, data: { name, revision: s.revision, document: { schemaVersion: 1, width: 1200, height: 720, background: '#101b2d', nodes } } })).json()
    const p = await request.post(`/api/screens/${s.id}/publish`, { headers: h, data: { revision: saved.revision } }); expect(p.ok(), await p.text()).toBeTruthy(); return s.id as number
  }
  const detail = await screen(`详情-${u}`, [node('d', '详情画面', none)])
  const face = await screen(`面板-${u}`, [node('f', '泵面板', none)])
  const overview = await screen(`总览-${u}`, [node('go', '去详情', { type: 'navigate', screenId: detail, tagId: null, value: 0 }), node('pop', '弹面板', { type: 'popup', screenId: face, tagId: null, value: 0 }, 300)])
  const layout = await (await request.post('/api/hmi/layouts', { headers: h, data: { name: `中控-${u}`, document: { monitors: [
    { name: '主屏', columns: 12, rows: 12, regions: [{ id: 'banner', kind: 'alarmBanner', row: 1, col: 1, rowSpan: 1, colSpan: 12 }, { id: 'nav', kind: 'navigation', row: 2, col: 1, rowSpan: 11, colSpan: 2 }, { id: 'main', kind: 'screen', screenId: overview, row: 2, col: 3, rowSpan: 11, colSpan: 10 }] },
    { name: '副屏', columns: 12, rows: 12, regions: [{ id: 'alarms', kind: 'alarmList', row: 1, col: 1, rowSpan: 12, colSpan: 12 }] }] } } })).json()
  expect(layout.id).toBeTruthy()
  const publication = await request.post(`/api/hmi/layouts/${layout.id}/publish`, { headers: h, data: { revision: layout.revision, note: '运行布局验收' } })
  expect(publication.ok(), await publication.text()).toBeTruthy()

  await page.addInitScript(({ token, user }) => { localStorage.setItem('scada_token', token); localStorage.setItem('scada_user', JSON.stringify(user)) }, auth)
  // 登记工作站并设为本机
  await page.goto('/config/layouts')
  await page.locator('[data-test="station-new"]').click()
  await page.locator('[data-test="station-name"]').fill('中文工位')
  await page.locator('[data-test="station-save"]').click()
  await expect(page.getByText('工作站名称须为 1～60 个字母、数字、点、下划线或连字符')).toBeVisible()
  await expect(page.locator('[data-test="station-name"]')).toBeVisible()
  await page.locator('[data-test="station-name"]').fill(`OP-${u}`)
  await page.locator('[data-test="station-layout"]').click(); await page.getByRole('option', { name: `中控-${u}` }).click()
  await page.locator('[data-test="station-save"]').click()
  await page.locator(`[data-test="station-local-OP-${u}"]`).click()
  await expect(page.locator('[data-test="local-station"]')).toContainText(`OP-${u}`)

  await page.goto('/console')
  await expect(page.locator('[data-test="console-station"]')).toContainText(`OP-${u}`)
  const main = page.locator('[data-test="region-main"]')
  await expect(page.locator('[data-test="alarm-banner"]')).toBeVisible()
  await main.locator('[data-node-id="go"]').click()
  await expect(main.locator('[data-node-id="d"]')).toBeVisible()
  await main.locator('[data-test="region-back-main"]').click()
  await expect(main.locator('[data-node-id="go"]')).toBeVisible()
  await main.locator('[data-test="region-forward-main"]').click()
  await expect(main.locator('[data-node-id="d"]')).toBeVisible()
  // 画面目录在主区域打开
  await page.locator(`[data-test="nav-${overview}"]`).click()
  await main.locator('[data-node-id="pop"]').click()
  await expect(page.locator('[data-test="faceplate"]')).toHaveCount(1)
  await expect(page.locator('[data-test="faceplate"] [data-node-id="f"]')).toBeVisible()
  await page.locator('[data-test="faceplate-close"]').click(); await expect(page.locator('[data-test="faceplate"]')).toHaveCount(0)
  // 新窗口（Window Management 不可用时退化为普通窗口）
  await main.locator('[data-test="region-window-main"]').click()
  const popup = page.waitForEvent('popup'); await page.locator('[data-test="monitor-open"]').click()
  const win = await popup; await win.waitForLoadState(); expect(win.url()).toContain(`/kiosk/screens/${overview}`)
  await expect(win.locator('[data-node-id="pop"]')).toBeVisible(); await win.close()
  // 切换显示器
  await page.locator('[data-test="console-monitor"]').getByText('副屏').click()
  await expect(page.locator('[data-test="alarm-list"]')).toBeVisible()
  await page.screenshot({ path: test.info().outputPath('console.png') })

  await page.locator('[data-test="console-monitor"]').getByText('主屏').click()
  await page.evaluate(() => { window.open = () => null })
  await main.locator('[data-test="region-window-main"]').click()
  const dialog = page.getByRole('dialog', { name: '选择显示器' })
  await expect(dialog).toBeVisible()
  await page.locator('[data-test="monitor-open"]').click()
  await expect(page.getByText('浏览器阻止了新窗口，请允许本站弹出窗口')).toBeVisible()
  await expect(dialog).toBeVisible()
  await dialog.getByRole('button', { name: '取消' }).click()
  await main.locator('[data-node-id="pop"]').click()
  await expect(page.locator('[data-test="faceplate"]')).toHaveCount(1)
  await page.locator('[data-test="faceplate"]').getByRole('button', { name: '新窗口' }).click()
  await expect(dialog).toBeVisible()
  await expect(page.locator('[data-test="faceplate"]')).toHaveCount(1)
  await page.locator('[data-test="monitor-open"]').click()
  await expect(dialog).toBeVisible()
  await expect(page.locator('[data-test="faceplate"]')).toHaveCount(1)
  await dialog.getByRole('button', { name: '取消' }).click()
  await page.locator('[data-test="console-open-all"]').click()
  await page.locator('[data-test="console-open-mapped"]').click()
  await expect(page.getByRole('dialog', { name: '显示器映射', exact: true }).getByText('浏览器阻止了新窗口，请允许本站弹出窗口', { exact: true })).toBeVisible()
  await expect(page.getByText('已在新窗口中打开')).toHaveCount(0)
  await expect(page.getByText('已以新窗口打开')).toHaveCount(0)
})

async function layoutFixture(page: Page, request: APIRequestContext, workstation = '') {
  const auth = await login(request)
  await page.addInitScript(({ token, workstation }) => { localStorage.setItem('scada_token', token); localStorage.removeItem('scada_user'); if (workstation) localStorage.setItem('scada_workstation', workstation); else localStorage.removeItem('scada_workstation') }, { token: auth.token, workstation })
  const layouts: HmiLayout[] = [{ id: 900001, name: '验收布局', revision: 1, updatedTs: Date.now(), updatedBy: 'admin', document: { monitors: [
    { name: '主屏', columns: 12, rows: 12, regions: [
      { id: 'banner', kind: 'alarmBanner', row: 1, col: 1, rowSpan: 1, colSpan: 12 },
      { id: 'nav', kind: 'navigation', row: 2, col: 1, rowSpan: 11, colSpan: 2 },
      { id: 'main', kind: 'screen', screenId: 900001, row: 2, col: 3, rowSpan: 11, colSpan: 5 },
      { id: 'aux', kind: 'screen', screenId: 900001, row: 2, col: 8, rowSpan: 11, colSpan: 5 }
    ] }, { name: '副屏', columns: 12, rows: 12, regions: [{ id: 'alarms', kind: 'alarmList', row: 1, col: 1, rowSpan: 12, colSpan: 12 }] }
  ] } }]
  const state = { layouts, failLayouts: false, failScreens: false, failStations: false, failSummary: false, failAlarms: false, summaryUnack: 7, returnedOnly: false }
  await page.route(url => url.pathname === '/api/hmi/layouts', route => state.failLayouts ? route.fulfill({ status: 500, json: { message: '布局服务不可用' } }) : route.fulfill({ json: state.layouts }))
  await page.route(url => url.pathname === '/api/hmi/workstations', route => state.failStations ? route.fulfill({ status: 500, json: { message: '工作站服务不可用' } }) : route.fulfill({ json: [] }))
  await page.route(url => url.pathname === '/api/hmi/workstations/resolve', route => route.fulfill({ status: 404, json: { message: '工作站未登记' } }))
  await page.route(url => url.pathname === '/api/screens', route => state.failScreens ? route.fulfill({ status: 500, json: { message: '画面目录不可用' } }) : route.fulfill({ json: [900001, 900002, 900003].map(id => ({ id, name: `画面 ${id}`, revision: 1, publishedRevision: 1, hasDraftChanges: false, updatedTs: Date.now(), updatedBy: 'admin' })) }))
  await page.route(/\/api\/screens\/90000[123](\?|$)/, route => {
    const id = Number(new URL(route.request().url()).pathname.split('/').at(-1))
    return route.fulfill({ json: { id, name: `画面 ${id}`, revision: 1, publishedRevision: 1, document: { schemaVersion: 1, width: 1200, height: 720, background: '#101b2d', nodes: [node('detail', '详情', { ...none, type: 'navigate', screenId: 900002 }), node('popup', '浮动面板', { ...none, type: 'popup', screenId: 900003 }, 300)] } } })
  })
  await page.route(url => url.pathname === '/api/alarms', route => state.failAlarms ? route.fulfill({ status: 500, json: { message: '报警明细不可用' } }) : route.fulfill({ json: [{ id: 900001, tagId: 900001, level: 'Warning', startTs: Date.now() - 2000, endTs: state.returnedOnly ? Date.now() - 1000 : null, ackTs: null, message: '验收报警' }] }))
  await page.route(url => url.pathname === '/api/alarms/summary', route => state.failSummary ? route.fulfill({ status: 500, json: { message: '报警汇总不可用' } }) : route.fulfill({ json: { unack: state.returnedOnly ? 0 : state.summaryUnack, acked: state.returnedOnly ? 0 : 2, rtnUnack: state.returnedOnly ? 1 : 0, suppressed: 0, shelved: 0, flood: false } }))
  return state
}

test('layout review distinguishes returned alarms from active alarms and includes pending acknowledgements', async ({ page, request }) => {
  const state = await layoutFixture(page, request)
  state.returnedOnly = true
  await page.goto('/console/900001')
  await expect(page.locator('[data-test="alarm-banner-state"]')).toHaveText('已恢复·待确认')
  await expect(page.locator('[data-test="alarm-banner-count"]')).toContainText('未确认 1 / 活动 0')
  await expect(page.locator('[data-test="alarm-returned-count"]')).toHaveText('恢复待确认 1')
  await expect(page.locator('[data-test="alarm-banner"]')).not.toHaveClass(/lvl-Warning/)
  await page.locator('[data-test="console-monitor"]').getByText('副屏', { exact: true }).click()
  await expect(page.locator('[data-test="alarm-list"] .alarm-state')).toHaveText('已恢复·待确认')
})

test('layout review retries a failed screen catalog without resetting region navigation', async ({ page, request }) => {
  const state = await layoutFixture(page, request)
  state.failScreens = true
  await page.goto('/console/900001')
  await expect(page.locator('[data-test="console-screens-error"]')).toBeVisible()
  await page.locator('[data-test="region-main"] [data-node-id="detail"]').click()
  await expect(page.locator('[data-test="region-main"] strong').first()).toHaveText('画面 900002')
  state.failScreens = false
  await page.locator('[data-test="console-refresh"]').click()
  await expect(page.locator('[data-test="console-screens-error"]')).toHaveCount(0)
  await expect(page.locator('[data-test="nav-900001"]')).toBeVisible()
  await expect(page.locator('[data-test="region-main"] strong').first()).toHaveText('画面 900002')
})

test('layout review clears old floating panels when changing layouts', async ({ page, request }) => {
  const state = await layoutFixture(page, request)
  state.layouts.push({ ...state.layouts[0], id: 900002, name: '另一运行布局' })
  await page.goto('/console/900001')
  await page.locator('[data-test="region-main"] [data-node-id="popup"]').click()
  await expect(page.locator('[data-test="faceplate"]')).toBeVisible()
  await page.locator('[data-test="console-layout"]').click()
  await page.getByRole('option', { name: '另一运行布局', exact: true }).click()
  await expect(page.locator('.console-bar > h1')).toHaveText('另一运行布局')
  await expect(page.locator('[data-test="faceplate"]')).toHaveCount(0)
})

test('layout review keeps floating-panel navigation inside its own window', async ({ page, request }) => {
  await layoutFixture(page, request)
  await page.goto('/console/900001')
  await page.locator('[data-test="region-main"] [data-node-id="popup"]').click()
  const panel = page.locator('[data-test="faceplate"]')
  await panel.locator('[data-node-id="detail"]').click()
  await expect(panel.locator('.faceplate-head > span').first()).toHaveText('画面 900002')
  await expect(panel.locator('.faceplate-head > span')).toHaveText('画面 900002')
  await expect(page.locator('[data-test="region-main"] strong').first()).toHaveText('画面 900001')
})

test('layout review excludes unpublished drafts from the runtime navigation catalog', async ({ page, request }) => {
  await layoutFixture(page, request)
  await page.route(url => url.pathname === '/api/screens', route => route.fulfill({ json: [
    { id: 900001, name: '运行画面', publishedRevision: 1 },
    { id: 900004, name: '未发布草稿', publishedRevision: null }
  ] }))
  await page.goto('/console/900001')
  await expect(page.locator('[data-test="nav-900001"]')).toHaveText('运行画面')
  await expect(page.locator('[data-test="nav-900004"]')).toHaveCount(0)
  await page.locator('[data-test="console-refresh"]').click()
  await expect(page.locator('[data-test="nav-900004"]')).toHaveCount(0)
})

test('layout review safely falls back from invalid monitor query values', async ({ page, request }) => {
  await layoutFixture(page, request)
  for (const monitor of ['0.5', 'NaN', '-1', 'Infinity']) {
    await page.goto(`/console/900001?monitor=${monitor}`)
    await expect(page.locator('[data-test="region-main"]')).toBeVisible()
  }
})

test('layout review bounds long runtime titles and exposes accessible region navigation', async ({ page, request }) => {
  const state = await layoutFixture(page, request)
  state.layouts[0].name = 'LongLayoutName'.repeat(12)
  state.layouts[0].document.monitors[0].name = 'LongMonitorName'.repeat(12)
  state.layouts[0].document.monitors[0].regions.find(region => region.id === 'main')!.title = 'LongRegionName'.repeat(12)
  for (const width of [1600, 390, 320]) {
    await page.setViewportSize({ width, height: 900 })
    await page.goto('/console/900001')
    const region = page.locator('[data-test="region-main"]')
    await expect(region).toBeVisible()
    await expect(region.getByRole('button', { name: '后退', exact: true })).toBeVisible()
    await expect(region.getByRole('button', { name: '主页', exact: true })).toBeVisible()
    expect(await region.locator('[data-test="region-window-main"]').evaluate(element => getComputedStyle(element).color)).toBe('rgb(220, 230, 240)')
    await region.locator('[data-node-id="detail"]').click()
    await expect(region.locator('[data-test="region-current-screen"]')).toContainText('画面 900002')
    await region.getByRole('button', { name: '主页', exact: true }).click()
    await expect(region.locator('[data-test="region-current-screen"]')).toContainText('画面 900001')
    expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(width)
    const regionBounds = (await region.boundingBox())!
    const titleBounds = (await region.locator('.region-title').boundingBox())!
    expect(titleBounds.x + titleBounds.width).toBeLessThanOrEqual(regionBounds.x + regionBounds.width)
    await page.screenshot({ path: test.info().outputPath(`layout-review-${width}.png`), animations: 'disabled' })
  }
})

test('layout review ignores a late workstation response after switching layouts', async ({ page, request }) => {
  const state = await layoutFixture(page, request, 'OP-REVIEW')
  state.layouts.push({ ...state.layouts[0], id: 900002, name: '另一运行布局' })
  let calls = 0
  let releaseOld = () => { throw new Error('No pending workstation request') }
  await page.route(url => url.pathname === '/api/hmi/workstations/resolve', async route => {
    const requestIndex = ++calls
    if (requestIndex === 1) await new Promise<void>(resolve => { releaseOld = resolve })
    await route.fulfill({ json: { workstation: { id: requestIndex, name: requestIndex === 1 ? '旧请求工作站' : '当前工作站', allowControl: false, layoutId: 900002 }, layout: null } })
  })
  await page.goto('/console/900001')
  await expect.poll(() => calls).toBe(1)
  await page.locator('[data-test="console-layout"]').click()
  await page.getByRole('option', { name: '另一运行布局', exact: true }).click()
  await expect(page.locator('[data-test="console-station"]')).toContainText('当前工作站')
  const oldResponse = page.waitForResponse(response => response.url().includes('/api/hmi/workstations/resolve'))
  releaseOld()
  await (await oldResponse).finished()
  await page.evaluate(() => new Promise<void>(resolve => requestAnimationFrame(() => resolve())))
  await expect(page.locator('[data-test="console-station"]')).toContainText('当前工作站')
  await expect(page.locator('.console-bar > h1')).toHaveText('另一运行布局')
})

test('layout usability rejects invalid explicit layouts and unresolved workstation assignments', async ({ page, request }) => {
  await layoutFixture(page, request, 'MISSING')
  await page.goto('/console/999999')
  await expect(page.locator('[data-test="console-layout-error"]')).toContainText('指定的运行布局不存在')
  await expect(page.locator('[data-test="region-main"]')).toHaveCount(0)
  await page.goto('/console')
  await expect(page.locator('[data-test="console-layout-error"]')).toContainText('工作站未登记')
  await expect(page.locator('[data-test="region-main"]')).toHaveCount(0)
  await page.goto('/console/900001')
  await expect(page.locator('[data-test="region-main"]')).toBeVisible()
  await expect(page.locator('[data-test="console-client-error"]')).toContainText('工作站未登记')
})

test('layout usability keeps alarm totals unknown on summary failure and recovers independently', async ({ page, request }) => {
  await page.clock.install()
  const state = await layoutFixture(page, request)
  await page.goto('/console/900001')
  await expect(page.locator('[data-test="alarm-banner-count"]')).toContainText('7')
  state.failSummary = true
  await page.clock.runFor(3500)
  await expect(page.locator('[data-test="console-summary-error"]')).toContainText('报警汇总不可用')
  await expect(page.locator('[data-test="alarm-banner-count"]')).toContainText('--')
  await expect(page.locator('[data-test="alarm-banner"]')).toContainText('验收报警')
  state.failSummary = false; state.summaryUnack = 3; state.failAlarms = true
  await page.clock.runFor(3500)
  await expect(page.locator('[data-test="console-summary-error"]')).toHaveCount(0)
  await expect(page.locator('[data-test="alarm-banner-count"]')).toContainText('3')
  await expect(page.locator('[data-test="console-alarm-error"]')).toContainText('报警明细不可用')
})

test('layout usability preserves the chosen navigation target and applies versions explicitly', async ({ page, request }) => {
  await page.clock.install()
  const state = await layoutFixture(page, request)
  await page.goto('/console/900001')
  await page.locator('[data-test="console-target-region"]').click()
  await page.getByRole('option', { name: 'aux', exact: true }).click()
  await page.locator('[data-test="nav-900002"]').click()
  await expect(page.locator('[data-test="region-aux"] strong').first()).toHaveText('画面 900002')
  await expect(page.locator('[data-test="region-main"] strong').first()).toHaveText('画面 900001')
  state.layouts[0].revision = 2
  await page.clock.runFor(11000)
  await expect(page.locator('[data-test="console-update"]')).toContainText('V2')
  await expect(page.locator('[data-test="console-version"]')).toHaveText('V1')
  await page.getByRole('button', { name: '应用新版本', exact: true }).click()
  await page.locator('.el-message-box').getByRole('button', { name: '确定', exact: true }).click()
  await expect(page.locator('[data-test="console-version"]')).toHaveText('V2')
  await expect(page.locator('[data-test="region-aux"] strong').first()).toHaveText('画面 900001')
})

test('layout usability isolates catalog and workstation failures from usable layouts', async ({ page, request }) => {
  const state = await layoutFixture(page, request)
  state.failScreens = state.failStations = true
  await page.goto('/config/layouts')
  await expect(page.locator('[data-test="layouts"]')).toContainText('验收布局')
  await expect(page.locator('[data-test="stations-load-error"]')).toContainText('工作站服务不可用')
  await expect(page.locator('[data-test="layouts-screens-error"]')).toContainText('画面目录不可用')
  state.failScreens = state.failStations = false
  await page.getByRole('button', { name: '刷新', exact: true }).click()
  await expect(page.locator('[data-test="stations-load-error"]')).toHaveCount(0)
  await expect(page.locator('[data-test="layouts-screens-error"]')).toHaveCount(0)
})

test('layout usability confirms preset replacement, monitor deletion and unsaved editor closure', async ({ page, request }) => {
  await layoutFixture(page, request)
  await page.goto('/config/layouts')
  await page.locator('[data-test="layouts"]').getByRole('button', { name: '编辑', exact: true }).click()
  const dialog = page.getByRole('dialog', { name: '编辑布局', exact: true })
  await page.locator('[data-test="layout-preset"]').click()
  await expect(page.locator('.el-message-box')).toContainText('替换全部显示器和分区')
  await page.locator('.el-message-box').getByRole('button', { name: '取消', exact: true }).click()
  await expect(dialog.getByRole('tab', { name: '主屏', exact: true })).toBeVisible()
  await dialog.locator('[data-test="layout-remove-monitor"]:visible').click()
  await expect(page.locator('.el-message-box')).toContainText('全部分区将被移除')
  await page.locator('.el-message-box').getByRole('button', { name: '取消', exact: true }).click()
  await page.locator('[data-test="layout-name"]').fill('未保存布局')
  await dialog.getByRole('button', { name: '取消', exact: true }).click()
  await expect(page.locator('.el-message-box')).toContainText('尚未保存')
  await page.locator('.el-message-box').getByRole('button', { name: '取消', exact: true }).click()
  await expect(page.locator('[data-test="layout-name"]')).toHaveValue('未保存布局')
  await dialog.getByRole('button', { name: '取消', exact: true }).click()
  await page.locator('.el-message-box').getByRole('button', { name: '确定', exact: true }).click()
  await expect(dialog).toBeHidden()
})

test('layout usability preserves failed saves and retries the edited document without real writes', async ({ page, request }) => {
  const state = await layoutFixture(page, request)
  let submissions = 0
  await page.route(url => url.pathname === '/api/hmi/layouts/900001', route => {
    submissions++
    const body = route.request().postDataJSON()
    expect(body.document.monitors).toHaveLength(2)
    if (submissions === 1) return route.fulfill({ status: 500, json: { message: '模拟保存失败' } })
    Object.assign(state.layouts[0], body, { revision: 2 })
    return route.fulfill({ json: state.layouts[0] })
  })
  await page.goto('/config/layouts')
  await page.locator('[data-test="layouts"]').getByRole('button', { name: '编辑', exact: true }).click()
  await page.locator('[data-test="layout-name"]').fill('保存重试')
  await page.locator('[data-test="layout-save"]').click()
  await expect(page.locator('[data-test="layout-save-error"]')).toContainText('模拟保存失败')
  await expect(page.locator('[data-test="layout-name"]')).toHaveValue('保存重试')
  await page.locator('[data-test="layout-save"]').click()
  await expect(page.locator('[data-test="layout-dialog"]')).toBeHidden()
  await expect(page.locator('[data-test="layouts"]')).toContainText('保存重试')
  expect(submissions).toBe(2)
})

test('layout usability confirms monitor mappings and preserves blocked-window feedback', async ({ page, request }) => {
  await layoutFixture(page, request)
  await page.goto('/console/900001')
  await page.evaluate(() => { window.open = () => null })
  await page.locator('[data-test="console-open-all"]').click()
  const dialog = page.getByRole('dialog', { name: '显示器映射', exact: true })
  await expect(dialog).toBeVisible()
  await expect(page.locator('[data-test="console-monitor-map-1"]')).toContainText('普通新窗口')
  await page.locator('[data-test="console-open-mapped"]').click()
  await expect(dialog).toContainText('浏览器阻止了新窗口')
  await expect(dialog).toBeVisible()
  await page.evaluate(() => { window.open = () => ({ moveTo() {}, resizeTo() {} }) as unknown as Window })
  await page.locator('[data-test="console-open-mapped"]').click()
  await expect(dialog).toBeHidden()
})

test('layout usability maps physical screens explicitly and warns about overlapping targets', async ({ page, request }) => {
  const state = await layoutFixture(page, request)
  state.layouts[0].document.monitors.push({ name: '第三屏', columns: 12, rows: 12, regions: [] })
  await page.addInitScript(() => {
    const opened: { url: string; features: string }[] = []
    Object.defineProperty(window, 'getScreenDetails', { value: async () => ({ screens: [0, 1, 2].map(index => ({ label: `物理屏 ${index + 1}`, availLeft: index * 1200, availTop: 0, availWidth: 1200, availHeight: 900, isPrimary: index === 0 })) }) })
    Object.defineProperty(window, '__openedWindows', { value: opened })
    window.open = (url, _name, features) => { opened.push({ url: String(url), features: features ?? '' }); return { moveTo() {}, resizeTo() {} } as unknown as Window }
  })
  await page.goto('/console/900001')
  await page.locator('[data-test="console-open-all"]').click()
  const dialog = page.getByRole('dialog', { name: '显示器映射', exact: true })
  await expect(dialog).toContainText('当前窗口 (物理屏 1)')
  await page.locator('[data-test="console-monitor-map-1"]').click()
  await page.getByRole('listbox', { name: '副屏', exact: true }).getByRole('option', { name: /物理屏 1/ }).click()
  await expect(dialog).toContainText('窗口可能重叠')
  await page.locator('[data-test="console-monitor-map-1"]').click()
  await page.getByRole('listbox', { name: '副屏', exact: true }).getByRole('option', { name: /物理屏 2/ }).click()
  await page.locator('[data-test="console-monitor-map-2"]').click()
  await page.getByRole('listbox', { name: '第三屏', exact: true }).getByRole('option', { name: /物理屏 2/ }).click()
  await expect(dialog).toContainText('窗口可能重叠')
  await page.locator('[data-test="console-monitor-map-2"]').click()
  await page.getByRole('listbox', { name: '第三屏', exact: true }).getByRole('option', { name: /物理屏 3/ }).click()
  await expect(dialog.getByText('多个逻辑屏映射到同一物理屏，窗口可能重叠。')).toHaveCount(0)
  await page.locator('[data-test="console-open-mapped"]').click()
  expect(await page.evaluate(() => Reflect.get(window, '__openedWindows'))).toEqual([
    { url: '/console/900001?monitor=1', features: 'popup,left=1200,top=0,width=1200,height=900' },
    { url: '/console/900001?monitor=2', features: 'popup,left=2400,top=0,width=1200,height=900' }
  ])
})

test('layout usability keeps editors and floating panels inside desktop and mobile viewports', async ({ page, request }) => {
  await layoutFixture(page, request)
  for (const width of [1600, 390, 320]) {
    await page.setViewportSize({ width, height: 900 })
    await page.goto('/config/layouts')
    await page.locator('[data-test="layouts"], [data-test="layout-cards"]').getByRole('button', { name: '编辑', exact: true }).click()
    const dialog = page.getByRole('dialog', { name: '编辑布局', exact: true })
    await expect(dialog).toBeVisible()
    const bounds = await dialog.locator('.el-dialog').count() ? await dialog.locator('.el-dialog').boundingBox() : await dialog.boundingBox()
    expect(bounds!.x).toBeGreaterThanOrEqual(0)
    expect(bounds!.x + bounds!.width).toBeLessThanOrEqual(width)
    expect(await page.locator('[data-test="layout-preview"]:visible').evaluate(element => element.getBoundingClientRect().right)).toBeLessThanOrEqual(width)
    await page.screenshot({ path: test.info().outputPath(`layout-editor-${width}.png`), animations: 'disabled' })
    await dialog.getByRole('button', { name: '取消', exact: true }).click()
    await page.goto('/console/900001')
    await page.locator('[data-test="region-main"] [data-node-id="popup"]').click()
    const panel = page.locator('[data-test="faceplate"]')
    await expect(panel).toBeVisible()
    let panelBounds = (await panel.boundingBox())!
    await page.mouse.move(panelBounds.x + 30, panelBounds.y + 10)
    await page.mouse.down(); await page.mouse.move(width - 1, 890); await page.mouse.up()
    panelBounds = (await panel.boundingBox())!
    expect(panelBounds.x + panelBounds.width).toBeLessThanOrEqual(width)
    expect(panelBounds.y + panelBounds.height).toBeLessThanOrEqual(900)
    await panel.evaluate(element => { (element as HTMLElement).style.width = '1200px'; (element as HTMLElement).style.height = '800px' })
    await expect.poll(async () => { const bounds = (await panel.boundingBox())!; return bounds.x + bounds.width }).toBeLessThanOrEqual(width)
    await expect.poll(async () => { const bounds = (await panel.boundingBox())!; return bounds.y + bounds.height }).toBeLessThanOrEqual(900)
    await expect(page.locator('[data-test="faceplate-close"]')).toBeVisible()
    expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(width)
    await page.screenshot({ path: test.info().outputPath(`layout-runtime-${width}.png`), animations: 'disabled' })
    if (width === 1600) {
      await page.setViewportSize({ width: 390, height: 640 })
      await expect.poll(async () => { const bounds = (await panel.boundingBox())!; return bounds.x + bounds.width }).toBeLessThanOrEqual(390)
      await expect.poll(async () => { const bounds = (await panel.boundingBox())!; return bounds.y + bounds.height }).toBeLessThanOrEqual(640)
    }
    await page.locator('[data-test="faceplate-close"]').click()
  }
})
