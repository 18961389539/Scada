import { test, expect, type Page } from './fixtures'
import type { HmiLayout, HmiMonitor, HmiRegion, HmiWorkstation } from '../src/api/hmiLayouts'

const region = (id: string, row = 1, col = 1, screenId = 10): HmiRegion => ({ id, kind: 'screen', screenId, row, col, rowSpan: 1, colSpan: 1 })
const monitor = (name: string, regions = [region('main')]): HmiMonitor => ({ name, columns: 4, rows: 4, regions })
const station = (name = 'OP-A'): HmiWorkstation => ({ id: 77, name, layoutId: 501, allowControl: false, description: '操作工位', updatedTs: 1, updatedBy: 'admin' })

async function fixture(page: Page, monitors = [monitor('主屏')], binding: { name?: string; id?: string } = {}) {
  await page.addInitScript(binding => {
    localStorage.setItem('scada_token', 'layout-editor-test')
    localStorage.setItem('scada_user', JSON.stringify({ id: 1, userName: 'admin', displayName: '管理员', role: 'Admin', access: { global: ['View', 'Control', 'AckAlarm', 'Configure', 'ManageUsers', 'System'], areas: [] } }))
    localStorage.setItem('scada_locale', 'zh-CN')
    localStorage.removeItem('scada_workstation'); localStorage.removeItem('scada_workstation_id')
    if (binding.name) localStorage.setItem('scada_workstation', binding.name)
    if (binding.id) localStorage.setItem('scada_workstation_id', binding.id)
  }, binding)
  const layout: HmiLayout = { id: 501, name: '测试运行布局', revision: 3, publishedRevision: 3, updatedTs: Date.UTC(2026, 9, 29, 5), updatedBy: 'admin', document: { monitors } }
  const state = { layout, stations: [station()], submitted: [] as HmiLayout[] }
  await page.route('**/api/**', async route => {
    const path = new URL(route.request().url()).pathname, method = route.request().method()
    if (path === '/api/hmi/layouts') return route.fulfill({ json: [state.layout] })
    if (path === '/api/hmi/layouts/501' && method === 'PUT') {
      const body = route.request().postDataJSON()
      state.submitted.push(body)
      Object.assign(state.layout, body, { revision: state.layout.revision + 1 })
      return route.fulfill({ json: state.layout })
    }
    if (path === '/api/hmi/workstations') return route.fulfill({ json: state.stations })
    if (path === '/api/hmi/workstations/77') {
      if (method === 'DELETE') { state.stations = []; return route.fulfill({ status: 204 }) }
      Object.assign(state.stations[0], route.request().postDataJSON())
      return route.fulfill({ json: state.stations[0] })
    }
    if (path === '/api/screens') return route.fulfill({ json: [{ id: 10, name: '已发布画面', publishedRevision: 2 }, { id: 20, name: '未发布草稿', publishedRevision: null }] })
    if (path === '/api/auth/site-info') return route.fulfill({ json: { siteName: '测试项目', demoMode: false } })
    if (path === '/api/license') return route.fulfill({ json: { status: 'Active', blocked: false } })
    if (path === '/api/health') return route.fulfill({ json: { status: 'ok' } })
    if (path === '/api/alarms/summary') return route.fulfill({ json: { unack: 0, acked: 0, rtnUnack: 0, flood: false } })
    return route.fulfill({ json: [] })
  })
  return state
}

async function openEditor(page: Page) {
  await page.goto('/config/layouts')
  await page.locator('[data-test="layouts"]').getByRole('button', { name: '编辑', exact: true }).click()
  await expect(page.locator('[data-test="layout-preview"]')).toBeVisible()
}

test('editor finds an invalid screen on another monitor and only offers published replacements', async ({ page }) => {
  const state = await fixture(page, [monitor('主屏'), monitor('副屏', [region('draft', 1, 1, 20)])])
  await openEditor(page)
  await expect(page.locator('[data-test="layout-save"]')).toBeEnabled()
  await page.locator('[data-test="layout-issue"]').filter({ hasText: 'draft' }).click()
  await expect(page.getByRole('tab', { name: /副屏/ })).toHaveAttribute('aria-selected', 'true')
  await expect(page.locator('[data-test="region-id"]')).toHaveValue('draft')
  await expect(page.locator('[data-test="layout-region"]')).toHaveClass(/is-invalid/)
  await page.locator('[data-test="region-screen"]').click()
  await expect(page.getByRole('option', { name: '未发布草稿', exact: true })).toHaveCount(0)
  await expect(page.getByRole('option', { name: /不可用/ })).toHaveAttribute('aria-disabled', 'true')
  await page.getByRole('option', { name: '已发布画面', exact: true }).click()
  await expect(page.locator('[data-test="layout-issue"]')).toHaveCount(0)
  await page.locator('[data-test="layout-save"]').click()
  await expect(page.locator('[data-test="layout-dialog"]')).toBeHidden()
  expect(state.submitted[0].document.monitors[1].regions[0].screenId).toBe(10)
})

test('editor places new regions in a free cell and groups an entire drag into one undo step', async ({ page }) => {
  const state = await fixture(page)
  await openEditor(page)
  const canvas = page.locator('[data-test="layout-preview"]')
  expect((await canvas.boundingBox())!.width).toBeGreaterThan(700)
  await page.locator('[data-test="layout-add-region-0"]').click()
  await expect(page.locator('[data-test="region-row"] input')).toHaveValue('1')
  await expect(page.locator('[data-test="region-col"] input')).toHaveValue('2')
  const added = page.locator('[data-test="layout-region"][data-region-id="r2"]')
  await added.focus(); await added.press('ArrowDown'); await added.press('Shift+ArrowRight')
  await expect(page.locator('[data-test="region-row"] input')).toHaveValue('2')
  await expect(page.locator('[data-test="region-col-span"] input')).toHaveValue('2')
  await added.press('Control+z')
  await expect(page.locator('[data-test="region-col-span"] input')).toHaveValue('1')
  await page.locator('[data-test="layout-redo"]').click()
  await expect(page.locator('[data-test="region-col-span"] input')).toHaveValue('2')
  const box = (await added.boundingBox())!, grid = (await canvas.boundingBox())!
  await page.mouse.move(box.x + 12, box.y + 12); await page.mouse.down()
  await page.mouse.move(box.x + 12 + (grid.width - 5) / 4, box.y + 12 + (grid.height - 5) / 4, { steps: 12 }); await page.mouse.up()
  await expect(page.locator('[data-test="region-row"] input')).toHaveValue('3')
  await expect(page.locator('[data-test="region-col"] input')).toHaveValue('3')
  await page.locator('[data-test="layout-undo"]').click()
  await expect(page.locator('[data-test="region-row"] input')).toHaveValue('2')
  await expect(page.locator('[data-test="region-col"] input')).toHaveValue('2')
  const restored = (await added.boundingBox())!
  await page.mouse.move(restored.x + 12, restored.y + 12); await page.mouse.down()
  await page.mouse.move(restored.x + 12 + (grid.width - 5) / 4, restored.y + 12 + (grid.height - 5) / 4, { steps: 4 })
  await page.keyboard.press('Control+z'); await page.mouse.up()
  await expect(page.locator('[data-test="region-row"] input')).toHaveValue('2')
  await expect(page.locator('[data-test="region-col"] input')).toHaveValue('2')
  await expect(page.locator('[data-test="region-col-span"] input')).toHaveValue('2')
  await page.locator('[data-test="layout-save"]').click()
  await expect(page.locator('[data-test="layout-dialog"]')).toBeHidden()
  expect(state.submitted[0].document.monitors[0].regions[1]).toMatchObject({ row: 2, col: 2, rowSpan: 1, colSpan: 2 })
})

test('editor enforces monitor and region limits and preserves preview ratio on narrow screens', async ({ page }) => {
  const fullGrid = Array.from({ length: 16 }, (_, i) => region(`r${i + 1}`, Math.floor(i / 4) + 1, i % 4 + 1))
  await fixture(page, [monitor('满屏', fullGrid), ...Array.from({ length: 7 }, (_, i) => monitor(`显示器${i + 2}`))])
  await openEditor(page)
  await page.screenshot({ path: test.info().outputPath('layout-editor-desktop.png'), animations: 'disabled' })
  await expect(page.locator('[data-test="layout-add-monitor"]')).toBeDisabled()
  await expect(page.locator('[data-test="layout-copy-monitor"]').first()).toBeDisabled()
  await expect(page.locator('[data-test="layout-add-region-0"]')).toBeDisabled()
  await page.locator('[data-test="layout-remove-region"]').click()
  await expect(page.locator('[data-test="layout-add-region-0"]')).toBeEnabled()
  await page.locator('[data-test="layout-add-region-0"]').click()
  await expect(page.locator('[data-test="region-row"] input')).toHaveValue('1')
  await expect(page.locator('[data-test="region-col"] input')).toHaveValue('1')
  await expect(page.locator('[data-test="layout-add-region-0"]')).toBeDisabled()
  await page.getByRole('tab', { name: '显示器8', exact: true }).click()
  await expect(page.locator('[data-test="layout-remove-region"]')).toBeDisabled()
  await expect(page.locator('[data-test="region-id"]')).toHaveAttribute('maxlength', '40')
  await expect(page.locator('[data-test="region-title"]')).toHaveAttribute('maxlength', '60')
  await page.setViewportSize({ width: 390, height: 900 })
  await page.locator('[data-test="layout-preview-ratio"]').click()
  await page.getByRole('option', { name: '9:16', exact: true }).click()
  const box = (await page.locator('[data-test="layout-preview-shell"]').boundingBox())!
  expect(box.width / box.height).toBeCloseTo(9 / 16, 1)
  expect(box.x + box.width).toBeLessThanOrEqual(390)
  expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(390)
  await page.screenshot({ path: test.info().outputPath('layout-editor-mobile.png'), animations: 'disabled' })
})

test('workstation binding migrates legacy names, follows rename, and clears on deleting this station', async ({ page }) => {
  const state = await fixture(page, undefined, { name: 'OP-A' })
  await page.goto('/config/layouts')
  await expect(page.locator('[data-test="local-station"]')).toContainText('OP-A')
  await expect.poll(() => page.evaluate(() => localStorage.getItem('scada_workstation_id'))).toBe('77')
  await expect(page.locator('[data-test="layout-usage"]')).toHaveText('1 个工作站')
  const row = page.locator('[data-test="stations"] .el-table__row')
  await expect(row).toContainText('本机')
  await row.getByRole('button', { name: '编辑', exact: true }).click()
  await page.locator('[data-test="station-name"]').fill('OP-RENAMED')
  await page.locator('[data-test="station-save"]').click()
  await expect(page.locator('[data-test="local-station"]')).toContainText('OP-RENAMED')
  expect(await page.evaluate(() => [localStorage.getItem('scada_workstation_id'), localStorage.getItem('scada_workstation')])).toEqual(['77', 'OP-RENAMED'])
  expect(state.stations[0].name).toBe('OP-RENAMED')
  await row.getByRole('button', { name: '删除', exact: true }).click()
  await page.locator('.el-message-box').getByRole('button', { name: '确定', exact: true }).click()
  await expect(page.locator('[data-test="local-station"]')).toContainText('未登记')
  expect(await page.evaluate(() => [localStorage.getItem('scada_workstation_id'), localStorage.getItem('scada_workstation')])).toEqual([null, null])
})

test('missing or malformed stable IDs never bind to a different workstation with the same name', async ({ page }) => {
  await fixture(page, undefined, { name: 'OP-A', id: '999' })
  await page.goto('/config/layouts')
  await expect(page.locator('[data-test="local-station-invalid"]')).toBeVisible()
  expect(await page.evaluate(() => localStorage.getItem('scada_workstation_id'))).toBe('999')
  await expect(page.locator('[data-test="stations"] .el-table__row .el-tag').filter({ hasText: '本机' })).toHaveCount(0)
  await page.evaluate(() => localStorage.setItem('scada_workstation_id', 'bad-id'))
  await page.getByRole('button', { name: '刷新', exact: true }).click()
  await expect(page.locator('[data-test="local-station-invalid"]')).toBeVisible()
  expect(await page.evaluate(() => localStorage.getItem('scada_workstation_id'))).toBe('bad-id')
})

test('legacy banners become a fixed header and only leading banner rows are reclaimed on saving', async ({ page }) => {
  const top = monitor('顶部横幅', [
    { ...region('banner'), kind: 'alarmBanner', colSpan: 4 },
    { ...region('main', 2), rowSpan: 3, colSpan: 4 }
  ])
  const middle = monitor('中部横幅', [
    { ...region('top'), colSpan: 4 },
    { ...region('banner', 2), kind: 'alarmBanner', colSpan: 4 },
    { ...region('bottom', 3), kind: 'alarmList', rowSpan: 2, colSpan: 4 }
  ])
  const state = await fixture(page, [top, middle])
  await openEditor(page)
  await expect(page.locator('[data-test="layout-banner-converted"]')).toContainText('2')
  await expect(page.locator('[data-test="layout-fixed-banner"]')).toBeVisible()
  await expect(page.locator('[data-test="monitor-rows"] input').first()).toHaveValue('3')
  await expect(page.locator('[data-test="layout-region"]')).toHaveCount(1)
  await expect(page.locator('[data-test="region-row"] input')).toHaveValue('1')
  await page.locator('[data-test="region-kind"]').click()
  await expect(page.getByRole('option', { name: '报警横幅', exact: true })).toHaveCount(0)
  await page.keyboard.press('Escape')
  expect(state.submitted).toHaveLength(0)
  expect(state.layout.document.monitors[0].regions).toHaveLength(2)
  await page.getByRole('tab', { name: '中部横幅', exact: true }).click()
  await expect(page.locator('[data-test="layout-region"]')).toHaveCount(2)
  await page.locator('[data-test="layout-pick-region-1"]').click()
  await expect(page.locator('[data-test="region-row"] input')).toHaveValue('3')
  await page.locator('[data-test="layout-save"]').click()
  expect(state.submitted[0].document.monitors[0]).toMatchObject({ rows: 3, regions: [{ id: 'main', row: 1, rowSpan: 3 }] })
  expect(state.submitted[0].document.monitors[1]).toMatchObject({ rows: 4, regions: [{ id: 'top', row: 1 }, { id: 'bottom', row: 3 }] })
  expect(state.submitted[0].document.monitors.flatMap(m => m.regions).some(r => r.kind === 'alarmBanner')).toBe(false)
})

test('runtime preview uses published screens at the target viewport, switches monitors and cannot control', async ({ page }, testInfo) => {
  const state = await fixture(page, [
    { ...monitor('横屏', [{ ...region('main'), rowSpan: 4, colSpan: 4 }]), previewWidth: 1366, previewHeight: 768 },
    { ...monitor('竖屏', [{ ...region('alarms'), kind: 'alarmList', rowSpan: 4, colSpan: 4 }]), previewWidth: 1080, previewHeight: 1920 }
  ])
  const writes: string[] = [], screenRequests: string[] = []
  page.on('request', request => { if (['POST', 'PUT', 'DELETE'].includes(request.method()) && /\/api\/(hmi|commands|screens\/\d+\/actions|alarms\/\d+\/ack)/.test(request.url())) writes.push(request.url()) })
  await page.route('**/api/screens/10?*', route => {
    screenRequests.push(route.request().url())
    return route.fulfill({ json: { id: 10, name: '已发布画面', revision: 2, publishedRevision: 2, document: { schemaVersion: 1, width: 1200, height: 720, background: '#101b2d', nodes: [
      { id: 'preview-command', type: 'button', x: 20, y: 20, width: 240, height: 60, rotation: 0, text: '已发布控制按钮', color: '#7394b8', activeColor: '#34d399', unit: '', decimals: 1, min: 0, max: 100, activeThreshold: .5, tagId: null, action: { type: 'command', tagId: 10, screenId: null, value: 1 } }
    ] } } })
  })
  await openEditor(page)
  await page.locator('[data-test="layout-name"]').fill('尚未保存的布局')
  await page.locator('[data-test="layout-runtime-preview"]').click()
  const frame = page.frameLocator('[data-test="runtime-preview-frame"]')
  await expect(frame.locator('.console-title')).toHaveText('尚未保存的布局')
  await expect(frame.locator('[data-node-id="preview-command"]')).toBeVisible()
  await expect(frame.locator('[data-test="alarm-banner"]')).toBeVisible()
  await expect(frame.locator('[data-test="console"]')).toHaveAttribute('inert', '')
  const control = (await frame.locator('[data-node-id="preview-command"]').boundingBox())!
  await page.mouse.click(control.x + control.width / 2, control.y + control.height / 2)
  expect(await frame.locator('[data-node-id="preview-command"]').evaluate(element => { (element as HTMLElement).focus(); return document.activeElement === element })).toBe(false)
  expect(screenRequests.every(url => new URL(url).searchParams.get('draft') === 'false')).toBe(true)
  expect(await frame.locator('body').evaluate(() => [innerWidth, innerHeight])).toEqual([1366, 768])
  const header = (await frame.locator('[data-test="alarm-banner"]').boundingBox())!
  const content = (await frame.locator('[data-test="console-workspace"]').boundingBox())!
  expect(content.y).toBeGreaterThanOrEqual(header.y + header.height - 1)
  await expect(frame.locator('[data-test="runtime-edit"]')).toHaveCount(0)
  await page.screenshot({ path: testInfo.outputPath('layout-runtime-preview-desktop.png'), animations: 'disabled' })
  await page.locator('[data-test="runtime-preview-monitor"]').click()
  await page.getByRole('option', { name: '竖屏', exact: true }).click()
  await expect(frame.locator('[data-test="alarm-list"]')).toBeVisible()
  expect(await frame.locator('body').evaluate(() => [innerWidth, innerHeight])).toEqual([1080, 1920])
  await page.locator('[data-test="runtime-preview-resolution"]').click()
  await page.getByRole('option', { name: '390×844', exact: true }).click()
  await expect.poll(() => frame.locator('body').evaluate(() => [innerWidth, innerHeight])).toEqual([390, 844])
  await page.setViewportSize({ width: 390, height: 900 })
  await expect.poll(() => page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(390)
  await page.screenshot({ path: testInfo.outputPath('layout-runtime-preview-mobile.png'), animations: 'disabled' })
  expect(writes).toEqual([])
  expect(state.submitted).toEqual([])
  expect(state.layout.document.monitors[1].previewWidth).toBe(1080)
})

test('target dimensions belong to each monitor, survive saving and appear in publication review', async ({ page }) => {
  const state = await fixture(page, [monitor('主屏'), monitor('副屏')])
  await openEditor(page)
  await page.locator('[data-test="monitor-preview-width"] input').first().fill('1280')
  await page.locator('[data-test="monitor-preview-width"] input').first().press('Tab')
  await page.locator('[data-test="monitor-preview-height"] input').first().fill('1024')
  await page.locator('[data-test="monitor-preview-height"] input').first().press('Tab')
  await page.getByRole('tab', { name: '副屏', exact: true }).click()
  await page.locator('[data-test="layout-preview-ratio"]').click()
  await page.getByRole('option', { name: '9:16', exact: true }).click()
  await page.locator('[data-test="layout-save"]').click()
  expect(state.submitted[0].document.monitors[0]).toMatchObject({ previewWidth: 1280, previewHeight: 1024 })
  expect(state.submitted[0].document.monitors[1]).toMatchObject({ previewWidth: 1080, previewHeight: 1920 })
  await page.route('**/api/hmi/layouts/501?draft=true', route => route.fulfill({ json: state.layout }))
  await page.route('**/api/hmi/layouts/501/versions', route => route.fulfill({ json: [] }))
  await page.route('**/api/hmi/layouts/501/versions/3', route => route.fulfill({ json: { ...state.layout, document: { monitors: [monitor('主屏'), monitor('副屏')] } } }))
  await page.locator('[data-test="layout-publish-501"]').click()
  await expect(page.locator('[data-test="layout-version-diff"]')).toContainText('预览宽度')
  const portrait = page.locator('.static-console').nth(1)
  const box = (await portrait.boundingBox())!
  expect(box.width / box.height).toBeCloseTo(9 / 16, 1)
  await page.locator('[data-test="layout-version-runtime-preview"]').click()
  await expect(page.frameLocator('[data-test="runtime-preview-frame"]').locator('.console-title')).toHaveText(state.layout.name)
})

test('a legacy unpublished workstation assignment explains repair and can be cleared', async ({ page }) => {
  const state = await fixture(page)
  state.layout.publishedRevision = null
  await page.goto('/config/layouts')
  await page.locator('[data-test="stations"]').getByRole('button', { name: '编辑', exact: true }).click()
  await expect(page.locator('[data-test="station-unpublished-binding"]')).toBeVisible()
  await page.locator('[data-test="station-save"]').click()
  await expect(page.locator('[data-test="station-save-error"]')).toContainText('未发布')
  await page.locator('[data-test="station-layout"]').hover()
  await page.locator('[data-test="station-layout"] .el-select__clear').click()
  await page.locator('[data-test="station-save"]').click()
  await expect(page.locator('[data-test="station-name"]')).toBeHidden()
  expect(state.stations[0].layoutId).toBeNull()
})
