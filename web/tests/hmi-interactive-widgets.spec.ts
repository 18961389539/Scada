import { test, expect, type APIRequestContext, type Page } from './fixtures'
import type { ScreenDocument } from '../src/screens/model'

async function login(request: APIRequestContext) {
  const response = await request.post('/api/auth/login', { data: { userName: 'admin', password: 'admin123' } }); expect(response.ok()).toBeTruthy()
  const auth = await response.json(); return { ...auth, headers: { Authorization: `Bearer ${auth.token}` } }
}

const base = {
  x: 20, y: 20, width: 280, height: 100, rotation: 0, text: '', color: '#7394b8', activeColor: '#34d399', unit: '', decimals: 1,
  min: 0, max: 100, activeThreshold: .5, tagId: null, action: { type: 'none' as const, screenId: null, tagId: null, value: 1 }
}
const names: Record<number, string> = { 900001: 'Alpha 压力', 900002: 'Beta 温度', 900003: 'Gamma 流量' }
const points = (offset: number) => Array.from({ length: 40 }, (_, index) => ({ ts: 1_700_000_000_000 + index * 60000, value: offset + index }))

async function interactiveScreen(page: Page, request: APIRequestContext) {
  const auth = await login(request)
  await page.addInitScript(({ token }) => { localStorage.setItem('scada_token', token); localStorage.setItem('scada_sound', 'off') }, auth)
  const document: ScreenDocument = { schemaVersion: 1, width: 1200, height: 720, background: '#101b2d', nodes: [
    { ...base, id: 'trend', type: 'chart-line', x: 20, y: 20, width: 560, height: 240, tags: [900001, 900002], windowSec: 300 },
    { ...base, id: 'table', type: 'table', x: 20, y: 280, width: 400, height: 180, tags: [900001, 900002, 900003] },
    { ...base, id: 'history', type: 'history-table', x: 440, y: 280, width: 520, height: 180, tags: [900001, 900002] },
    { ...base, id: 'recipe', type: 'recipe', x: 20, y: 480, width: 320, height: 150, text: '配方下发', recipeTemplateId: 900001 },
    { ...base, id: 'seq', type: 'button', x: 360, y: 480, width: 200, height: 56, text: '启动顺控', action: { type: 'sequence', screenId: null, tagId: null, value: 1, sequenceId: 900001 } },
    { ...base, id: 'recipeAction', type: 'button', x: 580, y: 480, width: 200, height: 56, text: '动作配方', action: { type: 'recipe', screenId: null, tagId: null, value: 1, recipeId: 900001 } }
  ] }
  const screen = { id: 900001, name: '交互验收画面', revision: 3, publishedRevision: 3, updatedTs: Date.now(), updatedBy: 'admin', hasDraftChanges: false, document }
  await page.route(url => url.pathname === '/api/screens/900001', route => route.fulfill({ json: screen }))
  await page.route(url => url.pathname === '/api/devices', route => route.fulfill({ json: [{ id: 900001, name: '验收设备', pollIntervalMs: 1000, enabled: true }] }))
  await page.route(url => url.pathname === '/api/tags', route => route.fulfill({ json: Object.keys(names).map((key, index) => ({
    id: Number(key), deviceId: 900001, name: names[Number(key)], unit: index === 0 ? 'MPa' : index === 1 ? '℃' : 'm³/h', dataType: 'Float', writable: true, areaId: null
  })) }))
  await page.route(url => url.pathname === '/api/realtime', async route => route.fulfill({ json: [
    { tagId: 900001, value: 63, quality: 'Good', ts: await page.evaluate(() => Date.now()) },
    { tagId: 900002, value: 21, quality: 'Good', ts: await page.evaluate(() => Date.now()) },
    { tagId: 900003, value: 42, quality: 'Good', ts: await page.evaluate(() => Date.now()) }
  ] }))
  await page.route(url => url.pathname === '/api/history', route => route.fulfill({ json: { series: [
    { tagId: 900001, points: points(0) }, { tagId: 900002, points: points(100) }, { tagId: 900003, points: points(200) }
  ] } }))
  await page.route(url => url.pathname === '/api/recipes/templates', route => route.fulfill({ json: [{
    id: 900001, name: '挤出模板', areaId: 1, description: null, dualApproval: false, revision: 1, updatedTs: Date.now(), updatedBy: 'admin',
    parameters: [{ key: 'temp', name: '温度', tagId: 900002, unit: '℃', min: 0, max: 300, defaultValue: 180 }]
  }] }))
  await page.route(url => url.pathname === '/api/recipes', route => route.fulfill({ json: [
    { id: 900001, templateId: 900001, templateName: '挤出模板', areaId: 1, name: 'PE-1', description: null, version: 2, updatedTs: Date.now(), updatedBy: 'admin', values: [{ key: 'temp', value: 180 }], note: null, source: null }
  ] }))
  await page.route(url => url.pathname === '/api/control/sequences', route => route.fulfill({ json: [
    { id: 900001, name: '启泵顺控', areaId: 1, description: null, dualApproval: false, revision: 1, updatedTs: Date.now(), updatedBy: 'admin', steps: [{ name: '开泵', tagId: null, value: 0, confirmExpression: null, timeoutMs: 0, delayMs: 0 }] }
  ] }))
  await page.route(url => url.pathname === '/api/recipes/900001/check', route => route.fulfill({ json: {
    recipeId: 900001, version: 2, dualApproval: false, ok: true, refusals: [],
    parameters: [{ key: 'temp', name: '温度', tagId: 900002, tagName: 'Beta 温度', unit: '℃', target: 180, current: 21, refusals: [], dualApproval: false }]
  } }))
  let downloads = 0
  await page.route(url => url.pathname === '/api/recipes/900001/download', route => {
    downloads++
    return route.fulfill({ json: {
      id: 77, recipeId: 900001, recipeName: 'PE-1', version: 2, templateId: 900001, areaId: 1, status: 'Completed', statusLabel: '已完成',
      requestedBy: 'admin', approvedBy: null, approvalId: null, createdTs: Date.now(), startedTs: Date.now(), endedTs: Date.now(), written: 1, message: '1 个参数已写入', results: []
    } })
  })
  let starts = 0
  await page.route(url => url.pathname === '/api/control/sequences/900001/start', route => {
    starts++
    return route.fulfill({ json: { pending: false, approvalId: null, runId: 7 } })
  })
  await page.goto('/screens/900001')
  await expect(page.locator('[data-testid="screen-runtime"] [data-node-id="trend"]')).toBeVisible()
  return { document, counts: () => ({ downloads, starts }) }
}

test('trend charts show a crosshair tooltip and open the trend dialog', async ({ page, request }) => {
  await interactiveScreen(page, request)
  const trend = page.locator('[data-test="trend-hit-trend"]')
  await trend.hover({ position: { x: 260, y: 120 } })
  const tooltip = trend.locator('g[pointer-events="none"]')
  await expect(tooltip).toBeVisible()
  await expect(tooltip).toContainText('Alpha 压力')
  await expect(tooltip).toContainText('Beta 温度')
  await expect(tooltip).toContainText('MPa')

  await trend.click({ position: { x: 260, y: 120 } })
  const dialog = page.locator('[data-test="screen-trend-dialog"]')
  await expect(dialog).toBeVisible()
  await expect(dialog).toContainText('趋势')
  await expect(dialog.locator('[data-test="trend-chart"] canvas')).toHaveCount(1)
  await expect(dialog.locator('[data-test="trend-latest"]')).toContainText('Alpha 压力')
  await expect(dialog.locator('[data-test="trend-latest"]')).toContainText('Beta 温度')
  await dialog.locator('.el-dialog__headerbtn').click()
  await expect(dialog).toHaveCount(0)
})

test('tables sort by header, select rows and drill history rows into the trend dialog', async ({ page, request }) => {
  await interactiveScreen(page, request)
  const table = page.locator('[data-node-id="table"]')
  const rows = table.locator('.table-row')
  await expect(rows).toHaveCount(3)
  await expect(rows.first()).toContainText('Alpha 压力')
  await table.locator('.table-sort').first().click()
  await expect(rows.first()).toContainText('Alpha 压力')
  await table.locator('.table-sort').first().click()
  await expect(rows.first()).toContainText('Gamma 流量')
  await table.locator('.table-sort').nth(1).click()
  await expect(rows.first()).toContainText('Beta 温度')

  await rows.first().click()
  await expect(rows.first()).toHaveClass(/selected/)
  await rows.first().click()
  await expect(rows.first()).not.toHaveClass(/selected/)

  const history = page.locator('[data-node-id="history"]')
  const historyRows = history.locator('.table-row')
  await historyRows.nth(1).dblclick()
  const dialog = page.locator('[data-test="screen-trend-dialog"]')
  await expect(dialog).toBeVisible()
  await expect(dialog).toContainText('Beta 温度')
  await expect(dialog.locator('[data-test="trend-latest"]')).toContainText('Beta 温度')
  await dialog.locator('.el-dialog__headerbtn').click()
  await expect(dialog).toHaveCount(0)
})

test('recipe control checks then downloads the selected recipe', async ({ page, request }) => {
  const state = await interactiveScreen(page, request)
  const recipe = page.locator('[data-node-id="recipe"]')
  await expect(recipe.locator('[data-test="recipe-download"]')).toBeDisabled()
  await recipe.locator('[data-test="recipe-select"]').selectOption({ label: 'PE-1 · v2' })
  await recipe.locator('[data-test="recipe-download"]').click()

  const dialog = page.locator('[data-test="recipe-download-dialog"]')
  await expect(dialog).toBeVisible()
  await expect(dialog.locator('[data-test="recipe-check"]')).toContainText('Beta 温度')
  await expect(dialog.locator('[data-test="recipe-check"]')).toContainText('180')
  await dialog.locator('[data-test="recipe-confirm"]').click()
  await expect(dialog.locator('[data-test="recipe-result"]')).toContainText('1 个参数已写入')
  expect(state.counts().downloads).toBe(1)
})

test('sequence and recipe click actions run through their confirmation flows', async ({ page, request }) => {
  test.setTimeout(60000)
  const state = await interactiveScreen(page, request)

  await page.locator('[data-node-id="seq"]').click()
  const confirm = page.locator('.el-message-box')
  await expect(confirm).toContainText('启泵顺控')
  await confirm.getByRole('button', { name: '确定', exact: true }).click()
  await expect(page.getByText('顺控已启动（运行 #7）')).toBeVisible()
  await expect.poll(() => state.counts().starts).toBe(1)

  await page.locator('[data-node-id="recipeAction"]').click()
  const dialog = page.locator('[data-test="recipe-download-dialog"]')
  await expect(dialog).toBeVisible()
  await expect(dialog.locator('[data-test="recipe-check"]')).toContainText('Beta 温度')
  await dialog.locator('[data-test="recipe-confirm"]').click()
  await expect(dialog.locator('[data-test="recipe-result"]')).toBeVisible()
  expect(state.counts().downloads).toBe(1)
})

test('designer exposes recipe template and sequence/recipe action targets', async ({ page, request }) => {
  const auth = await login(request), u = `${Date.now()}`.slice(-6)
  await page.addInitScript(({ token, user }) => { localStorage.setItem('scada_token', token); localStorage.setItem('scada_user', JSON.stringify(user)) }, auth)
  const device = await (await request.post('/api/devices', { headers: auth.headers, data: { name: `动作设备-${u}`, protocol: 'ModbusTcp', host: '127.0.0.1', port: 1502, stationNumber: 1, byteOrder: 'ABCD', pollIntervalMs: 1000, timeoutMs: 1000, enabled: false } })).json()
  const tag = await (await request.post('/api/tags', { headers: auth.headers, data: { deviceId: device.id, name: `动作测点-${u}`, area: 'HoldingRegister', address: 0, dataType: 'Float', scale: 1, offset: 0, writable: true, archiveMode: 'None', deadband: 0, archivePeriodSec: 60, minValue: 0, maxValue: 300 } })).json()
  const template = await (await request.post('/api/recipes/templates', { headers: auth.headers, data: { name: `设计器模板-${u}`, areaId: 1, dualApproval: false, parameters: [{ key: 'temp', name: '温度', tagId: tag.id, unit: '℃', min: 0, max: 300 }] } })).json()
  expect(template.id).toBeTruthy()
  const recipe = await (await request.post('/api/recipes', { headers: auth.headers, data: { templateId: template.id, name: `设计器配方-${u}`, values: [{ key: 'temp', value: 180 }], note: null } })).json()
  expect(recipe.recipe.id).toBeTruthy()
  const sequence = await (await request.post('/api/control/sequences', { headers: auth.headers, data: { name: `设计器顺控-${u}`, areaId: 1, dualApproval: false, description: null, steps: [{ name: '等待', type: 'wait', tagId: null, value: 0, expression: '1 == 1', confirmExpression: null, timeoutMs: 5000, delayMs: 0 }] } }))
  expect(sequence.ok(), await sequence.text()).toBeTruthy()
  const screenName = `动作画面-${u}`
  const screen = await (await request.post('/api/screens', { headers: auth.headers, data: { name: screenName } })).json()
  const document: ScreenDocument = { schemaVersion: 1, width: 1200, height: 720, background: '#101b2d', nodes: [
    { ...base, id: 'recipe', type: 'recipe', x: 40, y: 40, width: 320, height: 150, text: '配方', recipeTemplateId: template.id },
    { ...base, id: 'button', type: 'button', x: 400, y: 40, width: 200, height: 56, text: '动作按钮' }
  ] }
  const saved = await request.put(`/api/screens/${screen.id}`, { headers: auth.headers, data: { name: screenName, revision: screen.revision, document } })
  expect(saved.ok(), await saved.text()).toBeTruthy()
  await page.goto(`/config/screens/${screen.id}`)

  await page.locator('[data-layer-id="recipe"]').click()
  const templateSelect = page.locator('[data-test="node-recipe-template"]')
  await expect(templateSelect).toBeVisible()
  await templateSelect.click()
  await page.getByRole('option', { name: `设计器模板-${u}`, exact: true }).click()
  await expect(templateSelect).toContainText(`设计器模板-${u}`)

  await page.locator('[data-layer-id="button"]').click()
  await page.locator('[data-field="action"] .el-select').first().click()
  await page.getByRole('option', { name: '顺控流程', exact: true }).click()
  const sequenceSelect = page.locator('[data-test="node-action-sequence"]')
  await expect(sequenceSelect).toBeVisible()
  await sequenceSelect.click()
  await page.getByRole('option', { name: new RegExp(`设计器顺控-${u}`) }).click()
  await expect(sequenceSelect).toContainText(`设计器顺控-${u}`)

  await page.locator('[data-field="action"] .el-select').first().click()
  await page.getByRole('option', { name: '配方下发', exact: true }).click()
  const recipeSelect = page.locator('[data-test="node-action-recipe"]')
  await expect(recipeSelect).toBeVisible()
  await recipeSelect.click()
  await page.getByRole('option', { name: new RegExp(`设计器配方-${u}`) }).click()
  await expect(recipeSelect).toContainText(`设计器配方-${u}`)

  await page.getByRole('button', { name: '保存草稿', exact: true }).click()
  await expect(page.getByText('草稿已保存', { exact: true })).toBeVisible()
  const stored = await (await request.get(`/api/screens/${screen.id}?draft=true`, { headers: auth.headers })).json()
  const button = stored.document.nodes.find((node: { id: string }) => node.id === 'button')
  expect(button.action.type).toBe('recipe')
  expect(button.action.recipeId).toBe(recipe.recipe.id)
  expect(stored.document.nodes.find((node: { id: string }) => node.id === 'recipe').recipeTemplateId).toBe(template.id)
})
