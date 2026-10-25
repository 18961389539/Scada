import { test, expect, type APIRequestContext } from '@playwright/test'
import type { ScreenDocument } from '../src/screens/model'

async function auth(request: APIRequestContext, userName = 'admin', password = 'admin123') {
  const response = await request.post('/api/auth/login', { data: { userName, password } }); expect(response.ok()).toBeTruthy()
  const result = await response.json(); return { ...result, headers: { Authorization: `Bearer ${result.token}` } }
}
async function create(request: APIRequestContext, headers: Record<string, string>, name: string, expression: string) {
  const response = await request.post('/api/calculated-tags', { headers, data: { name, expression, intervalMs: 200 } })
  expect(response.status(), await response.text()).toBe(201); return response.json()
}
const read = async (request: APIRequestContext, headers: Record<string, string>, id: number) =>
  (await (await request.get(`/api/realtime?tagIds=${id}`, { headers })).json())[0]

test('physical engineering values feed calculation chains, history, alarms and published screens; failed inputs retain value with uncertain quality', async ({ page, request }) => {
  test.setTimeout(90000)
  const session = await auth(request), headers = session.headers, suffix = Date.now()
  const deviceInput = { name: `Calc-OPC-${suffix}`, protocol: 'OpcUa', host: '', port: 502, stationNumber: 1, byteOrder: 'ABCD', pollIntervalMs: 200, timeoutMs: 5000, enabled: false,
    opcEndpointUrl: 'opc.tcp://localhost:18583/ScadaSimulator', opcSecurityMode: 'None', opcSecurityPolicy: 'None', opcUserName: '' }
  let response = await request.post('/api/devices', { headers, data: deviceInput }); expect(response.status()).toBe(201); const device = await response.json()
  const browse = async (nodeId: string) => { const result = await request.post(`/api/devices/${device.id}/opc/browse`, { headers, data: { nodeId, limit: 100 } }); expect(result.ok(), await result.text()).toBeTruthy(); return result.json() }
  const root = await browse('i=85'), folder = root.nodes.find((x: any) => x.displayName === 'Scada'), variables = await browse(folder.nodeId)
  const temperature = variables.nodes.find((x: any) => x.displayName === 'Temperature'); expect(temperature).toBeTruthy()
  if (variables.cursor) await request.delete(`/api/devices/${device.id}/opc/browse/${variables.cursor}`, { headers })
  const sourceInput = { deviceId: device.id, name: `Calc-source-${suffix}`, area: 'OpcNode', nodeId: temperature.nodeId, address: 0, dataType: temperature.dataType, scale: 2, offset: 1, writable: false,
    archiveMode: 'OnChange', deadband: 0, archivePeriodSec: 600 }
  response = await request.post('/api/tags', { headers, data: sourceInput }); expect(response.status(), await response.text()).toBe(201); const source = await response.json()
  expect((await request.put(`/api/devices/${device.id}`, { headers, data: { ...deviceInput, enabled: true } })).ok()).toBeTruthy()
  await expect.poll(async () => (await read(request, headers, source.id))?.quality, { timeout: 20000 }).toBe('Good')
  expect((await read(request, headers, source.id)).value).toBe(48)
  const first = await create(request, headers, `Calc-first-${suffix}`, `#${source.id}+1`)
  const second = await create(request, headers, `Calc-second-${suffix}`, `#${first.id}*2`)
  const started = Date.now() - 5000
  await expect.poll(async () => (await read(request, headers, second.id))?.value, { timeout: 15000 }).toBe(98)
  expect(first.deviceId).toBeNull(); expect(first.writable).toBe(false)
  response = await request.post('/api/commands', { headers, data: { tagId: second.id, value: 1 } }); const rejected = await response.json(); expect(rejected.success).toBe(false); expect(rejected.message).toContain('只读')
  response = await request.post('/api/alarm-rules', { headers, data: { tagId: second.id, type: 'High', threshold: 90, level: 'Warning', delaySec: 0, recoverDeadband: 0, enabled: true, message: 'calculation-high' } }); expect(response.status(), await response.text()).toBe(201)
  await expect.poll(async () => (await (await request.get('/api/alarms?activeOnly=true', { headers })).json()).some((x: any) => x.tagId === second.id), { timeout: 15000 }).toBe(true)
  await expect.poll(async () => {
    const history = await (await request.get(`/api/history?tagIds=${second.id}&from=${started}&to=${Date.now()}`, { headers })).json()
    return history.series.some((series: any) => series.points.some((p: any) => p.value === 98 && p.quality === 'Good'))
  }, { timeout: 15000 }).toBe(true)
  const screenName = `Calc-screen-${suffix}`
  response = await request.post('/api/screens', { headers, data: { name: screenName } }); expect(response.status()).toBe(201); const screen = await response.json()
  const document: ScreenDocument = { schemaVersion: 1, width: 1200, height: 720, background: '#101b2d', nodes: [{ id: 'calculated-value', type: 'value', x: 100, y: 100, width: 300, height: 100, rotation: 0, text: '计算结果', color: '#7394b8', activeColor: '#34d399', unit: 'kW', decimals: 1, min: 0, max: 100, activeThreshold: .5, tagId: second.id, action: { type: 'none', tagId: null, screenId: null, value: 1 } }] }
  response = await request.put(`/api/screens/${screen.id}`, { headers, data: { name: screenName, revision: screen.revision, document } }); const saved = await response.json(); expect(response.ok(), JSON.stringify(saved)).toBeTruthy()
  response = await request.post(`/api/screens/${screen.id}/publish`, { headers, data: { revision: saved.revision } }); expect(response.ok(), await response.text()).toBeTruthy()
  await page.addInitScript(({ token, user }) => { localStorage.setItem('scada_token', token); localStorage.setItem('scada_user', JSON.stringify(user)) }, session)
  await page.goto(`/screens/${screen.id}`); await expect(page.locator('svg text').filter({ hasText: '98.0' }).first()).toBeVisible({ timeout: 15000 })
  await page.screenshot({ path: test.info().outputPath('calculated-screen.png'), animations: 'disabled' })
  expect((await request.delete(`/api/tags/${source.id}`, { headers })).status()).toBe(400)
  expect((await request.delete(`/api/devices/${device.id}`, { headers })).status()).toBe(400)
  expect((await request.put(`/api/tags/${source.id}`, { headers, data: { ...sourceInput, nodeId: 'ns=2;s=missing-calculation-input' } })).ok()).toBeTruthy()
  await expect.poll(async () => (await read(request, headers, second.id))?.quality, { timeout: 15000 }).toBe('Uncertain')
  expect((await read(request, headers, second.id)).value).toBe(98)
  expect((await request.put(`/api/devices/${device.id}`, { headers, data: deviceInput })).ok()).toBeTruthy()
})

test('definition graph changes enforce versions, atomic rollback and admin-only mutation', async ({ request }) => {
  const { headers } = await auth(request), suffix = Date.now(), first = await create(request, headers, `Version-first-${suffix}`, '2'), second = await create(request, headers, `Version-second-${suffix}`, `#${first.id}+1`)
  expect((await request.post('/api/calculated-tags', { headers, data: { name: `Missing-${suffix}`, expression: '#2147483647' } })).status()).toBe(400)
  expect((await request.put(`/api/calculated-tags/${first.id}`, { headers, data: { name: first.name, expression: `#${second.id}`, revision: 1 } })).status()).toBe(400)
  expect((await request.get(`/api/tags/${first.id}`, { headers })).ok()).toBeTruthy()
  expect((await request.delete(`/api/calculated-tags/${first.id}?revision=1`, { headers })).status()).toBe(409)
  expect((await request.put(`/api/calculated-tags/${second.id}`, { headers, data: { name: second.name, expression: '3', revision: 1 } })).status()).toBe(200)
  expect((await request.put(`/api/calculated-tags/${second.id}`, { headers, data: { name: second.name, expression: '4', revision: 1 } })).status()).toBe(409)
  const current = await (await request.get(`/api/tags/${second.id}`, { headers })).json(); expect(current.expression).toBe('3'); expect(current.calculationRevision).toBe(2)
  for (const role of ['Viewer', 'Operator']) {
    const userName = `calc-${role}-${suffix}`; expect((await request.post('/api/users', { headers, data: { userName, password: 'test-password-123', displayName: role, role, enabled: true } })).status()).toBe(201)
    const user = await auth(request, userName, 'test-password-123')
    expect((await request.get('/api/calculated-tags', { headers: user.headers })).status()).toBe(200)
    expect((await request.post('/api/calculated-tags/preview', { headers: user.headers, data: { name: 'x', expression: '1' } })).status()).toBe(403)
    expect((await request.post('/api/calculated-tags', { headers: user.headers, data: { name: 'x', expression: '1' } })).status()).toBe(403)
    expect((await request.put(`/api/calculated-tags/${first.id}`, { headers: user.headers, data: { name: 'x', expression: '1', revision: 1 } })).status()).toBe(403)
    expect((await request.delete(`/api/calculated-tags/${first.id}?revision=1`, { headers: user.headers })).status()).toBe(403)
  }
  expect((await request.delete(`/api/calculated-tags/${first.id}?revision=1`, { headers })).status()).toBe(204)
})

test('engineer previews and saves expressions, resolves stale edits explicitly, and leaves polling safely', async ({ page, request }) => {
  const session = await auth(request), name = `UI-Calculated-${Date.now()}`, errors: string[] = []; page.on('pageerror', e => errors.push(e.message))
  await page.addInitScript(({ token, user }) => { localStorage.setItem('scada_token', token); localStorage.setItem('scada_user', JSON.stringify(user)) }, session)
  await page.goto('/config/calculations'); await page.getByRole('button', { name: '新建计算测点', exact: true }).click()
  let dialog = page.getByRole('dialog', { name: '新建计算测点', exact: true }); await dialog.getByRole('textbox', { name: '计算测点名称', exact: true }).fill(name)
  await dialog.getByRole('textbox', { name: '计算表达式', exact: true }).fill('if(2>1, 6*7, 1/0)')
  await dialog.getByRole('button', { name: '校验并预览' }).click(); await expect(dialog.getByRole('alert').filter({ hasText: '预览值' })).toContainText('预览值 42')
  await page.screenshot({ path: test.info().outputPath('calculation-editor.png'), animations: 'disabled' })
  await dialog.getByRole('button', { name: '保存计算定义' }).click(); await expect(dialog).not.toBeVisible()
  await page.getByPlaceholder('名称、表达式或编号').fill(name); const row = page.getByRole('row').filter({ hasText: name }); await expect(row.getByText('42.00', { exact: true })).toBeVisible(); await expect(row).toContainText('正常')
  await page.screenshot({ path: test.info().outputPath('calculation-status.png'), animations: 'disabled' })
  const list = await (await request.get('/api/calculated-tags', { headers: session.headers })).json(), tag = list.find((x: any) => x.tag.name === name).tag
  await row.getByRole('button', { name: '编辑', exact: true }).click(); dialog = page.getByRole('dialog', { name: '编辑计算测点', exact: true })
  await dialog.getByRole('textbox', { name: '计算表达式', exact: true }).fill('99')
  expect((await request.put(`/api/calculated-tags/${tag.id}`, { headers: session.headers, data: { name, expression: '43', revision: 1 } })).ok()).toBeTruthy()
  await dialog.getByRole('button', { name: '保存计算定义' }).click(); await expect(dialog.getByRole('alert').filter({ hasText: '已变化' })).toContainText('已变化')
  await expect(dialog.getByRole('textbox', { name: '计算表达式', exact: true })).toHaveValue('99')
  await dialog.getByRole('button', { name: '重新读取定义' }).click(); await page.getByRole('button', { name: '放弃修改', exact: true }).click()
  await expect(dialog.getByRole('textbox', { name: '计算表达式', exact: true })).toHaveValue('43')
  await dialog.getByRole('button', { name: '取消', exact: true }).click(); await expect(dialog).not.toBeVisible()
  await page.route('**/api/calculated-tags', route => route.fulfill({ status: 503, contentType: 'application/json', body: '{"message":"test-read-failure"}' }))
  await page.getByRole('button', { name: '刷新计算状态' }).click(); await expect(page.locator('.el-alert')).toContainText('test-read-failure'); await expect(row).toContainText('待刷新')
  await page.unroute('**/api/calculated-tags'); await page.getByRole('button', { name: '刷新计算状态' }).click(); await expect(page.locator('.el-alert')).toHaveCount(0)
  await page.getByRole('menuitem', { name: '实时监视', exact: true }).click(); await expect(page).toHaveURL(/\/realtime/)
  let subsequentReads = 0; page.on('request', req => { if (new URL(req.url()).pathname === '/api/calculated-tags') subsequentReads++ })
  await page.waitForTimeout(2300); expect(subsequentReads).toBe(0); expect(errors).toEqual([])
})
