import { test, expect, type APIRequestContext } from '@playwright/test'
import { readFileSync } from 'node:fs'

const endpoint = 'opc.tcp://localhost:18583/ScadaSimulator'
async function auth(request: APIRequestContext) {
  const response = await request.post('/api/auth/login', { data: { userName: 'admin', password: 'admin123' } })
  expect(response.ok()).toBeTruthy(); const session = await response.json()
  return { ...session, headers: { Authorization: `Bearer ${session.token}` } }
}
function input(name: string, enabled = true) {
  return { name, protocol: 'OpcUa', host: 'localhost', port: 4840, stationNumber: 1, byteOrder: 'ABCD', pollIntervalMs: 100, timeoutMs: 5000, enabled,
    opcEndpointUrl: endpoint, opcSecurityMode: 'None', opcSecurityPolicy: 'None', opcUserName: '', opcPassword: 'diagnostic-must-not-export-this-secret' }
}
async function create(request: APIRequestContext, headers: Record<string, string>, name: string) {
  const deviceResponse = await request.post('/api/devices', { headers, data: input(name) }); expect(deviceResponse.ok()).toBeTruthy()
  const device = await deviceResponse.json()
  const root = await (await request.post(`/api/devices/${device.id}/opc/browse`, { headers, data: {} })).json()
  const folder = root.nodes.find((x: { displayName: string }) => x.displayName === 'Scada')
  const variables = await (await request.post(`/api/devices/${device.id}/opc/browse`, { headers, data: { nodeId: folder.nodeId, limit: 200 } })).json()
  const temperature = variables.nodes.find((x: { displayName: string }) => x.displayName === 'Temperature')
  for (const point of [{ name: 'GoodTemperature', nodeId: temperature.nodeId, dataType: 'Float', scale: 2 }, { name: 'UnknownNode', nodeId: 'nsu=urn:scada:simulator:variables;s=missing', dataType: 'UInt16', scale: 1 }]) {
    const response = await request.post('/api/tags', { headers, data: { deviceId: device.id, area: 'OpcNode', address: 0, offset: 0, writable: false, archiveMode: 'None', deadband: 0, archivePeriodSec: 600, ...point } })
    expect(response.ok(), await response.text()).toBeTruthy()
  }
  await expect.poll(async () => (await (await request.get(`/api/devices/${device.id}/diagnostics`, { headers })).json()).diagnostics.state).toBe('Degraded')
  return device
}

test('diagnostics distinguish point failures, expose real read groups, page results and export no credentials', async ({ request }) => {
  const { headers } = await auth(request), device = await create(request, headers, `Diagnostic-${Date.now()}`)
  const url = `/api/devices/${device.id}/diagnostics`
  const response = await request.get(url, { headers }); expect(response.headers()['cache-control']).toBe('no-store')
  const { diagnostics: snapshot, total } = await response.json()
  expect(total).toBe(2); expect(snapshot.quality.good).toBe(1); expect(snapshot.quality.bad).toBe(1)
  expect(snapshot.metrics.pollSuccesses).toBeGreaterThan(0); expect(snapshot.metrics.pollFailures).toBe(0); expect(snapshot.metrics.degradedPolls).toBeGreaterThan(0)
  expect(snapshot.readGroups).toHaveLength(1); expect(snapshot.readGroups[0].unit).toBe('节点'); expect(snapshot.readGroups[0].tagIds).toHaveLength(2)
  const good = snapshot.points.find((x: { name: string }) => x.name === 'GoodTemperature'), bad = snapshot.points.find((x: { name: string }) => x.name === 'UnknownNode')
  expect(good.rawValue).toBe(23.5); expect(good.value).toBe(47); expect(good.lastGoodTs).toBeGreaterThan(0)
  expect(bad.quality).toBe('Bad'); expect(bad.rawValue).toBeNull(); expect(bad.error).toContain('BadNodeIdUnknown')
  const filtered = await (await request.get(`${url}?quality=Bad&pageSize=1&search=Unknown`, { headers })).json()
  expect(filtered.total).toBe(1); expect(filtered.diagnostics.points).toHaveLength(1); expect(filtered.diagnostics.pointCount).toBe(2)
  const empty = await (await request.get(`${url}?staleOnly=true`, { headers })).json(); expect(empty.total).toBe(0)
  for (const query of ['page=0', 'pageSize=201', 'quality=999', `search=${'a'.repeat(201)}`]) expect((await request.get(`${url}?${query}`, { headers })).status()).toBe(400)
  const exported = await request.get(`${url}/export`, { headers }); expect(exported.headers()['content-disposition']).toContain('attachment')
  const text = await exported.text(); expect(JSON.parse(text).points).toHaveLength(2)
  for (const secret of ['opcPassword', 'opcUserName', 'diagnostic-must-not-export-this-secret', headers.Authorization]) expect(text).not.toContain(secret)
  const before = snapshot.metrics.connectAttempts; expect((await request.post(`${url}/reconnect`, { headers })).status()).toBe(202)
  await expect.poll(async () => (await (await request.get(url, { headers })).json()).diagnostics.metrics.connectAttempts).toBeGreaterThan(before)
  const log = await (await request.get(`/api/logs?keyword=${encodeURIComponent(`请求采集重连：设备 #${device.id}`)}`, { headers })).json(); expect(log.total).toBeGreaterThan(0)
})

test('diagnostics and reconnect require admin; disabled and empty devices never appear healthy', async ({ request }) => {
  const { headers } = await auth(request), suffix = Date.now()
  const disabled = await (await request.post('/api/devices', { headers, data: input(`DisabledDiagnostic-${suffix}`, false) })).json()
  const empty = await (await request.post('/api/devices', { headers, data: input(`EmptyDiagnostic-${suffix}`) })).json()
  expect((await (await request.get(`/api/devices/${disabled.id}/diagnostics`, { headers })).json()).diagnostics.state).toBe('Disabled')
  expect((await (await request.get(`/api/devices/${empty.id}/diagnostics`, { headers })).json()).diagnostics.state).toBe('NoTags')
  for (const id of [disabled.id, empty.id]) expect((await request.post(`/api/devices/${id}/diagnostics/reconnect`, { headers })).status()).toBe(409)
  expect((await request.get('/api/devices/2147483647/diagnostics', { headers })).status()).toBe(404)
  const userName = `diag-viewer-${suffix}`
  expect((await request.post('/api/users', { headers, data: { userName, password: 'test-password-123', displayName: 'Viewer', role: 'Viewer', enabled: true } })).ok()).toBeTruthy()
  const session = await (await request.post('/api/auth/login', { data: { userName, password: 'test-password-123' } })).json()
  const viewer = { Authorization: `Bearer ${session.token}` }, url = `/api/devices/${disabled.id}/diagnostics`
  expect((await request.get(url)).status()).toBe(401)
  expect((await request.get(url, { headers: viewer })).status()).toBe(403)
  expect((await request.get(`${url}/export`, { headers: viewer })).status()).toBe(403)
  expect((await request.post(`${url}/reconnect`, { headers: viewer })).status()).toBe(403)
})

test('engineer inspects live point quality, read groups, reconnect events and downloads a diagnostic snapshot', async ({ page, request }) => {
  const session = await auth(request), device = await create(request, session.headers, `UI-Diagnostic-${Date.now()}`)
  await page.addInitScript(({ token, user }) => { localStorage.setItem('scada_token', token); localStorage.setItem('scada_user', JSON.stringify(user)) }, session)
  const errors: string[] = []; page.on('pageerror', e => errors.push(e.message))
  await page.goto('/config/devices'); await page.getByRole('row').filter({ hasText: device.name }).getByRole('button', { name: '采集诊断', exact: true }).click()
  const dialog = page.getByRole('dialog', { name: `采集诊断 · ${device.name}`, exact: true })
  await expect(dialog.getByText('部分测点异常', { exact: true })).toBeVisible()
  await expect(dialog.getByRole('row').filter({ hasText: 'GoodTemperature' }).getByText('47.00', { exact: true })).toBeVisible()
  await expect(dialog.getByRole('row').filter({ hasText: 'UnknownNode' }).getByText('故障', { exact: true })).toBeVisible()
  await dialog.getByRole('textbox', { name: '诊断搜索', exact: true }).fill('Unknown'); await dialog.getByRole('textbox', { name: '诊断搜索', exact: true }).press('Enter')
  await expect(dialog.getByRole('row').filter({ hasText: 'GoodTemperature' })).not.toBeVisible()
  await expect(dialog.getByRole('row').filter({ hasText: 'UnknownNode' }).getByText('故障', { exact: true })).toBeVisible()
  await page.screenshot({ path: 'test-results/collector-diagnostics.png', animations: 'disabled' })
  await dialog.getByRole('tab', { name: '读取分组', exact: true }).click(); await expect(dialog.getByRole('cell', { name: 'OPC UA 节点', exact: true })).toBeVisible()
  await dialog.getByRole('button', { name: '请求重连', exact: true }).click()
  await dialog.getByRole('tab', { name: '最近事件', exact: true }).click(); await expect(dialog.getByText('执行手动重连', { exact: true }).first()).toBeVisible()
  const pendingDownload = page.waitForEvent('download'); await dialog.getByRole('button', { name: '导出诊断', exact: true }).click(); const download = await pendingDownload
  const path = await download.path(); expect(path).toBeTruthy(); const snapshot = JSON.parse(readFileSync(path!, 'utf8'))
  expect(snapshot.points).toHaveLength(2); expect(snapshot.deviceId).toBe(device.id); expect(snapshot.events.some((x: { kind: string }) => x.kind === 'Reconnecting')).toBeTruthy()
  await dialog.getByRole('button', { name: '关闭', exact: true }).click(); await expect(dialog).not.toBeVisible(); expect(errors).toEqual([])
})

test('closing diagnostics with a pending request cancels loading and stops automatic refresh', async ({ page, request }) => {
  const session = await auth(request), device = await create(request, session.headers, `UI-ClosingDiagnostic-${Date.now()}`)
  await page.addInitScript(({ token, user }) => { localStorage.setItem('scada_token', token); localStorage.setItem('scada_user', JSON.stringify(user)) }, session)
  await page.clock.install()
  let release!: () => void, requests = 0
  const gate = new Promise<void>(resolve => { release = resolve })
  await page.route(`**/api/devices/${device.id}/diagnostics?**`, async route => {
    requests++; await gate; await route.continue().catch(() => { /* The closed dialog aborts this request. */ })
  })
  try {
    await page.goto('/config/devices'); await page.getByRole('row').filter({ hasText: device.name }).getByRole('button', { name: '采集诊断', exact: true }).click()
    const dialog = page.getByRole('dialog', { name: `采集诊断 · ${device.name}`, exact: true })
    await expect.poll(() => requests).toBe(1)
    await dialog.getByRole('button', { name: '关闭', exact: true }).click(); await expect(dialog).not.toBeVisible()
    release(); await page.clock.fastForward(10000); expect(requests).toBe(1)
  } finally { release() }
})
