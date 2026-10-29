import { test, expect, type APIRequestContext, type Page } from './fixtures'
import type { HmiLayout } from '../src/api/hmiLayouts'

async function prepare(page: Page, request: APIRequestContext) {
  const response = await request.post('/api/auth/login', { data: { userName: 'admin', password: 'admin123' } })
  expect(response.ok()).toBeTruthy()
  const auth = await response.json()
  await page.addInitScript(({ token, user }) => {
    localStorage.setItem('scada_token', token)
    localStorage.setItem('scada_user', JSON.stringify(user))
    localStorage.setItem('scada_locale', 'zh-CN')
  }, auth)
  const screens = [
    { id: 910001, name: '锅炉/总览' }, { id: 910002, name: '锅炉/详情' },
    { id: 910003, name: '供水/总览' }, { id: 910004, name: '未发布草稿' }
  ].map(screen => ({ ...screen, revision: 1, publishedRevision: screen.id === 910004 ? null : 1, updatedTs: Date.now(), updatedBy: 'admin', hasDraftChanges: false }))
  const layout: HmiLayout = { id: 910001, name: '导航验收布局', revision: 1, updatedTs: Date.now(), updatedBy: 'admin', document: { monitors: [
    { name: '主屏', columns: 12, rows: 12, regions: [
      { id: 'nav', kind: 'navigation', row: 1, col: 1, rowSpan: 12, colSpan: 3 },
      { id: 'main', kind: 'screen', title: '主工艺区', screenId: 910001, row: 1, col: 4, rowSpan: 12, colSpan: 5 },
      { id: 'aux', kind: 'screen', title: '辅助区', screenId: 910003, row: 1, col: 9, rowSpan: 12, colSpan: 4 }
    ] }
  ] } }
  const state = { reads: [] as number[], checks: 0, writes: 0, failCatalog: false, failDetail: 0 }
  await page.route(url => url.pathname === '/api/hmi/layouts', route => route.fulfill({ json: [layout] }))
  await page.route(url => url.pathname === '/api/hmi/workstations/resolve', route => route.fulfill({ json: {
    workstation: { id: 1, name: new URL(route.request().url()).searchParams.get('name') ?? 'SECOND-OP', description: '', layoutId: layout.id, allowControl: true, updatedTs: Date.now(), updatedBy: 'admin' }, layout
  } }))
  await page.route(url => url.pathname === '/api/screens', route => state.failCatalog ? route.fulfill({ status: 500, json: { message: '画面目录暂不可用' } }) : route.fulfill({ json: screens }))
  await page.route(/\/api\/screens\/91000[123](\?|$)/, route => {
    const id = Number(new URL(route.request().url()).pathname.split('/').at(-1))
    state.reads.push(id)
    if (state.failDetail === id) return route.fulfill({ status: 500, json: { message: '画面内容暂不可用' } })
    const node = (nodeId: string, text: string, action: object, x: number, y = 40) => ({ id: nodeId, type: 'button', x, y, width: 200, height: 60, rotation: 0, text, color: '#7394b8', activeColor: '#34d399', unit: '', decimals: 1, min: 0, max: 100, activeThreshold: .5, tagId: null, action })
    return route.fulfill({ json: { ...screens.find(screen => screen.id === id), document: {
      schemaVersion: 1, width: 800, height: 500, background: '#101b2d', nodes: [
        node('next', '查看详情', { type: 'navigate', screenId: 910002, tagId: null, value: 0 }, 40),
        node('control', '启动设备', { type: 'command', screenId: null, tagId: 910011, value: 1 }, 280),
        node('popup', '打开面板', { type: 'popup', screenId: 910003, tagId: null, value: 0 }, 40, 140)
      ]
    } } })
  })
  await page.route(url => url.pathname === '/api/tags', route => route.fulfill({ json: [{ id: 910011, name: '启动命令', kind: 'Physical', deviceId: null, areaId: null, writable: true, unit: '' }] }))
  await page.route(url => url.pathname === '/api/devices', route => route.fulfill({ json: [] }))
  await page.route(url => url.pathname === '/api/realtime', route => route.fulfill({ json: [{ tagId: 910011, value: 0, ts: Date.now(), quality: 'Good' }] }))
  await page.route(url => url.pathname === '/api/alarms', route => route.fulfill({ json: [] }))
  await page.route(url => url.pathname === '/api/alarms/summary', route => route.fulfill({ json: { unack: 0, acked: 0, rtnUnack: 0, suppressed: 0, shelved: 0, flood: false } }))
  await page.route(url => url.pathname === '/api/control/check', route => {
    state.checks++
    return route.fulfill({ json: { tagId: 910011, tagName: '启动命令', current: 0, refusals: [], requireConfirm: true, dualApproval: false, limitMin: null, limitMax: null, maxStep: null, feedback: null, mode: 'Remote', lockouts: [] } })
  })
  await page.route(/\/api\/screens\/\d+\/actions\//, route => { state.writes++; return route.fulfill({ json: { success: true, readBack: 1 } }) })
  return state
}

test('console directory makes the active region and its current screen explicit', async ({ page, request }) => {
  await prepare(page, request)
  await page.goto('/console/910001')
  const main = page.locator('[data-test="region-main"]'), aux = page.locator('[data-test="region-aux"]')
  await expect(main.locator('[data-test="region-current-screen"]')).toHaveText('主工艺区 · 锅炉/总览')
  await expect(page.locator('[data-test="nav-910001"]')).toHaveAttribute('aria-current', 'page')
  await expect(page.locator('[data-test="nav-910004"]')).toHaveCount(0)
  await expect(page.locator('.directory-group h3')).toHaveText(['供水', '锅炉'])
  await page.getByRole('textbox', { name: '搜索画面', exact: true }).fill('详情')
  await expect(page.locator('.directory-screen')).toHaveCount(1)
  await page.locator('[data-test="nav-910002"]').click()
  await expect(main.locator('[data-test="region-current-screen"]')).toHaveText('主工艺区 · 锅炉/详情')
  await expect(aux.locator('[data-test="region-current-screen"]')).toHaveText('辅助区 · 供水/总览')
  await expect(page.locator('[data-test="nav-910002"]')).toHaveAttribute('aria-current', 'page')
  await main.locator('[data-test="region-back-main"]').click()
  await page.getByRole('textbox', { name: '搜索画面', exact: true }).fill('')
  await expect(page.locator('[data-test="nav-910001"]')).toHaveAttribute('aria-current', 'page')
  await aux.locator('[data-test="region-current-screen"]').click()
  await expect(page.locator('[data-test="nav-910003"]')).toHaveAttribute('aria-current', 'page')
  await page.locator('[data-test="nav-910002"]').click()
  await expect(aux.locator('[data-test="region-current-screen"]')).toHaveText('辅助区 · 锅炉/详情')
  await expect(main.locator('[data-test="region-current-screen"]')).toHaveText('主工艺区 · 锅炉/总览')
})

test('console directory preserves favorites and recent screens within a workstation', async ({ page, request }) => {
  await prepare(page, request)
  await page.goto('/console/910001')
  await page.locator('[data-test="directory-favorite-910002"]').click()
  await page.locator('[data-test="nav-910002"]').click()
  await page.locator('[data-test="directory-filter-favorites"]').click()
  await expect(page.locator('.directory-screen')).toHaveCount(1)
  await expect(page.locator('[data-test="nav-910002"]')).toBeVisible()
  await page.reload()
  await page.locator('[data-test="directory-filter-favorites"]').click()
  await expect(page.locator('[data-test="nav-910002"]')).toBeVisible()
  await page.locator('[data-test="directory-filter-recent"]').click()
  await expect(page.locator('[data-test="nav-910002"]')).toBeVisible()
  await page.evaluate(() => localStorage.setItem('scada_workstation', 'SECOND-OP'))
  await page.reload()
  await page.locator('[data-test="directory-filter-favorites"]').click()
  await expect(page.locator('[data-test="directory-empty"]')).toContainText('暂无收藏')
  await page.evaluate(() => { localStorage.removeItem('scada_workstation'); localStorage.removeItem('scada_workstation_id') })
  await page.reload()
  await page.locator('[data-test="directory-filter-favorites"]').click()
  await expect(page.locator('[data-test="nav-910002"]')).toBeVisible()
})

test('embedded screen actions stay in More while monitor mode blocks controls and preserves navigation', async ({ page, request }) => {
  const state = await prepare(page, request)
  await page.goto('/console/910001')
  const main = page.locator('[data-test="region-main"]')
  await expect(main.locator('.runtime-toolbar > strong')).toHaveCount(0)
  await expect(page.locator('[data-test="runtime-edit"]:visible')).toHaveCount(0)
  await main.locator('[data-test="runtime-more"]').click()
  const options = page.locator('.el-popover:visible')
  await expect(options.locator('[data-test="runtime-edit"]')).toBeVisible()
  await expect(options.locator('[data-test="runtime-version"]')).toHaveText('运行版本 1')
  const reads = state.reads.filter(id => id === 910001).length
  await options.locator('[data-test="runtime-local-refresh"]').click()
  await expect.poll(() => state.reads.filter(id => id === 910001).length).toBeGreaterThan(reads)
  await expect(options.locator('[data-test="runtime-edit"]')).toBeVisible()
  await main.locator('[data-test="region-current-screen"]').click()
  await main.locator('[data-node-id="control"]').click()
  await expect(page.locator('[data-test="control-confirm"]:visible')).toBeVisible()
  await expect.poll(() => state.checks).toBe(1)
  await page.locator('[data-test="control-confirm"]:visible').getByRole('button', { name: '取消', exact: true }).click()
  await page.locator('[data-test="console-monitor-mode"]').click()
  await expect(main.locator('[data-test="runtime-monitor-mode"]')).toBeVisible()
  await main.locator('[data-node-id="control"]').click()
  await expect(main.locator('[data-test="runtime-command-error"]')).toContainText('只读')
  expect(state.checks).toBe(1)
  expect(state.writes).toBe(0)
  await main.locator('[data-node-id="next"]').click()
  await expect(main.locator('[data-test="region-current-screen"]')).toHaveText('主工艺区 · 锅炉/详情')
  await expect(main.locator('[data-test="runtime-monitor-mode"]')).toBeVisible()
  await page.goto('/screens/910001')
  await expect(page.locator('.runtime-toolbar > strong')).toHaveText('锅炉/总览')
  await expect(page.locator('[data-test="runtime-edit"]')).toBeVisible()
  await expect(page.locator('[data-test="runtime-zoom"]')).toBeVisible()
  await expect(page.locator('[data-test="runtime-monitor-mode"]')).toHaveCount(0)
})

test('console titles use loaded screen names when the directory is unavailable', async ({ page, request }) => {
  const state = await prepare(page, request)
  state.failCatalog = true
  await page.goto('/console/910001')
  const main = page.locator('[data-test="region-main"]')
  await expect(page.locator('[data-test="console-screens-error"]')).toContainText('画面目录暂不可用')
  await expect(main.locator('[data-test="region-current-screen"]')).toHaveText('主工艺区 · 锅炉/总览')
  await main.locator('[data-node-id="next"]').click()
  await expect(main.locator('[data-test="region-current-screen"]')).toHaveText('主工艺区 · 锅炉/详情')
  state.failDetail = 910002
  await main.locator('[data-test="runtime-more"]').click()
  await page.locator('[data-test="runtime-local-refresh"]:visible').click()
  await expect(main.locator('[data-test="runtime-screen-error"]')).toContainText('画面内容暂不可用')
  await expect(main.locator('[data-test="region-current-screen"]')).toHaveText('主工艺区 · 画面 #910002')
})

test('read-only kiosk screens preserve the mode through popups and screen navigation', async ({ page, request }) => {
  const state = await prepare(page, request)
  await page.goto('/kiosk/screens/910001?monitorOnly=1')
  await expect(page.locator('[data-test="runtime-monitor-mode"]')).toBeVisible()
  await page.locator('[data-node-id="popup"]').click()
  const popup = page.getByRole('dialog')
  await expect(popup.locator('[data-test="runtime-monitor-mode"]')).toBeVisible()
  await popup.locator('[data-node-id="control"]').click()
  await expect(popup.locator('[data-test="runtime-command-error"]')).toContainText('只读')
  expect(state.checks).toBe(0)
  expect(state.writes).toBe(0)
  await popup.locator('[data-node-id="next"]').click()
  await expect(page).toHaveURL('/kiosk/screens/910002?monitorOnly=1')
  await expect(page.getByRole('dialog')).toBeHidden()
  await expect(page.locator('[data-test="runtime-monitor-mode"]')).toBeVisible()
  await page.locator('[data-node-id="control"]').click()
  await expect(page.locator('[data-test="runtime-command-error"]')).toContainText('只读')
  expect(state.checks).toBe(0)
  expect(state.writes).toBe(0)
})
