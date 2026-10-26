import { test, expect, type APIRequestContext, type Page } from '@playwright/test'
import type { LibraryItem, ScreenDocument, ScreenNode } from '../src/screens/model'

const node = (id: string, text: string, x: number, y: number, type: ScreenNode['type'] = 'pump'): ScreenNode => ({
  id, type, x, y, text, width: 140, height: 112, rotation: 0, color: '#7394b8', activeColor: '#34d399',
  unit: '', decimals: 1, min: 0, max: 100, activeThreshold: .5, tagId: null,
  action: { type: 'none', tagId: null, screenId: null, value: 1 }
})
const drawing = (): ScreenDocument => ({ schemaVersion: 1, width: 3600, height: 2400, background: '#101b2d', nodes: [
  node('near-pump', '近端泵 A', 80, 80),
  { ...node('remote-pump', '远端泵 B', 3240, 2080), texts: { 'en-US': 'Remote pump B' } },
  node('standby-tank', '备用罐', 500, 300, 'tank')
] })
const library: LibraryItem[] = [
  { id: 991, key: 'custom-pump', name: '自定义泵 A', category: 'Factory', kind: 'symbol', svg: '<svg viewBox="0 0 100 100"><circle cx="50" cy="50" r="40" /></svg>', width: 100, height: 100, revision: 1, updatedTs: 0, updatedBy: 'admin', placeholders: [], document: null },
  { id: 992, key: 'station-face', name: '泵站面板', category: 'PlantA', kind: 'template', svg: null, width: 200, height: 150, revision: 1, updatedTs: 0, updatedBy: 'admin', placeholders: ['run'], document: { ...drawing(), width: 320, height: 200, nodes: [node('inner-pump', '内部泵', 10, 10)] } }
]

async function openDesigner(page: Page, request: APIRequestContext, document = drawing()) {
  const response = await request.post('/api/auth/login', { data: { userName: 'admin', password: 'admin123' } })
  expect(response.ok()).toBeTruthy()
  const auth = await response.json()
  const created = await request.post('/api/screens', { headers: { Authorization: `Bearer ${auth.token}` }, data: { name: '画布易用性验收' } })
  expect(created.status()).toBe(201)
  const screen = await created.json()
  await page.addInitScript(({ token, user }) => { localStorage.setItem('scada_token', token); localStorage.setItem('scada_user', JSON.stringify(user)) }, auth)
  await page.route(url => url.pathname === `/api/screens/${screen.id}` && url.searchParams.get('draft') === 'true', route => route.fulfill({ json: {
    ...screen, name: '画布易用性验收', publishedRevision: null, updatedTs: 0, updatedBy: 'admin', hasDraftChanges: false, document
  } }))
  await page.route(url => url.pathname === '/api/hmi/library', route => route.request().method() === 'GET' ? route.fulfill({ json: library }) : route.continue())
  await page.route(url => url.pathname === '/api/tags', route => route.fulfill({ json: [{ id: 771, name: '出口压力', deviceId: null, unit: 'MPa' }] }))
  await page.goto(`/config/screens/${screen.id}`)
  await expect(page.locator('[data-node-id="near-pump"]')).toBeVisible()
  await expect(page.getByRole('button', { name: '适应画布', exact: true })).toBeEnabled()
  return screen
}

async function zoomTo(page: Page, label: string) {
  await page.locator('.canvas-toolbar .el-select__wrapper').click()
  await page.getByRole('option', { name: label, exact: true }).click()
}

async function expectNodeInsideStage(page: Page, id: string) {
  await expect.poll(async () => {
    const stage = (await page.getByTestId('designer-stage').boundingBox())!
    const box = (await page.locator(`[data-node-id="${id}"]`).boundingBox())!
    return box.x >= stage.x && box.y >= stage.y && box.x + box.width <= stage.x + stage.width && box.y + box.height <= stage.y + stage.height
  }).toBe(true)
}

test('component search covers basic shapes, bilingual symbols, user symbols and template keys', async ({ page, request }) => {
  await openDesigner(page, request)
  const search = page.getByRole('searchbox', { name: '搜索组件', exact: true })
  await search.fill('PUMP')
  await expect(page.locator('.palette [data-palette]')).toHaveCount(1)
  await expect(page.locator('.palette [data-symbol="isa:pump"]')).toBeVisible()
  await expect(page.locator('.palette [data-symbol="user:custom-pump"]')).toBeVisible()
  await search.fill('positive pump')
  await expect(page.locator('.palette [data-symbol="isa:pump-positive"]')).toBeVisible()
  await expect(page.locator('.palette [data-symbol]')).toHaveCount(1)
  await search.fill('FACTORY')
  await page.locator('.palette [data-symbol="user:custom-pump"]').click()
  await expect(page.locator('.screen-canvas [data-node-type="symbol"]')).toHaveCount(1)
  await search.fill('station FACE')
  await expect(page.locator('[data-template="station-face"]')).toBeVisible()
  await search.fill('not-a-component')
  await expect(page.getByText('没有匹配的组件，请尝试其他名称或编号。')).toBeVisible()
  await expect(page.locator('.palette [data-palette], .palette [data-symbol], .palette [data-template]')).toHaveCount(0)
  await search.fill('')
  await expect(page.locator('.palette [data-palette]')).toHaveCount(12)
  await expect(page.locator('[data-template="station-face"]')).toBeVisible()
})

test('layer search finds text, translation, ID, type and bound tags without changing the selection', async ({ page, request }) => {
  const document = drawing(); document.nodes[1].tagId = 771
  await openDesigner(page, request, document)
  await zoomTo(page, '100%')
  const search = page.getByRole('searchbox', { name: '搜索图层', exact: true })
  const layers = page.locator('[data-test="designer-layers"]')
  await search.fill('REMOTE pump')
  await expect(layers.locator('button')).toHaveCount(1)
  const before = await page.evaluate(() => window.scrollY)
  await layers.locator('[data-layer-id="remote-pump"]').click()
  await expectNodeInsideStage(page, 'remote-pump')
  expect(await page.evaluate(() => window.scrollY)).toBe(before)
  await expect(page.getByRole('textbox', { name: '图元文字', exact: true })).toHaveValue('远端泵 B')
  await search.fill('出口压力'); await expect(layers.locator('button')).toHaveCount(1)
  await search.fill('771'); await expect(layers.locator('button')).toHaveCount(1)
  await search.fill('TANK'); await expect(layers.locator('[data-layer-id="standby-tank"]')).toBeVisible()
  await search.fill('near-pump'); await expect(layers.locator('[data-layer-id="near-pump"]')).toBeVisible()
  await search.fill('missing')
  await expect(page.getByText('没有匹配的图层，请尝试文字、类型或测点。')).toBeVisible()
  await expect(page.getByRole('textbox', { name: '图元文字', exact: true })).toHaveValue('远端泵 B')
  await search.fill('')
  await expect(layers.locator('[data-layer-id="remote-pump"]')).toHaveAttribute('aria-pressed', 'true')
})

test('fit canvas fits both dimensions, reports the actual zoom and does not create edits', async ({ page, request }) => {
  await openDesigner(page, request)
  await zoomTo(page, '150%')
  await page.getByRole('button', { name: '适应画布', exact: true }).click()
  const dimensions = await page.getByTestId('designer-stage').evaluate(stage => {
    const svg = stage.querySelector('.screen-canvas')!.getBoundingClientRect()
    return { width: svg.width, height: svg.height, viewportWidth: stage.clientWidth, viewportHeight: stage.clientHeight, x: stage.scrollLeft, y: stage.scrollTop }
  })
  expect(dimensions.width).toBeLessThanOrEqual(dimensions.viewportWidth - 40)
  expect(dimensions.height).toBeLessThanOrEqual(dimensions.viewportHeight - 40)
  expect(dimensions.x).toBe(0); expect(dimensions.y).toBe(0)
  await expect(page.locator('.canvas-toolbar .el-select')).toContainText(`${Math.round(dimensions.width / 3600 * 100)}%`)
  await expect(page.getByRole('button', { name: '撤销', exact: true })).toBeDisabled()
  await expect(page.locator('.designer-toolbar .el-tag')).toHaveText('已保存')
  await page.screenshot({ path: test.info().outputPath('designer-fit.png'), animations: 'disabled' })
})

test('zoom preserves the viewport center and locate selection restores offscreen elements', async ({ page, request }) => {
  await openDesigner(page, request)
  await zoomTo(page, '100%')
  await page.getByTestId('designer-stage').evaluate(stage => { stage.scrollLeft = 1200; stage.scrollTop = 800 })
  const center = () => page.getByTestId('designer-stage').evaluate(stage => ({ x: stage.scrollLeft + stage.clientWidth / 2 - 20, y: stage.scrollTop + stage.clientHeight / 2 - 20 }))
  const before = await center()
  await zoomTo(page, '150%')
  const after = await center()
  expect(after.x / 1.5).toBeCloseTo(before.x, 0); expect(after.y / 1.5).toBeCloseTo(before.y, 0)
  await page.locator('[data-layer-id="remote-pump"]').click()
  await page.getByTestId('designer-stage').evaluate(stage => { stage.scrollLeft = 0; stage.scrollTop = 0 })
  await page.getByRole('button', { name: '定位选中', exact: true }).click()
  await expectNodeInsideStage(page, 'remote-pump')
})

test('locating a distant multiselection fits the entire group without altering its positions', async ({ page, request }) => {
  await openDesigner(page, request)
  await zoomTo(page, '100%')
  const near = page.locator('[data-node-id="near-pump"]'), remote = page.locator('[data-node-id="remote-pump"]')
  const positions = [await near.getAttribute('transform'), await remote.getAttribute('transform')]
  await page.locator('[data-layer-id="near-pump"]').click()
  await page.locator('[data-layer-id="remote-pump"]').click({ modifiers: ['Shift'] })
  await expectNodeInsideStage(page, 'near-pump'); await expectNodeInsideStage(page, 'remote-pump')
  expect([await near.getAttribute('transform'), await remote.getAttribute('transform')]).toEqual(positions)
  await expect(page.locator('[data-test="designer-layers"] button[aria-pressed="true"]')).toHaveCount(2)
  await expect(page.getByRole('button', { name: '撤销', exact: true })).toBeDisabled()
})

test('validation links reveal nodes and resolve expanded template children to their editable instance', async ({ page, request }) => {
  const document = drawing()
  document.nodes.push({ ...node('station-instance', '远端面板', 2900, 1800, 'template'), templateKey: 'station-face', bindings: [{ key: 'run', tagId: null }] })
  await openDesigner(page, request, document)
  await page.route('**/api/screens/validate?*', route => route.fulfill({ json: { issues: [
    { nodeId: 'remote-pump', message: '请检查输入测点' },
    { nodeId: 'station-instance--inner-pump', message: '请检查模板绑定' }
  ] } }))
  await zoomTo(page, '100%')
  await page.getByRole('button', { name: '校验', exact: true }).click()
  await page.locator('[data-test="designer-issues"]').getByRole('button', { name: '远端泵 B', exact: true }).click()
  await expectNodeInsideStage(page, 'remote-pump')
  await page.getByRole('button', { name: '校验', exact: true }).click()
  await page.locator('[data-test="designer-issues"]').getByRole('button', { name: 'station-instance--inner-pump', exact: true }).click()
  await expectNodeInsideStage(page, 'station-instance')
  await expect(page.locator('[data-layer-id="station-instance"]')).toHaveAttribute('aria-pressed', 'true')
  await expect(page.getByRole('textbox', { name: '图元文字', exact: true })).toHaveValue('远端面板')
})

test('selection shortcuts respect text editing and composition, and can clear or undo deletion', async ({ page, request }) => {
  await openDesigner(page, request)
  const stage = page.getByTestId('designer-stage')
  await stage.focus(); await page.keyboard.press('Control+a')
  await expect(page.locator('[data-test="designer-layers"] button[aria-pressed="true"]')).toHaveCount(3)
  await page.keyboard.press('Escape')
  await expect(page.locator('[data-test="designer-layers"] button[aria-pressed="true"]')).toHaveCount(0)
  await page.locator('[data-layer-id="near-pump"]').click()
  const text = page.getByRole('textbox', { name: '图元文字', exact: true })
  await text.focus(); await page.keyboard.press('Control+a')
  await expect(page.locator('[data-test="designer-layers"] button[aria-pressed="true"]')).toHaveCount(1)
  await stage.focus()
  await stage.dispatchEvent('keydown', { key: 'Delete', isComposing: true })
  await expect(page.locator('.screen-canvas [data-node-type]')).toHaveCount(3)
  await page.keyboard.press('Control+a'); await page.keyboard.press('Delete')
  await expect(page.getByText('从组件库添加图元后，可在此选择和定位。')).toBeVisible()
  await page.keyboard.press('Control+z')
  await expect(page.locator('.screen-canvas [data-node-type]')).toHaveCount(3)
})

test('save shortcuts ignore template dialogs, global search and IME composition', async ({ page, request }) => {
  const screen = await openDesigner(page, request)
  let saves = 0
  await page.route(url => url.pathname === `/api/screens/${screen.id}` && !url.search, route => {
    if (route.request().method() === 'PUT') { saves++; return route.fulfill({ json: { revision: 2 } }) }
    return route.continue()
  })
  await page.locator('[data-layer-id="near-pump"]').click()
  await page.getByRole('button', { name: '存为模板', exact: true }).click()
  const templateKey = page.locator('[data-test="template-key"]')
  await templateKey.fill('new-template'); await templateKey.press('Control+s')
  expect(saves).toBe(0)
  await page.locator('[data-test="template-dialog"]').getByRole('button', { name: '取消', exact: true }).click()
  await page.keyboard.press('Control+k')
  const globalSearch = page.locator('[data-test="command-palette-input"]')
  await expect(globalSearch).toBeVisible(); await globalSearch.press('Control+s')
  expect(saves).toBe(0)
  await page.keyboard.press('Escape')
  const name = page.getByRole('textbox', { name: '画面名称', exact: true })
  await name.dispatchEvent('keydown', { key: 's', ctrlKey: true, isComposing: true })
  expect(saves).toBe(0)
  await name.fill('保存快捷键验收'); await name.press('Control+s')
  await expect(page.getByText('草稿已保存', { exact: true })).toBeVisible()
  expect(saves).toBe(1)
})

test('embedded panel editing keeps component search, fit and keyboard saving working together', async ({ page, request }) => {
  const auth = await (await request.post('/api/auth/login', { data: { userName: 'admin', password: 'admin123' } })).json()
  const headers = { Authorization: `Bearer ${auth.token}` }
  const document = await (await request.get('/api/object-templates/examples/pump', { headers })).json()
  const created = await request.post('/api/object-templates', { headers, data: { name: '嵌入面板易用性', revision: 0, document } })
  expect(created.status()).toBe(201)
  const template = await created.json()
  await page.addInitScript(({ token, user }) => { localStorage.setItem('scada_token', token); localStorage.setItem('scada_user', JSON.stringify(user)) }, auth)
  await page.goto(`/config/object-templates/${template.id}`)
  await page.getByRole('tab', { name: '面板模板', exact: true }).click()
  const editor = page.locator('.designer.embedded')
  await expect(editor).toBeVisible()
  await editor.getByRole('searchbox', { name: '搜索组件', exact: true }).fill('TANK')
  await editor.locator('[data-palette="tank"]').click()
  await editor.getByRole('button', { name: '适应画布', exact: true }).click()
  await editor.getByTestId('designer-stage').focus(); await page.keyboard.press('Control+s')
  await expect(page.getByText('模板草稿已保存', { exact: true })).toBeVisible()
  const saved = await (await request.get(`/api/object-templates/${template.id}`, { headers })).json()
  expect(saved.document.panel.nodes).toHaveLength(document.panel.nodes.length + 1)
  expect(saved.document.panel.nodes.at(-1).type).toBe('tank')
})
