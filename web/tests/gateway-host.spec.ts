import { test, expect } from './fixtures'
import { spawn, type ChildProcess } from 'node:child_process'
import { createServer, request as forward } from 'node:http'
import { once } from 'node:events'
import { mkdtempSync, readFileSync, rmSync } from 'node:fs'
import { join, resolve, sep } from 'node:path'
import { tmpdir } from 'node:os'

// This uses a separate executable and the real OPC UA simulator. Only its center link is interrupted.
test('standalone gateway collects, survives offline restart, backfills history and remembers revocation', async ({ request }) => {
  test.setTimeout(90000)
  const root = mkdtempSync(join(tmpdir(), 'scada-gateway-e2e-'))
  const processes: ChildProcess[] = [], logs: string[] = [], responses: string[] = []
  let offline = false
  const proxy = createServer((req, res) => {
    if (offline) { res.writeHead(503); res.end(); return }
    const upstream = forward({ hostname: '127.0.0.1', port: 18581, path: req.url, method: req.method, headers: req.headers }, reply => {
      responses.push(`${req.url}: ${reply.statusCode}`); if (responses.length > 100) responses.shift()
      res.writeHead(reply.statusCode!, reply.headers); reply.pipe(res)
    })
    upstream.on('error', () => { if (!res.headersSent) res.writeHead(503); res.end() })
    req.on('aborted', () => upstream.destroy()); res.on('close', () => upstream.destroy()); req.pipe(upstream)
  })
  proxy.listen(0, '127.0.0.1'); await once(proxy, 'listening')
  const address = proxy.address() as { port: number }
  const session = await (await request.post('/api/auth/login', { data: { userName: 'admin', password: 'admin123' } })).json()
  const headers = { Authorization: `Bearer ${session.token}` }
  const identity = await (await request.post('/api/gateways', { headers, data: { name: `Process-${Date.now()}` } })).json()
  function start() {
    const child = spawn('dotnet', [resolve('../src/Scada.Gateway.Host/bin/Release/net10.0/Scada.Gateway.Host.dll')], {
      windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'], env: { ...process.env,
        GatewayAgent__ServerUrl: `http://127.0.0.1:${address.port}/`, GatewayAgent__GatewayId: String(identity.id), GatewayAgent__Token: identity.token,
        GatewayAgent__DataRoot: root, GatewayAgent__AllowLoopbackHttp: 'true', GatewayAgent__HttpTimeoutMs: '1000',
        GatewayAgent__HeartbeatIntervalMs: '500', GatewayAgent__ConfigurationPollMs: '500', GatewayAgent__RetryDelayMs: '200', GatewayAgent__MaxPendingBytes: '16384' }
    })
    let log = ''; const index = logs.push('') - 1
    const capture = (data: Buffer) => { log = (log + String(data)).slice(-16384); logs[index] = log }
    child.stdout!.on('data', capture); child.stderr!.on('data', capture); processes.push(child); return child
  }
  async function stop(child: ChildProcess) {
    if (child.exitCode !== null || child.signalCode !== null) return
    const exited = once(child, 'exit'); child.kill(); await exited
  }
  function status(): any { try { return JSON.parse(readFileSync(join(root, 'status.json'), 'utf8')) } catch { return {} } }
  async function gateway() { return (await request.get(`/api/gateways/${identity.id}`, { headers })).json() }
  try {
    const body = { name: `ProcessDevice-${Date.now()}`, protocol: 'OpcUa', host: 'localhost', port: 4840, stationNumber: 1, byteOrder: 'ABCD', pollIntervalMs: 200,
      timeoutMs: 3000, enabled: false, opcEndpointUrl: 'opc.tcp://localhost:18583/ScadaSimulator', opcSecurityMode: 'None', opcSecurityPolicy: 'None', opcUserName: '', opcPassword: 'agent-cache-secret' }
    const device = await (await request.post('/api/devices', { headers, data: body })).json()
    const browse = async (data: object) => (await request.post(`/api/devices/${device.id}/opc/browse`, { headers, data })).json()
    const folder = (await browse({})).nodes.find((x: any) => x.displayName === 'Scada')
    const temperature = (await browse({ nodeId: folder.nodeId, limit: 200 })).nodes.find((x: any) => x.displayName === 'Temperature')
    const tagResponse = await request.post('/api/tags', { headers, data: { deviceId: device.id, name: 'ProcessTemperature', area: 'OpcNode', nodeId: temperature.nodeId,
      address: 0, dataType: 'Float', scale: 2, offset: 0, writable: false, archiveMode: 'Periodic', archivePeriodSec: 1, deadband: 0 } })
    expect(tagResponse.ok(), await tagResponse.text()).toBeTruthy(); const tag = await tagResponse.json()
    expect((await request.post(`/api/gateways/assignments/${device.id}`, { headers, data: { gatewayId: identity.id } })).ok()).toBeTruthy()
    expect((await request.put(`/api/devices/${device.id}`, { headers, data: { ...body, enabled: true, opcPassword: null } })).ok()).toBeTruthy()
    let agent = start()
    await expect.poll(async () => (await (await request.get(`/api/realtime?tagIds=${tag.id}`, { headers })).json())[0]?.value, { timeout: 20000,
      message: 'Standalone process must return scaled OPC value through the center' }).toBe(47)
    await expect.poll(() => status().confirmed).toBe(true)
    const initial = status(); expect(initial.state).toBe('Running'); expect(initial.streamId).toBeTruthy()
    expect(readFileSync(join(root, 'gateway.db')).includes(Buffer.from('agent-cache-secret'))).toBe(false)
    // The real OS lease rejects another process using this installation.
    const duplicate = start(); await once(duplicate, 'exit'); expect(duplicate.exitCode).toBe(1)
    expect(agent.exitCode).toBeNull()

    offline = true; const offlineFrom = Date.now()
    await expect.poll(() => status().state).toBe('OfflineCollecting')
    await expect.poll(() => status().pendingBatches).toBeGreaterThanOrEqual(6)
    const beforeCrash = status(); await stop(agent)
    agent = start()
    await expect.poll(() => status().nextSequence, { timeout: 15000 }).toBeGreaterThan(beforeCrash.nextSequence + 5)
    expect(status().streamId).toBe(initial.streamId); expect(status().pendingBatches).toBeGreaterThan(beforeCrash.pendingBatches)
    await expect.poll(() => status().state, { timeout: 20000 }).toBe('AcquisitionPaused')
    const full = status(); expect(full.pendingBytes).toBeLessThanOrEqual(16384)
    await expect.poll(() => status().updatedTs).toBeGreaterThan(full.updatedTs + 1000)
    expect(status().nextSequence).toBe(full.nextSequence)
    // A crash keeps its old center lease; only the test's administrator releases that old session.
    expect((await request.post(`/api/gateways/${identity.id}/release-session`, { headers })).ok()).toBeTruthy()
    const offlineTo = Date.now(); offline = false
    await expect.poll(() => status(), { timeout: 15000 }).toMatchObject({ state: 'Running' })
    await expect.poll(() => status().pendingBatches, { timeout: 15000 }).toBeLessThanOrEqual(2)
    await expect.poll(async () => {
      const history = await (await request.get(`/api/history?tagIds=${tag.id}&from=${offlineFrom}&to=${offlineTo}`, { headers })).json()
      return history.series[0]?.points?.length ?? 0
    }, { timeout: 10000 }).toBeGreaterThanOrEqual(2)
    expect(status().streamId).toBe(initial.streamId); expect(status().lastAcknowledged).toBeGreaterThan(beforeCrash.lastAcknowledged)

    const previousRevision = (await gateway()).appliedRevision
    expect((await request.put(`/api/devices/${device.id}`, { headers, data: { ...body, enabled: false, opcPassword: null } })).ok()).toBeTruthy()
    await expect.poll(async () => (await gateway()).appliedRevision).not.toBe(previousRevision)
    await expect.poll(() => status().devices?.length).toBe(0)
    // 迁移设备是最终一致的：除了设备要停用并完全退出，服务端还要求原网关确认到停用配置
    // （AssignmentBlockAsync 会返回 409）。负载高时这一步要等几秒，必须轮询而不是一次性断言。
    await expect.poll(async () =>
      (await request.post(`/api/gateways/assignments/${device.id}`, { headers, data: { gatewayId: null } })).ok(),
      { timeout: 45000, message: '解除网关归属应最终成功（原网关需先确认停用配置）' }
    ).toBe(true)

    // Re-enable actual acquisition before revoking the identity.
    expect((await request.post(`/api/gateways/assignments/${device.id}`, { headers, data: { gatewayId: identity.id } })).ok()).toBeTruthy()
    expect((await request.put(`/api/devices/${device.id}`, { headers, data: { ...body, enabled: true, opcPassword: null } })).ok()).toBeTruthy()
    await expect.poll(() => status().devices?.[0]?.online).toBe(true)

    expect((await request.put(`/api/gateways/${identity.id}`, { headers, data: { name: `RevokedProcess-${Date.now()}`, enabled: false } })).ok()).toBeTruthy()
    await expect.poll(() => status().state).toBe('Blocked')
    const stoppedSequence = status().nextSequence
    await stop(agent); offline = true
    agent = start()
    await expect.poll(() => status().updatedTs).toBeGreaterThan(offlineTo)
    // Give startup a complete failed handshake; persisted revocation must prevent cached startup.
    const observed = status().updatedTs
    await expect.poll(() => status().updatedTs).toBeGreaterThan(observed + 1000)
    expect(status().state).toBe('Blocked'); expect(status().revision).toBeNull(); expect(status().devices).toEqual([])
    expect(status().nextSequence).toBe(stoppedSequence)
    for (const log of logs) { expect(log).not.toContain(identity.token); expect(log).not.toContain('agent-cache-secret') }
  } catch (error) {
    await test.info().attach('gateway-status', { body: JSON.stringify(status(), null, 2), contentType: 'application/json' })
    await test.info().attach('gateway-link', { body: responses.join('\n'), contentType: 'text/plain' })
    await test.info().attach('gateway-processes', { body: logs.join('\n').replaceAll(identity.token, '[redacted]').replaceAll('agent-cache-secret', '[redacted]'), contentType: 'text/plain' })
    throw error
  } finally {
    for (const child of processes) await stop(child)
    proxy.closeAllConnections(); await new Promise<void>(done => proxy.close(() => done()))
    if (resolve(root).startsWith(resolve(tmpdir()) + sep) && root.split(sep).at(-1)!.startsWith('scada-gateway-e2e-')) rmSync(root, { recursive: true, force: true })
  }
})
