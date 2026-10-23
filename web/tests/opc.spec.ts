import { test, expect, type APIRequestContext } from '@playwright/test'
const endpoint = 'opc.tcp://localhost:18583/ScadaSimulator'
async function auth(request: APIRequestContext) {
  const login = await (await request.post('/api/auth/login', { data: { userName: 'admin', password: 'admin123' } })).json()
  return { ...login, headers: { Authorization: `Bearer ${login.token}` } }
}
function input(name: string) { return { name, protocol: 'OpcUa', host: '', port: 502, stationNumber: 1, byteOrder: 'ABCD', pollIntervalMs: 1000, timeoutMs: 15000, enabled: false,
  opcEndpointUrl: endpoint, opcSecurityMode: 'None', opcSecurityPolicy: 'None', opcUserName: '' } }
async function create(request: APIRequestContext, headers: Record<string, string>, name: string) {
  const response = await request.post('/api/devices', { headers, data: input(name) }); expect(response.status(), await response.text()).toBe(201); return response.json()
}
async function browse(request: APIRequestContext, headers: Record<string, string>, id: number, nodeId = 'i=85', limit = 100, cursor: string | null = null) {
  const response = await request.post(`/api/devices/${id}/opc/browse`, { headers, data: { nodeId, limit, cursor } })
  expect(response.ok(), await response.text()).toBeTruthy(); return response.json()
}
async function variables(request: APIRequestContext, headers: Record<string, string>, id: number) {
  const root = await browse(request, headers, id), folder = root.nodes.find((x: { displayName: string }) => x.displayName === 'Scada')
  expect(folder).toBeTruthy(); const page = await browse(request, headers, id, folder.nodeId)
  return { folder, page, temperature: page.nodes.find((x: { displayName: string }) => x.displayName === 'Temperature'), run: page.nodes.find((x: { displayName: string }) => x.displayName === 'Run') }
}

test('OPC browse pages keep their session; bulk import validates previews and creates all selected tags atomically', async ({ request }) => {
  const { headers } = await auth(request), device = await create(request, headers, `OPC-${Date.now()}`)
  const { folder, page, temperature, run } = await variables(request, headers, device.id)
  expect(page.nodes.length).toBe(100); expect(page.cursor).toBeTruthy()
  const next = await browse(request, headers, device.id, folder.nodeId, 100, page.cursor)
  expect(next.nodes.length).toBe(14); expect(next.cursor).toBeNull()
  const all = [...page.nodes, ...next.nodes]; expect(new Set(all.map(x => x.nodeId)).size).toBe(114)
  const model = all.find(x => x.displayName === 'Model'); expect(model.dataType).toBeNull(); expect(model.error).toBeTruthy()
  const invalid = await (await request.post(`/api/devices/${device.id}/opc/import/preview`, { headers, data: { items: [{ nodeId: model.nodeId, name: 'invalid-model' }, { nodeId: temperature.nodeId, name: 'must-not-create' }] } })).json()
  expect(invalid.canApply).toBeFalsy(); expect((await request.post(`/api/devices/${device.id}/opc/import/${invalid.id}`, { headers })).status()).toBe(400)
  expect((await (await request.get(`/api/tags?deviceId=${device.id}`, { headers })).json()).length).toBe(0)
  const preview = await (await request.post(`/api/devices/${device.id}/opc/import/preview`, { headers, data: { items: [{ nodeId: temperature.nodeId, name: 'Temperature', writable: false }, { nodeId: run.nodeId, name: 'Run', writable: true }] } })).json()
  expect(preview.canApply).toBeTruthy(); expect(preview.rows[0].nodeId).toContain('nsu=')
  const applied = await request.post(`/api/devices/${device.id}/opc/import/${preview.id}`, { headers }); expect(applied.ok(), await applied.text()).toBeTruthy()
  const tags = await applied.json(); expect(tags.length).toBe(2); expect(tags[0].writable).toBeFalsy(); expect(tags[1].writable).toBeTruthy()
  expect((await request.post(`/api/devices/${device.id}/opc/import/${preview.id}`, { headers })).status()).toBe(404)
  const stale = await (await request.post(`/api/devices/${device.id}/opc/import/preview`, { headers, data: { items: [{ nodeId: temperature.nodeId, name: 'NewTemperature' }] } })).json()
  expect((await request.put(`/api/devices/${device.id}`, { headers, data: { ...input(device.name), pollIntervalMs: 2000 } })).ok()).toBeTruthy()
  expect((await request.post(`/api/devices/${device.id}/opc/import/${stale.id}`, { headers })).status()).toBe(409)
  expect((await (await request.get(`/api/tags?deviceId=${device.id}`, { headers })).json()).length).toBe(2)
})

test('OPC cursors and previews belong to their admin; viewers cannot browse, import or change certificate trust', async ({ request }) => {
  const { headers } = await auth(request), suffix = Date.now(), device = await create(request, headers, `Owner-OPC-${suffix}`)
  const other = await (await request.post('/api/users', { headers, data: { userName: `other-admin-${suffix}`, password: 'test-password-123', displayName: 'Other admin', role: 'Admin', enabled: true } })).json()
  const otherAuth = await (await request.post('/api/auth/login', { data: { userName: other.userName, password: 'test-password-123' } })).json()
  const otherHeaders = { Authorization: `Bearer ${otherAuth.token}` }
  const { folder, temperature, page: initial } = await variables(request, headers, device.id)
  if (initial.cursor) await request.delete(`/api/devices/${device.id}/opc/browse/${initial.cursor}`, { headers })
  const page = await browse(request, headers, device.id, folder.nodeId, 1)
  expect(page.cursor).toBeTruthy()
  expect((await request.post(`/api/devices/${device.id}/opc/browse`, { headers: otherHeaders, data: { nodeId: folder.nodeId, cursor: page.cursor } })).status()).toBe(404)
  await request.delete(`/api/devices/${device.id}/opc/browse/${page.cursor}`, { headers: otherHeaders })
  expect((await browse(request, headers, device.id, folder.nodeId, 1, page.cursor)).nodes.length).toBe(1)
  await request.delete(`/api/devices/${device.id}/opc/browse/${page.cursor}`, { headers })
  const preview = await (await request.post(`/api/devices/${device.id}/opc/import/preview`, { headers, data: { items: [{ nodeId: temperature.nodeId, name: 'owned-preview' }] } })).json()
  expect((await request.post(`/api/devices/${device.id}/opc/import/${preview.id}`, { headers: otherHeaders })).status()).toBe(404)
  const viewer = await (await request.post('/api/users', { headers, data: { userName: `opc-viewer-${suffix}`, password: 'test-password-123', displayName: 'OPC viewer', role: 'Viewer', enabled: true } })).json()
  const viewerAuth = await (await request.post('/api/auth/login', { data: { userName: viewer.userName, password: 'test-password-123' } })).json()
  const viewerHeaders = { Authorization: `Bearer ${viewerAuth.token}` }
  expect((await request.post(`/api/devices/${device.id}/opc/browse`, { headers: viewerHeaders, data: {} })).status()).toBe(403)
  expect((await request.post(`/api/devices/${device.id}/opc/import/${preview.id}`, { headers: viewerHeaders })).status()).toBe(403)
  expect((await request.get('/api/opc/certificates', { headers: viewerHeaders })).status()).toBe(403)
})

test('encrypted OPC connection requires explicit certificate trust and authenticates with a protected password', async ({ request }) => {
  const { headers } = await auth(request), plain = input(`Secure-OPC-${Date.now()}`)
  const secure = { ...plain, opcSecurityMode: 'SignAndEncrypt', opcSecurityPolicy: 'Basic256Sha256', opcUserName: 'operator', opcPassword: 'opc-test-password' }
  const response = await request.post('/api/devices', { headers, data: secure }); expect(response.ok(), await response.text()).toBeTruthy(); const device = await response.json()
  expect(device.hasOpcPassword).toBeTruthy(); expect(device.opcPasswordProtected).toBeUndefined(); expect(device.opcPassword).toBeUndefined()
  const rejected = await request.post(`/api/devices/${device.id}/opc/browse`, { headers, data: {} }); expect(rejected.status()).toBe(502)
  const certs = await (await request.get('/api/opc/certificates', { headers })).json()
  const cert = certs.find((x: { store: string; subject: string }) => x.store === 'rejected' && x.subject.includes('ScadaOpcSimulator'))
  expect(cert).toBeTruthy(); expect(certs.some((x: { store: string }) => x.store === 'own')).toBeTruthy()
  expect((await request.post(`/api/opc/certificates/${cert.sha256}/trust`, { headers })).ok()).toBeTruthy()
  const connected = await browse(request, headers, device.id); expect(connected.nodes.length).toBeGreaterThan(0)
  const update = await request.put(`/api/devices/${device.id}`, { headers, data: { ...secure, opcPassword: null } }); expect(update.ok()).toBeTruthy()
  expect((await update.json()).hasOpcPassword).toBeTruthy(); await browse(request, headers, device.id)
  expect((await request.put(`/api/devices/${device.id}`, { headers, data: { ...secure, opcPassword: 'wrong-password' } })).ok()).toBeTruthy()
  expect((await request.post(`/api/devices/${device.id}/opc/browse`, { headers, data: {} })).status()).toBe(502)
  expect((await request.delete(`/api/opc/certificates/${cert.sha256}/trust`, { headers })).ok()).toBeTruthy()
  const anonSecure = { ...secure, opcUserName: '', opcPassword: null, clearOpcPassword: true }
  expect((await request.put(`/api/devices/${device.id}`, { headers, data: anonSecure })).ok()).toBeTruthy()
  expect((await request.post(`/api/devices/${device.id}/opc/browse`, { headers, data: {} })).status()).toBe(502)
})

test('engineer browses live OPC variables, reviews mapping and imports them in the UI', async ({ page, request }) => {
  const session = await auth(request), device = await create(request, session.headers, `UI-OPC-${Date.now()}`)
  await page.addInitScript(({ token, user }) => { localStorage.setItem('scada_token', token); localStorage.setItem('scada_user', JSON.stringify(user)) }, session)
  const errors: string[] = []; page.on('pageerror', e => errors.push(e.message))
  await page.goto('/config/devices'); const row = page.getByRole('row').filter({ hasText: device.name })
  await row.getByRole('button', { name: '浏览变量' }).click()
  const dialog = page.getByRole('dialog', { name: `OPC UA 变量 · ${device.name}`, exact: true })
  await dialog.getByRole('button', { name: 'Scada', exact: true }).click()
  const variableRow = dialog.locator('.el-table').first().getByRole('row').filter({ hasText: 'Temperature' })
  await variableRow.locator('.el-checkbox__inner').click()
  await expect(variableRow.locator('.el-checkbox__input')).toHaveClass(/is-checked/)
  await dialog.getByRole('textbox', { name: '导入测点名称' }).fill('UI-Temperature')
  await page.screenshot({ path: 'test-results/opc-variable-browser.png' })
  await dialog.getByRole('button', { name: '预览导入', exact: true }).click()
  const preview = page.getByRole('dialog', { name: 'OPC UA 导入预览', exact: true })
  await expect(preview.getByText('UI-Temperature', { exact: true })).toBeVisible()
  await preview.getByRole('button', { name: '确认创建测点', exact: true }).click(); await expect(dialog).not.toBeVisible()
  const tags = await (await request.get(`/api/tags?deviceId=${device.id}`, { headers: session.headers })).json()
  expect(tags.length).toBe(1); expect(tags[0].name).toBe('UI-Temperature'); expect(tags[0].area).toBe('OpcNode'); expect(tags[0].writable).toBeFalsy()
  await page.getByRole('button', { name: 'OPC UA 证书', exact: true }).click()
  await page.getByRole('tab', { name: '客户端公钥', exact: true }).click(); await expect(page.getByRole('button', { name: '下载公钥' }).first()).toBeVisible()
  expect(errors).toEqual([])
})

test('OPC connection editor keeps stored passwords hidden and clears them only when requested', async ({ page, request }) => {
  const session = await auth(request), name = `UI-Credentials-${Date.now()}`
  await page.addInitScript(({ token, user }) => { localStorage.setItem('scada_token', token); localStorage.setItem('scada_user', JSON.stringify(user)) }, session)
  await page.goto('/config/devices'); await page.getByRole('button', { name: '新增设备', exact: true }).click()
  let dialog = page.getByRole('dialog', { name: '新增设备', exact: true })
  const field = (label: string) => dialog.locator('.el-form-item').filter({ has: page.locator('label').filter({ hasText: new RegExp(`^${label}$`) }) })
  await field('设备名称').locator('input').fill(name)
  await field('协议').locator('.el-select').click(); await page.getByRole('option', { name: 'OPC UA', exact: true }).click()
  await field('端点 URL').locator('input').fill(endpoint); await field('用户名').locator('input').fill('operator')
  await field('密码').locator('input').fill('opc-test-password')
  await field('启用').locator('.el-switch__core').click()
  await page.screenshot({ path: 'test-results/opc-connection-editor.png' })
  await dialog.getByRole('button', { name: '保存', exact: true }).click(); await expect(dialog).not.toBeVisible()
  let device = (await (await request.get('/api/devices', { headers: session.headers })).json()).find((x: { name: string }) => x.name === name)
  expect(device.hasOpcPassword).toBeTruthy(); expect(device.opcPasswordProtected).toBeUndefined()
  await page.getByRole('row').filter({ hasText: name }).getByRole('button', { name: '编辑', exact: true }).click()
  dialog = page.getByRole('dialog', { name: '编辑设备', exact: true }); await expect(field('密码').locator('input')).toHaveValue('')
  await dialog.getByText('保存时清除已保存密码', { exact: true }).click()
  await expect(dialog.getByRole('checkbox', { name: '保存时清除已保存密码', exact: true })).toBeChecked()
  await dialog.getByRole('button', { name: '保存', exact: true }).click(); await expect(dialog).not.toBeVisible()
  device = await (await request.get(`/api/devices/${device.id}`, { headers: session.headers })).json(); expect(device.hasOpcPassword).toBeFalsy()
})
