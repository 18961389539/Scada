import { test, expect, type Page } from './fixtures'
import type { HmiLayout, HmiMonitor, HmiRegion } from '../src/api/hmiLayouts'
import { fitRegionsToGrid } from '../src/components/console/layoutEditing'

const region = (id: string, overrides: Partial<HmiRegion> = {}): HmiRegion => ({ id, kind: 'screen', screenId: 10, title: id, row: 1, col: 1, rowSpan: 2, colSpan: 2, ...overrides })
const monitor = (regions = [region('main')], columns = 7, rows = 5): HmiMonitor => ({ name: '主屏', columns, rows, previewWidth: 1920, previewHeight: 1080, regions })
const geometry = (regions: HmiRegion[]) => regions.map(({ row, col, rowSpan, colSpan }) => ({ row, col, rowSpan, colSpan }))
async function fixture(page: Page, first = monitor(), second = { ...monitor([region('other')]), name: '副屏' }) {
  page.on('dialog', dialog => void dialog.accept())
  await page.addInitScript(() => {
    localStorage.setItem('scada_token', 'layout-geometry-test')
    localStorage.setItem('scada_user', JSON.stringify({ id: 1, userName: 'admin', displayName: '管理员', role: 'Admin', access: { global: ['View', 'Control', 'AckAlarm', 'Configure', 'ManageUsers', 'System'], areas: [] } }))
    localStorage.setItem('scada_locale', 'zh-CN')
  })
  const draft: HmiLayout = { id: 501, name: '几何布局', revision: 7, publishedRevision: 5, publishedName: '运行布局', hasDraftChanges: true, document: { monitors: [first, second] }, updatedTs: 1, updatedBy: 'admin' }
  const state = { draft, saved: [] as HmiLayout[] }
  await page.route('**/api/**', async route => {
    const path = new URL(route.request().url()).pathname, method = route.request().method()
    if (path === '/api/hmi/layouts') return route.fulfill({ json: [state.draft] })
    if (path === '/api/hmi/layouts/501' && method === 'PUT') {
      const body = route.request().postDataJSON(); state.saved.push(body)
      Object.assign(state.draft, body, { revision: state.draft.revision + 1 }); return route.fulfill({ json: state.draft })
    }
    if (path === '/api/hmi/workstations') return route.fulfill({ json: [] })
    if (path === '/api/screens') return route.fulfill({ json: [{ id: 10, name: '已发布画面', publishedRevision: 2 }] })
    if (path === '/api/auth/site-info') return route.fulfill({ json: { siteName: '测试项目', demoMode: false } })
    if (path === '/api/license') return route.fulfill({ json: { status: 'Active', blocked: false } })
    if (path === '/api/health') return route.fulfill({ json: { status: 'ok' } })
    if (path === '/api/alarms/summary') return route.fulfill({ json: { unack: 0, acked: 0, rtnUnack: 0, flood: false } })
    return route.fulfill({ json: [] })
  })
  return state
}
const control = (page: Page, name: string) => page.locator(`[data-test="${name}"]:visible`)
async function openEditor(page: Page) {
  await page.goto('/config/layouts')
  await page.locator('[data-test="layouts"], [data-test="layout-cards"]').getByRole('button', { name: '编辑', exact: true }).first().click()
  await expect(control(page, 'layout-preview-shell')).toBeVisible()
}
async function openPanel(page: Page, name: string) {
  const panel = control(page, name)
  if (await panel.getAttribute('open') == null) await panel.locator(':scope > summary').click()
}
async function gridColumns(page: Page, value: number) {
  const input = control(page, 'monitor-columns').locator('input')
  await input.fill(String(value)); await input.press('Tab')
}
function expectSafe(monitor: HmiMonitor, positions = geometry(monitor.regions)) {
  for (const position of positions) {
    expect(position.row).toBeGreaterThanOrEqual(1); expect(position.col).toBeGreaterThanOrEqual(1)
    expect(position.rowSpan).toBeGreaterThanOrEqual(1); expect(position.colSpan).toBeGreaterThanOrEqual(1)
    expect(position.row + position.rowSpan - 1).toBeLessThanOrEqual(monitor.rows)
    expect(position.col + position.colSpan - 1).toBeLessThanOrEqual(monitor.columns)
  }
  positions.forEach((position, index) => positions.slice(index + 1).forEach(other => {
    expect(position.row < other.row + other.rowSpan && other.row < position.row + position.rowSpan && position.col < other.col + other.colSpan && other.col < position.col + position.colSpan).toBe(false)
  }))
}

test('six alignment actions keep size and bindings, affect only the selected region and support undo', async ({ page }) => {
  const state = await fixture(page, monitor([region('main', { row: 2, col: 3 }), region('other', { row: 5, col: 7, rowSpan: 1, colSpan: 1, kind: 'navigation', screenId: null })]))
  const original = structuredClone(state.draft.document)
  await openEditor(page); await openPanel(page, 'layout-geometry-tools')
  for (const [alignment, row, col] of [['left', 2, 1], ['right', 2, 6], ['center', 2, 3], ['top', 1, 3], ['middle', 2, 3], ['bottom', 4, 3]] as const) {
    await control(page, `layout-align-${alignment}`).click()
    await expect(control(page, 'region-row').locator('input')).toHaveValue(String(row))
    await expect(control(page, 'region-col').locator('input')).toHaveValue(String(col))
  }
  await control(page, 'layout-undo').click()
  await expect(control(page, 'region-row').locator('input')).toHaveValue('2')
  await control(page, 'layout-redo').click()
  await expect(control(page, 'region-row').locator('input')).toHaveValue('4')
  await control(page, 'layout-geometry-tools').scrollIntoViewIfNeeded()
  await page.screenshot({ path: test.info().outputPath('geometry-tools-desktop.png'), animations: 'disabled' })
  await control(page, 'layout-save').click()
  const saved = state.saved[0].document
  expect(saved.monitors[0].regions[0]).toEqual({ ...original.monitors[0].regions[0], row: 4 })
  expect(saved.monitors[0].regions[1]).toEqual(original.monitors[0].regions[1])
  expect(saved.monitors[1]).toEqual(original.monitors[1]); expectSafe(saved.monitors[0])
})

test('alignment refuses collisions and oversized regions instead of introducing overlaps', async ({ page }) => {
  await fixture(page, monitor([region('main', { row: 2, col: 3 }), region('wall', { row: 1, col: 6, rowSpan: 5 })]))
  await openEditor(page); await openPanel(page, 'layout-geometry-tools')
  await expect(control(page, 'layout-align-right')).toBeDisabled()
  await expect(control(page, 'layout-align-right')).toHaveAttribute('title', /重叠/)
  await control(page, 'layout-align-left').click()
  await expect(control(page, 'region-col').locator('input')).toHaveValue('1')
})

for (const axis of ['columns', 'rows'] as const) {
  test(`equal ${axis} fills odd-sized grids, preserves every region and is one undo step`, async ({ page }) => {
    const state = await fixture(page, monitor([region('main'), region('nav', { kind: 'navigation', screenId: null, col: 4 }), region('alarms', { kind: 'alarmList', screenId: null, row: 4, rowSpan: 1, colSpan: 3 })]))
    const original = structuredClone(state.draft.document)
    await openEditor(page); await openPanel(page, 'layout-geometry-tools')
    await control(page, `layout-divide-${axis}`).click()
    await expect(control(page, 'layout-region-issues')).toHaveCount(0)
    await control(page, 'layout-undo').click()
    await expect(control(page, 'region-row-span').locator('input')).toHaveValue('2')
    await expect(control(page, 'layout-undo')).toBeDisabled()
    await control(page, 'layout-redo').click()
    await control(page, 'layout-save').click()
    const saved = state.saved[0].document.monitors[0]
    expect(geometry(saved.regions)).toEqual(axis === 'columns' ? [
      { row: 1, col: 1, rowSpan: 5, colSpan: 3 }, { row: 1, col: 4, rowSpan: 5, colSpan: 2 }, { row: 1, col: 6, rowSpan: 5, colSpan: 2 }
    ] : [{ row: 1, col: 1, rowSpan: 2, colSpan: 7 }, { row: 3, col: 1, rowSpan: 2, colSpan: 7 }, { row: 5, col: 1, rowSpan: 1, colSpan: 7 }])
    saved.regions.forEach((item, index) => expect(item).toEqual({ ...original.monitors[0].regions[index], ...geometry(saved.regions)[index] }))
    expect(state.saved[0].document.monitors[1]).toEqual(original.monitors[1]); expectSafe(saved)
  })
}

test('fit repairs out-of-bounds saved geometry without dropping regions or changing their bindings', async ({ page }) => {
  const state = await fixture(page, monitor([region('left', { colSpan: 4, rowSpan: 4 }), region('right', { col: 5, colSpan: 4, rowSpan: 4, kind: 'alarmList', screenId: null })], 4, 4))
  await openEditor(page); await openPanel(page, 'layout-geometry-tools')
  await control(page, 'layout-fit-grid').click()
  await expect(control(page, 'layout-region-issues')).toHaveCount(0)
  await control(page, 'layout-save').click()
  expect(state.saved[0].document.monitors[0].regions).toMatchObject([{ id: 'left', screenId: 10, col: 1, colSpan: 2, rowSpan: 4 }, { id: 'right', kind: 'alarmList', col: 3, colSpan: 2, rowSpan: 4 }])
  expectSafe(state.saved[0].document.monitors[0])
})

test('impossible arrangements disable fit and division without silently losing regions', async ({ page }) => {
  const state = await fixture(page, monitor(Array.from({ length: 5 }, (_, index) => region(`r${index}`, { rowSpan: 1, colSpan: 1 })), 2, 2))
  const original = structuredClone(state.draft.document.monitors[0])
  await openEditor(page); await openPanel(page, 'layout-geometry-tools')
  for (const name of ['layout-fit-grid', 'layout-divide-columns', 'layout-divide-rows']) await expect(control(page, name)).toBeDisabled()
  await expect(control(page, 'layout-geometry-tools')).toContainText('无法在当前网格中安放全部区域')
  await expect(control(page, 'layout-undo')).toBeDisabled()
  await control(page, 'layout-save').click()
  expect(state.saved[0].document.monitors[0]).toEqual(original)
})

test('shrinking previews affected regions, cancels cleanly and fits in one undoable operation', async ({ page }) => {
  const state = await fixture(page, monitor([region('left', { colSpan: 4, rowSpan: 4 }), region('right', { col: 5, colSpan: 4, rowSpan: 4 })], 8, 4))
  await openEditor(page); await gridColumns(page, 4)
  await expect(control(page, 'grid-resize-affected')).toContainText('right')
  await expect(control(page, 'grid-resize-affected')).not.toContainText('left')
  await expect(control(page, 'grid-resize-preview').getByRole('img')).toHaveCount(1)
  await control(page, 'grid-resize-cancel').click()
  await expect(control(page, 'monitor-columns').locator('input')).toHaveValue('8')
  await expect(control(page, 'layout-undo')).toBeDisabled()
  await gridColumns(page, 4); await control(page, 'grid-resize-fit').click()
  await expect(control(page, 'monitor-columns').locator('input')).toHaveValue('4')
  await expect(control(page, 'layout-region-issues')).toHaveCount(0)
  await control(page, 'layout-undo').click()
  await expect(control(page, 'monitor-columns').locator('input')).toHaveValue('8')
  await expect(control(page, 'region-col-span').locator('input')).toHaveValue('4')
  await expect(control(page, 'layout-undo')).toBeDisabled()
  await control(page, 'layout-redo').click(); await control(page, 'layout-save').click()
  expect(state.saved[0].document.monitors[0]).toMatchObject({ columns: 4, regions: [{ col: 1, colSpan: 2 }, { col: 3, colSpan: 2 }] })
  expectSafe(state.saved[0].document.monitors[0])
})

test('keeping positions preserves unfinished geometry including the selected out-of-bounds region', async ({ page }) => {
  const state = await fixture(page, monitor([region('left', { colSpan: 4, rowSpan: 4 }), region('right', { col: 5, colSpan: 4, rowSpan: 4 })], 8, 4))
  const original = structuredClone(state.draft.document.monitors[0].regions)
  await openEditor(page); await control(page, 'layout-pick-region-1').click()
  await gridColumns(page, 4); await control(page, 'grid-resize-keep').click()
  await expect(control(page, 'region-col').locator('input')).toHaveValue('5')
  await expect(control(page, 'region-col-span').locator('input')).toHaveValue('4')
  await expect(control(page, 'layout-region-issues')).toContainText('right')
  await expect(control(page, 'layout-runtime-preview')).toBeDisabled()
  await control(page, 'layout-undo').click()
  await expect(control(page, 'monitor-columns').locator('input')).toHaveValue('8')
  await expect(control(page, 'layout-undo')).toBeDisabled()
  await control(page, 'layout-redo').click(); await control(page, 'layout-save').click()
  expect(state.saved[0].document.monitors[0].columns).toBe(4)
  expect(state.saved[0].document.monitors[0].regions).toEqual(original)
})

test('grid resize cannot fit more regions than available cells and growing needs no confirmation', async ({ page }) => {
  const state = await fixture(page, monitor(Array.from({ length: 6 }, (_, index) => region(`r${index}`, { row: Math.floor(index / 3) + 1, col: index % 3 + 1, rowSpan: 1, colSpan: 1 })), 3, 2))
  await openEditor(page); await gridColumns(page, 2)
  await expect(control(page, 'grid-resize-fit')).toBeDisabled()
  await control(page, 'grid-resize-cancel').click()
  await gridColumns(page, 6)
  await expect(control(page, 'layout-grid-resize')).toHaveCount(0)
  await control(page, 'layout-save').click()
  expect(state.saved[0].document.monitors[0].regions).toHaveLength(6)
  expect(state.saved[0].document.monitors[0].columns).toBe(6)
})

for (const size of [{ width: 320, height: 640 }, { width: 390, height: 844 }, { width: 768, height: 900 }]) {
  test(`mobile ${size.width} opens with the landscape canvas in view and secondary panels collapsed`, async ({ page }) => {
    await page.setViewportSize(size); await fixture(page); await openEditor(page)
    for (const name of ['layout-settings', 'layout-monitor-settings', 'layout-region-properties']) await expect(control(page, name)).not.toHaveAttribute('open', '')
    const frame = (await control(page, 'layout-preview-shell').boundingBox())!, body = (await page.locator('.layout-editor .el-dialog__body').boundingBox())!
    expect(frame.y).toBeGreaterThanOrEqual(body.y)
    expect(frame.y + frame.height).toBeLessThanOrEqual(body.y + body.height + 1)
    await expect(control(page, 'layout-save')).toBeInViewport({ ratio: 1 })
    expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(size.width)
    await page.screenshot({ path: test.info().outputPath(`canvas-first-${size.width}.png`), animations: 'disabled' })
  })
}

test('portrait canvas stays in the mobile first view and collapsed tools remain usable for editing', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 })
  const state = await fixture(page, { ...monitor(), previewWidth: 1080, previewHeight: 1920 })
  await openEditor(page)
  const frame = (await control(page, 'layout-preview-shell').boundingBox())!, body = (await page.locator('.layout-editor .el-dialog__body').boundingBox())!
  expect(frame.width / frame.height).toBeCloseTo(9 / 16, 1)
  expect(frame.y + frame.height).toBeLessThanOrEqual(body.y + body.height + 1)
  await expect(control(page, 'layout-save')).toBeInViewport({ ratio: 1 })
  await page.screenshot({ path: test.info().outputPath('canvas-first-portrait.png'), animations: 'disabled' })
  await openPanel(page, 'layout-settings'); await expect(control(page, 'layout-template')).toBeVisible()
  await control(page, 'layout-settings').locator(':scope > summary').click()
  await openPanel(page, 'layout-monitor-settings')
  await expect(control(page, 'monitor-preview-width').locator('input')).toHaveValue('1080')
  await control(page, 'layout-monitor-settings').locator(':scope > summary').click()
  await openPanel(page, 'layout-region-properties'); await control(page, 'region-title').fill('手机调整的标题')
  await openPanel(page, 'layout-geometry-tools'); await control(page, 'layout-align-right').click()
  await expect(control(page, 'region-col').locator('input')).toHaveValue('6')
  await control(page, 'layout-save').click()
  expect(state.saved[0].document.monitors[0].regions[0]).toMatchObject({ title: '手机调整的标题', col: 6 })
})

test('mobile issue navigation opens the relevant collapsed properties and focuses the invalid field', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 })
  await fixture(page, monitor(), { ...monitor([region('outside', { row: 7 })]), name: '副屏' })
  await openEditor(page); await openPanel(page, 'layout-region-issues')
  await control(page, 'layout-issue').filter({ hasText: 'outside' }).first().click()
  await expect(page.getByRole('tab', { name: /副屏/ })).toHaveAttribute('aria-selected', 'true')
  await expect(control(page, 'layout-region-properties')).toHaveAttribute('open', '')
  await expect(control(page, 'region-row').locator('input')).toBeFocused()
})

test('fit either keeps all regions safely or reports failure across dense grid sizes', () => {
  for (let columns = 1; columns <= 8; columns++) for (let rows = 1; rows <= 6; rows++) {
    const source = monitor(Array.from({ length: 8 }, (_, index) => region(`r${index}`, { col: index % 4 * 3 + 1, row: Math.floor(index / 4) * 3 + 1, colSpan: 3, rowSpan: 3 })), 12, 6)
    const original = structuredClone(source), fitted = fitRegionsToGrid(source, columns, rows)
    expect(source).toEqual(original)
    if (fitted) { expect(fitted).toHaveLength(8); expectSafe({ ...source, columns, rows }, fitted) }
    if (columns * rows < 8) expect(fitted).toBeNull()
  }
})
