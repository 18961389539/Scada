import { test, expect, type APIRequestContext } from './fixtures'
import type { ScreenDocument } from '../src/screens/model'

async function login(request: APIRequestContext, userName = 'admin', password = 'admin123') {
  const response = await request.post('/api/auth/login', { data: { userName, password } })
  expect(response.ok()).toBeTruthy()
  const result = await response.json()
  return { ...result, headers: { Authorization: `Bearer ${result.token}` } }
}
const doc = (text = '泵站'): ScreenDocument => ({ schemaVersion: 1, width: 1200, height: 720, background: '#101b2d', nodes: [{
  id: 'pump-1', type: 'pump', x: 60, y: 60, width: 140, height: 112, rotation: 0, text, color: '#7394b8', activeColor: '#34d399',
  unit: '', decimals: 1, min: 0, max: 100, activeThreshold: .5, tagId: null, action: { type: 'none', tagId: null, screenId: null, value: 1 }
}] })
async function create(request: APIRequestContext, headers: Record<string, string>, name = '验收画面') {
  const response = await request.post('/api/screens', { headers, data: { name } }); expect(response.status()).toBe(201); return response.json()
}

async function createHmiScreen(request: APIRequestContext, headers: Record<string, string>) {
  const suffix = Date.now()
  const device = await (await request.post('/api/devices', { headers, data: { name: `hmi-${suffix}`, protocol: 'ModbusTcp', host: '127.0.0.1', port: 1502, stationNumber: 1, byteOrder: 'ABCD', pollIntervalMs: 1000, timeoutMs: 1000, enabled: false } })).json()
  const tagResponse = await request.post('/api/tags', { headers, data: { deviceId: device.id, name: `hmi-tag-${suffix}`, area: 'HoldingRegister', address: 0, dataType: 'Float', scale: 1, offset: 0, writable: true, archiveMode: 'None', deadband: 0, archivePeriodSec: 60, minValue: 0, maxValue: 50 } })
  expect(tagResponse.ok()).toBeTruthy()
  const tag = await tagResponse.json(), screen = await create(request, headers, 'HMI equipment')
  const document = doc('Pump A'), pump = document.nodes[0]
  Object.assign(pump, { x: 40, y: 40, tagId: tag.id, states: [{ value: 0, label: 'Stopped', color: '#7394b8' }, { value: 1, label: 'Running', color: '#34d399' }, { value: 2, label: 'Moving', color: '#fbbf24' }, { value: 3, label: 'Fault', color: '#ef4444' }] })
  document.nodes.push({ ...structuredClone(pump), id: 'valve', type: 'valve', text: 'Valve B', x: 400, y: 180 },
    { ...structuredClone(pump), id: 'pipe', type: 'polyline', text: 'Process pipe', tagId: null, states: null, x: 180, y: 96, width: 220, height: 140, points: [{ x: 0, y: 0 }, { x: 50, y: 0 }, { x: 50, y: 100 }, { x: 100, y: 100 }] },
    { ...structuredClone(pump), id: 'panel', type: 'equipment-panel', text: 'Pump operations', x: 700, y: 40, width: 320, height: 280, commands: [{ key: 'start', label: 'Start pump', tagId: tag.id, value: 1 }, { key: 'stop', label: 'Stop pump', tagId: tag.id, value: 0 }] },
    { ...structuredClone(pump), id: 'input', type: 'input', text: 'Frequency setpoint', states: null, x: 40, y: 300, width: 260, height: 90, min: 0, max: 50, unit: 'Hz', action: { type: 'command', tagId: tag.id, screenId: null, value: 20 } })
  const saved = await request.put(`/api/screens/${screen.id}`, { headers, data: { name: 'HMI equipment', revision: screen.revision, document } }); expect(saved.ok()).toBeTruthy()
  const published = await request.post(`/api/screens/${screen.id}/publish`, { headers, data: { revision: (await saved.json()).revision } }); expect(published.ok()).toBeTruthy()
  return { screen, tag, document }
}

test('HMI editor preserves states and editable connected pipes', async ({ page, request }) => {
  const auth = await login(request), { screen } = await createHmiScreen(request, auth.headers)
  await page.addInitScript(({ token, user }) => { localStorage.setItem('scada_token', token); localStorage.setItem('scada_user', JSON.stringify(user)) }, auth)
  await page.goto(`/config/screens/${screen.id}`)
  for (const type of ['polyline', 'equipment-panel', 'input']) await expect(page.locator(`[data-palette="${type}"]`)).toBeVisible()
  const pipe = page.locator('[data-node-id="pipe"]')
  await pipe.click({ position: { x: 50, y: 30 } })
  const before = await pipe.locator('polyline').getAttribute('points'), bounds = (await page.locator('[data-waypoint="1"]').boundingBox())!
  await page.mouse.move(bounds.x + bounds.width / 2, bounds.y + bounds.height / 2); await page.mouse.down()
  await page.mouse.move(bounds.x + bounds.width / 2, bounds.y + bounds.height / 2 + 25, { steps: 5 }); await page.mouse.up()
  expect(await pipe.locator('polyline').getAttribute('points')).not.toBe(before)
  await page.getByRole('button', { name: '撤销', exact: true }).click(); await expect(pipe.locator('polyline')).toHaveAttribute('points', before!)
  await pipe.click({ position: { x: 50, y: 30 } })
  await page.locator('[data-test="pipe-source"]').click(); await page.getByRole('listbox', { name: '起点设备', exact: true }).getByRole('option', { name: 'Pump A', exact: true }).click()
  await page.locator('[data-test="pipe-target"]').click(); await page.getByRole('listbox', { name: '终点设备', exact: true }).getByRole('option', { name: 'Valve B', exact: true }).click()
  const connected = `${await pipe.getAttribute('transform')}|${await pipe.locator('polyline').getAttribute('points')}`
  const pump = page.locator('[data-node-id="pump-1"]'), pumpBox = (await pump.boundingBox())!
  await page.mouse.move(pumpBox.x + 30, pumpBox.y + 30); await page.mouse.down(); await page.mouse.move(pumpBox.x + 60, pumpBox.y + 45, { steps: 5 }); await page.mouse.up()
  expect(`${await pipe.getAttribute('transform')}|${await pipe.locator('polyline').getAttribute('points')}`).not.toBe(connected)
  await pump.click(); await expect(page.locator('[data-test="node-state-row"]')).toHaveCount(4)
  await page.locator('[data-test="node-state-row"]').nth(2).getByRole('textbox', { name: '状态名称', exact: true }).fill('Maintenance'); await page.locator('[data-test="node-state-row"]').nth(2).getByRole('textbox', { name: '状态名称', exact: true }).blur()
  await page.getByRole('button', { name: '保存草稿', exact: true }).click(); await expect(page.getByText('草稿已保存', { exact: true })).toBeVisible()
  const saved = await (await request.get(`/api/screens/${screen.id}?draft=true`, { headers: auth.headers })).json()
  expect(saved.document.nodes.find((node: { id: string }) => node.id === 'pipe').source.nodeId).toBe('pump-1')
  expect(saved.document.nodes.find((node: { id: string }) => node.id === 'pump-1').states[2].label).toBe('Maintenance')
  await page.reload(); await expect(page.locator('[data-node-id="panel"] button')).toHaveCount(2)
})

test('HMI runtime maps states and confirms numeric and panel commands', async ({ page, request }) => {
  const auth = await login(request), { screen, tag } = await createHmiScreen(request, auth.headers)
  await page.addInitScript(({ token, user }) => { localStorage.setItem('scada_token', token); localStorage.setItem('scada_user', JSON.stringify(user)) }, auth)
  let sample = { value: 2, quality: 'Good' }
  await page.route(url => url.pathname === '/api/realtime', route => route.fulfill({ json: [{ tagId: tag.id, ...sample, ts: Date.now() }] }))
  await page.route('**/api/control/check', route => route.fulfill({ json: { tagId: tag.id, tagName: 'Frequency', current: 2, mode: 'Remote', lockouts: [], refusals: [], dualApproval: false } }))
  const submissions: { value: number; operation: string | null }[] = []
  await page.route(`**/api/screens/${screen.id}/actions/*`, route => { submissions.push(route.request().postDataJSON()); return route.fulfill({ json: { success: true, readBack: route.request().postDataJSON().value } }) })
  await page.goto(`/screens/${screen.id}`)
  await expect(page.locator('[data-node-id="pump-1"]')).toHaveAttribute('data-state', 'state-2')
  await expect(page.locator('[data-node-id="panel"] [data-state-label]')).toHaveText('Moving')
  const input = page.getByRole('spinbutton', { name: 'Frequency setpoint' })
  await input.fill('51'); await page.getByRole('button', { name: '下发', exact: true }).click()
  expect(await input.evaluate((element: HTMLInputElement) => element.validity.rangeOverflow)).toBe(true); expect(submissions).toHaveLength(0)
  await input.fill('25.5'); await page.getByRole('button', { name: '下发', exact: true }).click()
  await expect(page.locator('[data-test="control-confirm"]')).toContainText('25.5'); expect(submissions).toHaveLength(0)
  await page.locator('[data-test="control-confirm-ok"]').click(); await expect.poll(() => submissions.length).toBe(1)
  expect(submissions[0]).toMatchObject({ value: 25.5, operation: null })
  await page.getByRole('button', { name: 'Start pump', exact: true }).click(); await expect(page.locator('[data-test="control-confirm"]')).toContainText('Start pump')
  await page.locator('[data-test="control-confirm-ok"]').click(); await expect.poll(() => submissions.length).toBe(2)
  expect(submissions[1]).toMatchObject({ value: 1, operation: 'start' })
  await expect(page.locator('[data-test="control-confirm"]')).toBeHidden()
  await page.screenshot({ path: 'test-results/hmi-runtime-desktop.png', fullPage: true, animations: 'disabled' })
  sample = { value: 99, quality: 'Good' }; await page.getByRole('button', { name: '刷新', exact: true }).click()
  await expect(page.locator('[data-node-id="pump-1"]')).toHaveAttribute('data-state', 'unknown')
  sample = { value: 1, quality: 'Bad' }; await page.getByRole('button', { name: '刷新', exact: true }).click()
  await expect(page.locator('[data-node-id="panel"] [data-state-label]')).toHaveText('未知状态')
  await page.setViewportSize({ width: 390, height: 844 }); await page.reload()
  await expect(page.locator('[data-node-id="panel"]')).toBeVisible()
  await page.screenshot({ path: 'test-results/hmi-runtime-mobile.png', fullPage: true, animations: 'disabled' })
  expect(await page.locator('.equipment-control').evaluateAll(elements => elements.every(element => element.scrollWidth <= element.clientWidth + 1))).toBe(true)
})

test('HMI preview disables controls and failed tag catalogs fail closed', async ({ page, request }) => {
  const auth = await login(request), { screen } = await createHmiScreen(request, auth.headers)
  await page.addInitScript(({ token, user }) => { localStorage.setItem('scada_token', token); localStorage.setItem('scada_user', JSON.stringify(user)) }, auth)
  const submissions: string[] = []; page.on('request', request => { if (request.url().includes('/actions/')) submissions.push(request.url()) })
  await page.goto(`/config/screens/${screen.id}`); await page.getByRole('button', { name: '预览', exact: true }).click()
  const dialog = page.getByRole('dialog')
  await expect(dialog.getByRole('spinbutton', { name: 'Frequency setpoint' })).toBeDisabled()
  await expect(dialog.getByRole('button', { name: 'Start pump', exact: true })).toBeDisabled(); expect(submissions).toHaveLength(0)
  await page.route(url => url.pathname === '/api/tags', route => route.fulfill({ status: 500, json: { message: 'Catalog unavailable' } }))
  await page.goto(`/screens/${screen.id}`)
  await expect(page.getByRole('spinbutton', { name: 'Frequency setpoint' })).toBeDisabled()
  await expect(page.getByRole('button', { name: 'Start pump', exact: true })).toBeDisabled(); expect(submissions).toHaveLength(0)
})

test('drafts stay private, published revisions stay stable, and stale saves are rejected', async ({ request }) => {
  const { headers } = await login(request)
  const screen = await create(request, headers)
  expect((await request.get(`/api/screens/${screen.id}`, { headers })).status()).toBe(404)
  let response = await request.put(`/api/screens/${screen.id}`, { headers, data: { name: '已发布名称', revision: screen.revision, document: doc() } })
  const saved = await response.json(); expect(response.ok()).toBeTruthy()
  response = await request.post(`/api/screens/${screen.id}/publish`, { headers, data: { revision: saved.revision } })
  const published = await response.json(); expect(response.ok()).toBeTruthy()
  response = await request.put(`/api/screens/${screen.id}`, { headers, data: { name: '未完成草稿', revision: published.revision, document: doc('草稿文字') } })
  expect(response.ok()).toBeTruthy()
  const running = await (await request.get(`/api/screens/${screen.id}`, { headers })).json()
  expect(running.name).toBe('已发布名称'); expect(running.document.nodes[0].text).toBe('泵站')
  expect((await request.put(`/api/screens/${screen.id}`, { headers, data: { name: '过期覆盖', revision: published.revision, document: doc() } })).status()).toBe(409)
  const all = await (await request.get('/api/screens', { headers })).json()
  expect(all.find((s: { id: number }) => s.id === screen.id).name).toBe('已发布名称')
})

test('publication validates references, structure, and protects linked screens from deletion', async ({ request }) => {
  const { headers } = await login(request), target = await create(request, headers, '目标画面'), source = await create(request, headers, '来源画面')
  let shape = doc(); shape.nodes[0].action = { type: 'popup', tagId: null, screenId: target.id, value: 1 }
  let response = await request.put(`/api/screens/${source.id}`, { headers, data: { name: '来源画面', revision: source.revision, document: shape } })
  let revision = (await response.json()).revision
  expect((await request.post(`/api/screens/${source.id}/publish`, { headers, data: { revision } })).status()).toBe(400)
  const targetPublished = await (await request.post(`/api/screens/${target.id}/publish`, { headers, data: { revision: target.revision } })).json()
  expect((await request.post(`/api/screens/${source.id}/publish`, { headers, data: { revision } })).ok()).toBeTruthy()
  expect((await request.delete(`/api/screens/${target.id}?revision=${targetPublished.revision}`, { headers })).status()).toBe(400)
  shape = doc(); shape.nodes[0].x = 1200
  expect((await request.put(`/api/screens/${target.id}`, { headers, data: { name: '越界', revision: targetPublished.revision, document: shape } })).status()).toBe(400)
  const missing = doc(); missing.nodes[0].tagId = 987654
  const validation = await (await request.post('/api/screens/validate', { headers, data: missing })).json()
  expect(validation.issues.length).toBeGreaterThan(0)
})

test('roles enforce draft privacy; disabling or downgrading users revokes their existing tokens', async ({ request }) => {
  const { headers } = await login(request)
  const suffix = Date.now(), screen = await create(request, headers)
  const user = await (await request.post('/api/users', { headers, data: { userName: `viewer${suffix}`, password: 'test-password-123', displayName: '查看员', role: 'Viewer', enabled: true } })).json()
  const viewer = await login(request, user.userName, 'test-password-123')
  expect((await request.get(`/api/screens/${screen.id}?draft=true`, { headers: viewer.headers })).status()).toBe(403)
  expect((await request.post('/api/screens', { headers: viewer.headers, data: { name: '越权创建' } })).status()).toBe(403)
  expect((await request.post(`/api/screens/${screen.id}/actions/pump-1`, { headers: viewer.headers, data: { publishedRevision: 1 } })).status()).toBe(403)
  await request.put(`/api/users/${user.id}`, { headers, data: { displayName: '已停用', role: 'Viewer', enabled: false } })
  expect((await request.get('/api/screens', { headers: viewer.headers })).status()).toBe(401)
  const operatorResponse = await request.post('/api/users', { headers, data: { userName: `operator${suffix}`, password: 'test-password-123', displayName: '操作员', role: 'Operator', enabled: true } })
  const operator = await operatorResponse.json(), session = await login(request, operator.userName, 'test-password-123')
  await request.put(`/api/users/${operator.id}`, { headers, data: { displayName: '已降权', role: 'Viewer', enabled: true } })
  expect((await request.post('/api/commands', { headers: session.headers, data: { tagId: 1, value: 1 } })).status()).toBe(401)
})

test('editor supports drag, resize, undo, redo, save, reopen, preview and publish', async ({ page, request }) => {
  const auth = await login(request), screen = await create(request, auth.headers, '编辑器验收')
  await page.addInitScript(({ token, user }) => { localStorage.setItem('scada_token', token); localStorage.setItem('scada_user', JSON.stringify(user)) }, auth)
  const errors: string[] = []; page.on('pageerror', e => errors.push(e.message))
  await page.goto(`/config/screens/${screen.id}`)
  const designerBox = (await page.locator('.designer').boundingBox())!
  expect(designerBox.x).toBeGreaterThanOrEqual(218)
  await page.locator('[data-palette="pump"]').click()
  const pump = page.locator('[data-node-type="pump"]')
  await expect(pump).toHaveCount(1)
  const before = await pump.getAttribute('transform'), box = (await pump.boundingBox())!
  await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2); await page.mouse.down(); await page.mouse.move(box.x + box.width / 2 + 80, box.y + box.height / 2 + 40, { steps: 6 }); await page.mouse.up()
  const moved = await pump.getAttribute('transform'); expect(moved).not.toBe(before)
  await page.getByRole('button', { name: '撤销', exact: true }).click(); await expect(pump).toHaveAttribute('transform', before!)
  await page.getByRole('button', { name: '重做', exact: true }).click(); await expect(pump).toHaveAttribute('transform', moved!)
  await pump.click()
  const widthBefore = Number(await pump.locator('svg').getAttribute('width'))
  const resizeControl = page.locator('.stage .moveable-control.moveable-se')
  await expect(resizeControl).toBeVisible()
  const handle = (await resizeControl.boundingBox())!
  await page.mouse.move(handle.x + handle.width / 2, handle.y + handle.height / 2); await page.mouse.down()
  await page.mouse.move(handle.x + handle.width / 2 + 40, handle.y + handle.height / 2 + 20, { steps: 5 }); await page.mouse.up()
  expect(Number(await pump.locator('svg').getAttribute('width'))).toBeGreaterThan(widthBefore)
  await page.getByRole('button', { name: '复制', exact: true }).click(); await expect(pump).toHaveCount(2)
  await page.getByRole('button', { name: '撤销', exact: true }).click(); await expect(pump).toHaveCount(1)
  await pump.click(); await page.getByRole('textbox', { name: '图元文字' }).fill('给水泵 A'); await page.getByRole('textbox', { name: '图元文字' }).blur()
  await page.getByRole('button', { name: '保存草稿', exact: true }).click(); await expect(page.getByText('草稿已保存', { exact: true })).toBeVisible()
  await page.reload(); await expect(page.locator('[data-node-type="pump"]')).toHaveCount(1); await expect(pump.locator('text').filter({ hasText: '给水泵 A' })).toBeVisible()
  await page.getByRole('textbox', { name: '画面名称' }).fill('未保存修改')
  await page.getByRole('button', { name: '画面列表', exact: true }).click()
  await page.getByRole('dialog').getByRole('button', { name: '继续编辑', exact: true }).click()
  await expect(page.getByRole('textbox', { name: '画面名称' })).toHaveValue('未保存修改')
  await page.getByRole('textbox', { name: '画面名称' }).fill('编辑器验收'); await page.getByRole('textbox', { name: '画面名称' }).blur()
  await page.getByRole('button', { name: '预览', exact: true }).click(); await expect(page.getByText('草稿预览 · 控制动作禁用')).toBeVisible()
  await page.getByRole('dialog').getByRole('button', { name: '关闭此对话框', exact: true }).click()
  await page.getByRole('button', { name: '发布', exact: true }).click(); await expect(page.getByText('已发布，运行画面已更新')).toBeVisible()
  expect((await page.locator('.header').boundingBox())!.y).toBe(0)
  await page.screenshot({ path: 'test-results/screen-editor.png', fullPage: true })
  await page.goto(`/screens/${screen.id}`); await expect(page.locator('[data-testid="screen-runtime"] [data-node-type="pump"]')).toHaveCount(1)
  expect(errors).toEqual([])
})

test('published navigation and nested popup execute without exposing draft content', async ({ page, request }) => {
  const auth = await login(request), target = await create(request, auth.headers, '弹窗目标'), source = await create(request, auth.headers, '导航入口')
  await request.post(`/api/screens/${target.id}/publish`, { headers: auth.headers, data: { revision: target.revision } })
  const shape = doc(); shape.nodes[0].type = 'button'; shape.nodes[0].text = '查看设备'; shape.nodes[0].action = { type: 'popup', screenId: target.id, tagId: null, value: 1 }
  shape.nodes.push({ ...structuredClone(shape.nodes[0]), id: 'navigate-1', x: 240, text: '进入设备画面', action: { type: 'navigate', screenId: target.id, tagId: null, value: 1 } })
  const saved = await (await request.put(`/api/screens/${source.id}`, { headers: auth.headers, data: { name: '导航入口', revision: source.revision, document: shape } })).json()
  await request.post(`/api/screens/${source.id}/publish`, { headers: auth.headers, data: { revision: saved.revision } })
  await page.addInitScript(({ token, user }) => { localStorage.setItem('scada_token', token); localStorage.setItem('scada_user', JSON.stringify(user)) }, auth)
  await page.goto(`/screens/${source.id}`); await page.locator('[data-node-id="pump-1"]').click()
  await expect(page.getByRole('dialog').getByText('弹窗目标', { exact: true })).toBeVisible()
  await page.getByRole('dialog').getByRole('button', { name: '关闭此对话框', exact: true }).click()
  await page.locator('[data-node-id="navigate-1"]').click()
  await expect(page).toHaveURL(`/screens/${target.id}`)
})

test('leaving an editor while its load is pending does not install stale keyboard handlers', async ({ page, request }) => {
  const auth = await login(request), screen = await create(request, auth.headers, '异步离开验收')
  await page.addInitScript(({ token, user }) => { localStorage.setItem('scada_token', token); localStorage.setItem('scada_user', JSON.stringify(user)) }, auth)
  let release!: () => void
  const gate = new Promise<void>(resolve => { release = resolve })
  await page.route(`**/api/screens/${screen.id}?draft=true`, async route => { await gate; await route.continue() })
  const pendingLoad = page.waitForRequest(r => r.url().endsWith(`/api/screens/${screen.id}?draft=true`))
  await page.goto(`/config/screens/${screen.id}`)
  await pendingLoad
  await page.getByRole('menuitem', { name: '工艺画面', exact: true }).click(); await expect(page).toHaveURL('/screens')
  const response = page.waitForResponse(r => r.url().endsWith(`/api/screens/${screen.id}?draft=true`))
  release(); await (await response).finished()
  await page.evaluate(() => new Promise(requestAnimationFrame))
  const writes: string[] = []; page.on('request', r => { if (r.method() === 'PUT' && r.url().endsWith(`/api/screens/${screen.id}`)) writes.push(r.url()) })
  await page.dispatchEvent('body', 'keydown', { key: 's', ctrlKey: true })
  await page.evaluate(() => new Promise(requestAnimationFrame))
  expect(writes).toHaveLength(0)
})

test('preview never executes commands and runtime preserves unknown data quality', async ({ page, request }) => {
  const auth = await login(request), screen = await create(request, auth.headers, '绑定验收')
  const device = await (await request.post('/api/devices', { headers: auth.headers, data: { name: `test-${Date.now()}`, protocol: 'ModbusTcp', host: '127.0.0.1', port: 1502, stationNumber: 1, byteOrder: 'ABCD', pollIntervalMs: 1000, timeoutMs: 1000, enabled: false } })).json()
  const tag = await (await request.post('/api/tags', { headers: auth.headers, data: { deviceId: device.id, name: '泵频率', area: 'HoldingRegister', address: 0, dataType: 'Float', scale: 1, offset: 0, writable: true, archiveMode: 'None', deadband: 0, archivePeriodSec: 600, minValue: 0, maxValue: 50 } })).json()
  const shape = doc(); shape.nodes[0].type = 'value'; shape.nodes[0].tagId = tag.id; shape.nodes[0].action = { type: 'command', tagId: tag.id, screenId: null, value: 20 }
  const saved = await (await request.put(`/api/screens/${screen.id}`, { headers: auth.headers, data: { name: '绑定验收', revision: screen.revision, document: shape } })).json()
  const published = await (await request.post(`/api/screens/${screen.id}/publish`, { headers: auth.headers, data: { revision: saved.revision } })).json()
  expect((await request.post(`/api/screens/${screen.id}/actions/pump-1`, { headers: auth.headers, data: { publishedRevision: saved.revision } })).status()).toBe(409)
  const action = await (await request.post(`/api/screens/${screen.id}/actions/pump-1`, { headers: auth.headers, data: { publishedRevision: published.publishedRevision } })).json()
  expect(action.success).toBe(false); expect(action.message).toContain('未启用')
  await page.addInitScript(({ token, user }) => { localStorage.setItem('scada_token', token); localStorage.setItem('scada_user', JSON.stringify(user)) }, auth)
  const commandCalls: string[] = []; page.on('request', r => { if (r.url().includes('/actions/')) commandCalls.push(r.url()) })
  await page.goto(`/config/screens/${screen.id}`); await page.getByRole('button', { name: '预览', exact: true }).click()
  await page.getByRole('dialog').locator('[data-node-id="pump-1"]').click()
  await expect(page.getByText('预览模式不执行控制，请发布后在运行画面验证')).toBeVisible(); expect(commandCalls).toHaveLength(0)
  await page.getByRole('dialog').getByRole('button', { name: '关闭此对话框', exact: true }).click()
  await page.goto(`/screens/${screen.id}`)
  await expect(page.locator('[data-node-id="pump-1"]')).toHaveClass(/invalid/)
  await expect(page.locator('[data-node-id="pump-1"] text').filter({ hasText: '--' })).toBeVisible()
})

test('screen run keeps the monitor dialog open when the browser blocks the new window', async ({ page, request }) => {
  const auth = await login(request), screen = await create(request, auth.headers, '新窗口画面')
  await page.addInitScript(({ token, user }) => { localStorage.setItem('scada_token', token); localStorage.setItem('scada_user', JSON.stringify(user)) }, auth)
  await page.goto(`/screens/${screen.id}`)
  await page.evaluate(() => { window.open = () => null })
  await page.locator('[data-test="screen-new-window"]').click()
  const dialog = page.getByRole('dialog', { name: '选择显示器' })
  await expect(dialog).toBeVisible()
  await page.locator('[data-test="screen-open-window"]').click()
  await expect(page.getByText('浏览器阻止了新窗口，请允许本站弹出窗口')).toBeVisible()
  await expect(dialog).toBeVisible()
  await page.evaluate(() => { window.open = () => ({ moveTo() {}, resizeTo() {} }) as unknown as Window })
  await page.locator('[data-test="screen-open-window"]').click()
  await expect(dialog).toBeHidden()
})

test('screen command stays closed when the tag list fails', async ({ page, request }) => {
  const auth = await login(request)
  const screen = await create(request, auth.headers, '失联命令画面')
  const device = await (await request.post('/api/devices', { headers: auth.headers, data: { name: `cmd-${Date.now()}`, protocol: 'ModbusTcp', host: '127.0.0.1', port: 1502, stationNumber: 1, byteOrder: 'ABCD', pollIntervalMs: 1000, timeoutMs: 1000, enabled: false } })).json()
  const tag = await (await request.post('/api/tags', { headers: auth.headers, data: { deviceId: device.id, name: '失联命令点', area: 'HoldingRegister', address: 1, dataType: 'UInt16', scale: 1, offset: 0, writable: true, archiveMode: 'None', deadband: 0, archivePeriodSec: 60 } })).json()
  const shape = doc()
  shape.nodes[0] = { ...shape.nodes[0], type: 'button', text: '失联命令', width: 180, height: 48, tagId: tag.id, action: { type: 'command', tagId: tag.id, screenId: null, value: 1 } }
  const saved = await (await request.put(`/api/screens/${screen.id}`, { headers: auth.headers, data: { name: '失联命令画面', revision: screen.revision, document: shape } })).json()
  expect((await request.post(`/api/screens/${screen.id}/publish`, { headers: auth.headers, data: { revision: saved.revision } })).ok()).toBeTruthy()
  await page.addInitScript(({ token, user }) => { localStorage.setItem('scada_token', token); localStorage.setItem('scada_user', JSON.stringify(user)) }, auth)
  await page.route('**/api/tags*', async route => {
    const url = new URL(route.request().url())
    if (route.request().method() === 'GET' && url.pathname === '/api/tags') {
      await route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message: '测点列表不可用' }) })
      return
    }
    await route.continue()
  })
  const failed = page.waitForResponse(response => { try { return new URL(response.url()).pathname === '/api/tags' && response.status() === 500 } catch { return false } })
  await page.goto(`/screens/${screen.id}`)
  await failed
  await page.getByRole('button', { name: '失联命令' }).click()
  await expect(page.getByText('当前用户没有控制权限')).toBeVisible()
  await expect(page.locator('[data-test="control-confirm"]')).toHaveCount(0)
})

test('screen list shows a load failure instead of an empty library', async ({ page, request }) => {
  const auth = await login(request)
  await page.addInitScript(({ token, user }) => { localStorage.setItem('scada_token', token); localStorage.setItem('scada_user', JSON.stringify(user)) }, auth)
  await page.route('**/api/screens*', async route => {
    const url = new URL(route.request().url())
    if (route.request().method() === 'GET' && url.pathname === '/api/screens') {
      await route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message: '画面列表不可用' }) })
      return
    }
    await route.continue()
  })
  await page.goto('/screens')
  await expect(page.locator('[data-test="screens-load-error"]')).toContainText('画面列表不可用')
  await expect(page.getByText('创建第一个工艺画面，绑定设备测点后发布')).toHaveCount(0)
  await expect(page.getByText('尚无已发布画面')).toHaveCount(0)
})

test('running screen keeps a value-read failure on the page', async ({ page, request }) => {
  const auth = await login(request)
  const suffix = `${Date.now()}`
  const device = await (await request.post('/api/devices', { headers: auth.headers, data: {
    name: `run-${suffix}`, protocol: 'ModbusTcp', host: '127.0.0.1', port: 1502, stationNumber: 1,
    byteOrder: 'ABCD', pollIntervalMs: 1000, timeoutMs: 1000, enabled: false, areaId: 1
  } })).json()
  const tag = await (await request.post('/api/tags', { headers: auth.headers, data: {
    deviceId: device.id, name: `run-tag-${suffix}`, area: 'HoldingRegister', address: 2, dataType: 'Float',
    scale: 1, offset: 0, writable: false, archiveMode: 'None', deadband: 0, archivePeriodSec: 60
  } })).json()
  const screen = await create(request, auth.headers, `运行读值${suffix}`)
  const shape = doc('运行读值')
  shape.nodes[0] = { ...shape.nodes[0], tagId: tag.id }
  const saved = await (await request.put(`/api/screens/${screen.id}`, { headers: auth.headers, data: { name: `运行读值${suffix}`, revision: screen.revision, document: shape } })).json()
  expect((await request.post(`/api/screens/${screen.id}/publish`, { headers: auth.headers, data: { revision: saved.revision } })).ok()).toBeTruthy()
  await page.addInitScript(({ token, user }) => { localStorage.setItem('scada_token', token); localStorage.setItem('scada_user', JSON.stringify(user)) }, auth)
  await page.route((url) => url.pathname === '/api/realtime', (route) => {
    if (route.request().method() !== 'GET') return route.continue()
    return route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message: '画面实时值不可用' }) })
  })
  await page.goto(`/screens/${screen.id}`)
  await expect(page.locator('[data-test="runtime-value-error"]')).toContainText('画面实时值不可用')
  await expect(page.locator('[data-test="runtime-stage"]')).toBeVisible()
  await expect(page.getByText('暂无可运行画面')).toHaveCount(0)
})
