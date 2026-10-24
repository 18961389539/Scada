import { test, expect } from '@playwright/test'
import { spawn, type ChildProcess } from 'node:child_process'
import { once } from 'node:events'
import { createHash } from 'node:crypto'
import { mkdtempSync, readFileSync, rmSync } from 'node:fs'
import { join, resolve, sep } from 'node:path'
import { tmpdir } from 'node:os'

test('engineering runs at the assigned gateway: isolated certificates, OPC paging/import, probe and live diagnostics', async ({ request, page }) => {
  test.setTimeout(90000)
  const root = mkdtempSync(join(tmpdir(), 'scada-engineering-e2e-'))
  let child: ChildProcess | undefined, log = ''
  const auth = await (await request.post('/api/auth/login', { data: { userName: 'admin', password: 'admin123' } })).json(), headers = { Authorization: `Bearer ${auth.token}` }
  const identity = await (await request.post('/api/gateways', { headers, data: { name: `Engineering-${Date.now()}` } })).json()
  const input = { name: `EngineeringDevice-${Date.now()}`, protocol: 'OpcUa', host: 'localhost', port: 4840, stationNumber: 1, byteOrder: 'ABCD', pollIntervalMs: 200,
    timeoutMs: 3000, enabled: false, opcEndpointUrl: 'opc.tcp://localhost:18583/ScadaSimulator', opcSecurityMode: 'SignAndEncrypt', opcSecurityPolicy: 'Basic256Sha256', opcUserName: 'operator', opcPassword: 'opc-test-password' }
  const deviceResponse = await request.post('/api/devices', { headers, data: input }); expect(deviceResponse.ok(), await deviceResponse.text()).toBeTruthy(); const device = await deviceResponse.json()
  const base = `/api/devices/${device.id}`, certs = `${base}/opc/certificates?gatewayId=${identity.id}`
  async function browse(data: object, caller = headers) { const response = await request.post(`${base}/opc/browse`, { headers: caller, data }); expect(response.ok(), await response.text()).toBeTruthy(); return response.json() }
  function status(): any { try { return JSON.parse(readFileSync(join(root, 'status.json'), 'utf8')) } catch { return {} } }
  try {
    expect((await request.post(`/api/gateways/assignments/${device.id}`, { headers, data: { gatewayId: identity.id } })).ok()).toBeTruthy()
    child = spawn('dotnet', [resolve('../src/Scada.Gateway.Host/bin/Release/net10.0/Scada.Gateway.Host.dll')], { windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'], env: { ...process.env,
      GatewayAgent__ServerUrl: 'http://127.0.0.1:18581/', GatewayAgent__GatewayId: String(identity.id), GatewayAgent__Token: identity.token, GatewayAgent__DataRoot: root,
      GatewayAgent__AllowLoopbackHttp: 'true', GatewayAgent__HeartbeatIntervalMs: '500', GatewayAgent__ConfigurationPollMs: '500', GatewayAgent__RetryDelayMs: '200' } })
    const capture = (chunk: Buffer) => { log = (log + String(chunk)).slice(-16000) }; child.stdout!.on('data', capture); child.stderr!.on('data', capture)
    await expect.poll(() => status().confirmed, { timeout: 15000 }).toBe(true)
    const centre = await (await request.get('/api/opc/certificates', { headers })).json()
    const initial = await request.post(`${base}/test`, { headers, data: { area: 'OpcNode', nodeId: 'nsu=urn:scada:simulator:variables;s=Temperature', dataType: 'Float' } })
    expect(initial.ok(), await initial.text()).toBeTruthy(); expect((await initial.json()).connected).toBe(false)
    const listResponse = await request.get(certs, { headers }); expect(listResponse.ok(), await listResponse.text()).toBeTruthy(); expect(listResponse.headers()['x-scada-acquisition-scope']).toBe(`gateway:${identity.id}`)
    const list = await listResponse.json(), own = list.find((x: any) => x.store === 'own'), server = list.find((x: any) => x.store === 'rejected')
    expect(own).toBeTruthy(); expect(server).toBeTruthy(); expect(centre.filter((x: any) => x.store === 'own').some((x: any) => x.sha256 === own.sha256)).toBe(false)
    expect((await request.get(`${base}/opc/certificates?gatewayId=${identity.id + 100}`, { headers })).status()).toBe(409)
    const publicKey = await request.get(`${base}/opc/certificates/${own.sha256}/public?gatewayId=${identity.id}`, { headers }); expect(publicKey.ok()).toBeTruthy()
    expect(createHash('sha256').update(await publicKey.body()).digest('hex').toUpperCase()).toBe(own.sha256)
    const trust = await request.post(`${base}/opc/certificates/${server.sha256}/trust?gatewayId=${identity.id}`, { headers }); expect(trust.ok(), await trust.text()).toBeTruthy()
    const centreAfter = await (await request.get('/api/opc/certificates', { headers })).json()
    expect(centreAfter.filter((x: any) => x.store === 'trusted').map((x: any) => x.sha256)).toEqual(centre.filter((x: any) => x.store === 'trusted').map((x: any) => x.sha256))

    const objects = await browse({}), folder = objects.nodes.find((x: any) => x.displayName === 'Scada')
    expect(folder).toBeTruthy(); const first = await browse({ nodeId: folder.nodeId, limit: 20 }); expect(first.nodes).toHaveLength(20); expect(first.cursor).toBeTruthy()
    const next = await browse({ nodeId: folder.nodeId, limit: 20, cursor: first.cursor }); expect(next.nodes.length).toBeGreaterThan(0)
    expect(new Set([...first.nodes, ...next.nodes].map((x: any) => x.nodeId)).size).toBe(first.nodes.length + next.nodes.length)
    const all = await browse({ nodeId: folder.nodeId, limit: 200 }), temperature = all.nodes.find((x: any) => x.displayName === 'Temperature'), run = all.nodes.find((x: any) => x.displayName === 'Run')
    const userName = `other-engineer-${Date.now()}`
    await request.post('/api/users', { headers, data: { userName, password: 'test-password-123', displayName: 'Other engineer', role: 'Admin', enabled: true } })
    const other = await (await request.post('/api/auth/login', { data: { userName, password: 'test-password-123' } })).json(), otherHeaders = { Authorization: `Bearer ${other.token}` }
    expect((await request.post(`${base}/opc/browse`, { headers: otherHeaders, data: { nodeId: folder.nodeId, cursor: next.cursor, limit: 20 } })).status()).toBe(404)
    await request.delete(`${base}/opc/browse/${next.cursor}`, { headers })
    const probe = await request.post(`${base}/test`, { headers, data: { area: 'OpcNode', nodeId: temperature.nodeId, dataType: 'Float' } }); expect(probe.ok(), await probe.text()).toBeTruthy(); expect((await probe.json()).value).toBe(23.5)
    const previewResponse = await request.post(`${base}/opc/import/preview`, { headers, data: { items: [{ nodeId: temperature.nodeId, name: 'RemoteEngineeringTemperature' }, { nodeId: run.nodeId, name: 'RemoteEngineeringRun', writable: true }] } })
    expect(previewResponse.ok(), await previewResponse.text()).toBeTruthy(); const preview = await previewResponse.json(); expect(preview.canApply).toBe(true)
    const imported = await request.post(`${base}/opc/import/${preview.id}`, { headers }); expect(imported.ok(), await imported.text()).toBeTruthy(); expect(await imported.json()).toHaveLength(2)
    expect((await request.put(base, { headers, data: { ...input, enabled: true, opcPassword: null } })).ok()).toBeTruthy()
    await expect.poll(async () => { const response = await request.get(`${base}/diagnostics`, { headers }); return response.ok() ? (await response.json()).diagnostics.state : 'pending' }, { timeout: 15000 }).toBe('Online')
    const diagnostic = await (await request.get(`${base}/diagnostics?pageSize=1`, { headers })).json(); expect(diagnostic.total).toBe(2); expect(diagnostic.diagnostics.points).toHaveLength(1); expect(diagnostic.diagnostics.gatewayId).toBe(identity.id)
    const exported = await request.get(`${base}/diagnostics/export`, { headers }); expect(exported.ok(), await exported.text()).toBeTruthy(); const snapshot = await exported.json()
    expect(snapshot.points).toHaveLength(2); expect(snapshot.configurationRevision).toHaveLength(64); expect(await exported.text()).not.toContain('opc-test-password'); expect(await exported.text()).not.toContain(identity.token)
    expect((await request.post(`${base}/diagnostics/reconnect`, { headers })).status()).toBe(202)
    await expect.poll(async () => (await (await request.get(`${base}/diagnostics`, { headers })).json()).diagnostics?.events.some((x: any) => x.kind === 'ReconnectRequested')).toBe(true)

    await page.goto('/login'); await page.evaluate(data => { localStorage.setItem('scada_token', data.token); localStorage.setItem('scada_user', JSON.stringify(data.user)) }, auth); await page.goto('/config/devices')
    await page.getByRole('button', { name: 'OPC UA 证书', exact: true }).click(); const dialog = page.getByRole('dialog', { name: 'OPC UA 证书', exact: true })
    await dialog.getByRole('combobox', { name: '证书作用范围' }).press('ArrowDown'); await page.getByRole('option', { name: `网关 #${identity.id} · ${input.name}`, exact: true }).click()
    await expect(dialog.getByText(`正在操作网关 #${identity.id} 的证书库`, { exact: false })).toBeVisible()
    await dialog.getByRole('tab', { name: '客户端公钥', exact: true }).click(); await expect(dialog.getByText(own.sha256, { exact: true })).toBeVisible()
    const download = page.waitForEvent('download'); await dialog.getByRole('row').filter({ hasText: own.sha256 }).getByRole('button', { name: '下载公钥', exact: true }).click(); expect(createHash('sha256').update(readFileSync((await (await download).path())!)).digest('hex').toUpperCase()).toBe(own.sha256)
    await page.screenshot({ path: test.info().outputPath('gateway-certificate-scope.png'), fullPage: true }); await dialog.getByRole('button', { name: '关闭', exact: true }).click()
    const untrust = await request.delete(`${base}/opc/certificates/${server.sha256}/trust?gatewayId=${identity.id}`, { headers }); expect(untrust.ok(), await untrust.text()).toBeTruthy()
    await expect.poll(async () => { const response = await request.get(`${base}/diagnostics`, { headers }); return response.ok() ? (await response.json()).diagnostics.state : 'pending' }, { timeout: 15000 }).toBe('Offline')
    const alarms = async () => (await (await request.get('/api/alarms?limit=5000', { headers })).json()).filter((x: any) => x.deviceId === device.id && x.gatewayId === identity.id)
    await expect.poll(async () => (await alarms()).filter((x: any) => x.endTs === null).length, { timeout: 15000 }).toBe(1)
    const alarm = (await alarms())[0]; expect((await request.post(`/api/alarms/${alarm.id}/ack`, { headers })).ok()).toBeTruthy()
    expect((await request.post(`${base}/opc/certificates/${server.sha256}/trust?gatewayId=${identity.id}`, { headers })).ok()).toBeTruthy()
    await expect.poll(async () => (await alarms()).find((x: any) => x.id === alarm.id)?.endReason, { timeout: 15000 }).toContain('通信恢复')
    expect((await alarms()).filter((x: any) => x.id === alarm.id)[0].ackUser).toBe('admin')
    expect(log).not.toContain(identity.token); expect(log).not.toContain('opc-test-password')
  } catch (error) {
    await test.info().attach('engineering-process', { body: JSON.stringify({ status: status(), log: log.replaceAll(identity.token, '[redacted]').replaceAll('opc-test-password', '[redacted]') }, null, 2), contentType: 'application/json' }); throw error
  } finally {
    if (child && child.exitCode === null && child.signalCode === null) { const exited = once(child, 'exit'); child.kill(); await exited }
    if (resolve(root).startsWith(resolve(tmpdir()) + sep) && root.split(sep).at(-1)!.startsWith('scada-engineering-e2e-')) rmSync(root, { recursive: true, force: true })
  }
})
