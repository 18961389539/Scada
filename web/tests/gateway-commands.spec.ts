import { test, expect } from '@playwright/test'
import { randomUUID } from 'node:crypto'
import { spawn, type ChildProcess } from 'node:child_process'
import { createServer as httpServer, request as forward } from 'node:http'
import { createServer as tcpServer, type Socket } from 'node:net'
import { once } from 'node:events'
import { mkdtempSync, readFileSync, rmSync } from 'node:fs'
import { join, resolve, sep } from 'node:path'
import { tmpdir } from 'node:os'

test('remote commands execute once on the real TCP link and reconcile lost authorizations, receipts and process crashes', async ({ request, page }) => {
  test.setTimeout(90000)
  const root = mkdtempSync(join(tmpdir(), 'scada-command-e2e-')), sockets = new Set<Socket>(), processes: ChildProcess[] = [], logs: string[] = []
  let value = 5, writes = 0, loseWriteAck = false, blockReadsAfterWrite = false, blockedReads = false, offline = false, loseAuthorization = false, loseReceipt = false
  const modbus = tcpServer(socket => {
    sockets.add(socket); socket.on('close', () => sockets.delete(socket)); socket.on('error', () => {})
    let pending = Buffer.alloc(0)
    socket.on('data', chunk => {
      pending = Buffer.concat([pending, chunk])
      while (pending.length >= 7 && pending.length >= 6 + pending.readUInt16BE(4)) {
        const size = 6 + pending.readUInt16BE(4), frame = pending.subarray(0, size); pending = pending.subarray(size)
        const fn = frame[7]; let pdu: Buffer
        if (fn === 3) {
          if (blockedReads) continue
          const count = frame.readUInt16BE(10); pdu = Buffer.alloc(2 + count * 2); pdu[0] = 3; pdu[1] = count * 2
          for (let n = 0; n < count; n++) pdu.writeUInt16BE(value, 2 + n * 2)
        } else if (fn === 16) {
          writes++; value = frame.readUInt16BE(13)
          if (loseWriteAck) { socket.destroy(); continue }
          pdu = Buffer.from(frame.subarray(7, 12)); if (blockReadsAfterWrite) blockedReads = true
        } else { pdu = Buffer.from([fn! | 0x80, 1]) }
        const header = Buffer.from(frame.subarray(0, 7)); header.writeUInt16BE(pdu.length + 1, 4); socket.write(Buffer.concat([header, pdu]))
      }
    })
  })
  modbus.listen(0, '127.0.0.1'); await once(modbus, 'listening')
  const plcPort = (modbus.address() as { port: number }).port
  const proxy = httpServer((req, res) => {
    if (offline) { res.writeHead(503); res.end(); return }
    const upstream = forward({ hostname: '127.0.0.1', port: 18581, path: req.url, method: req.method, headers: req.headers }, reply => {
      if (reply.statusCode === 200 && (loseAuthorization && req.url?.endsWith('/authorize') || loseReceipt && req.url === '/api/gateway-agent/command-results')) {
        if (!req.url?.endsWith('/authorize')) loseReceipt = false
        reply.resume(); res.destroy(); return
      }
      res.writeHead(reply.statusCode!, reply.headers); reply.pipe(res)
    })
    upstream.on('error', () => { if (!res.headersSent) res.writeHead(503); res.end() }); req.pipe(upstream); res.on('close', () => upstream.destroy())
  })
  proxy.listen(0, '127.0.0.1'); await once(proxy, 'listening'); const port = (proxy.address() as { port: number }).port
  const auth = await (await request.post('/api/auth/login', { data: { userName: 'admin', password: 'admin123' } })).json(), headers = { Authorization: `Bearer ${auth.token}` }
  const identity = await (await request.post('/api/gateways', { headers, data: { name: `CommandProcess-${Date.now()}` } })).json()
  function start() {
    const child = spawn('dotnet', [resolve('../src/Scada.Gateway.Host/bin/Release/net10.0/Scada.Gateway.Host.dll')], { windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'], env: { ...process.env,
      GatewayAgent__ServerUrl: `http://127.0.0.1:${port}/`, GatewayAgent__GatewayId: String(identity.id), GatewayAgent__Token: identity.token, GatewayAgent__DataRoot: root,
      GatewayAgent__AllowLoopbackHttp: 'true', GatewayAgent__HttpTimeoutMs: '1000', GatewayAgent__HeartbeatIntervalMs: '500', GatewayAgent__ConfigurationPollMs: '500', GatewayAgent__RetryDelayMs: '200' } })
    const index = logs.push('') - 1; const capture = (chunk: Buffer) => { logs[index] = (logs[index] + String(chunk)).slice(-16000) }
    child.stdout!.on('data', capture); child.stderr!.on('data', capture); processes.push(child); return child
  }
  async function stop(child: ChildProcess) { if (child.exitCode === null && child.signalCode === null) { const done = once(child, 'exit'); child.kill(); await done } }
  function status(): any { try { return JSON.parse(readFileSync(join(root, 'status.json'), 'utf8')) } catch { return {} } }
  async function write(tagId: number, target: number, id = randomUUID()) {
    const response = await request.post('/api/commands', { headers, data: { tagId, value: target, requestId: id } }); expect(response.ok()).toBeTruthy(); return response.json()
  }
  try {
    const body = { name: `CommandDevice-${Date.now()}`, protocol: 'ModbusTcp', host: '127.0.0.1', port: plcPort, stationNumber: 1, byteOrder: 'ABCD', pollIntervalMs: 200, timeoutMs: 500, enabled: false }
    const device = await (await request.post('/api/devices', { headers, data: body })).json()
    const tagResponse = await request.post('/api/tags', { headers, data: { deviceId: device.id, name: 'RemoteSetpoint', area: 'HoldingRegister', address: 0, dataType: 'UInt16',
      scale: 2, offset: 1, writable: true, minValue: 1, maxValue: 1000, archiveMode: 'None', archivePeriodSec: 600, deadband: 0 } })
    expect(tagResponse.ok(), await tagResponse.text()).toBeTruthy(); const tag = await tagResponse.json()
    expect((await request.post(`/api/gateways/assignments/${device.id}`, { headers, data: { gatewayId: identity.id } })).ok()).toBeTruthy()
    expect((await request.put(`/api/devices/${device.id}`, { headers, data: { ...body, enabled: true } })).ok()).toBeTruthy()
    let agent = start(); await expect.poll(() => status().devices?.[0]?.online, { timeout: 15000 }).toBe(true)
    const id = randomUUID(); loseReceipt = true
    const first = await write(tag.id, 61, id); expect(first.success).toBe(true); expect(first.readBack).toBe(61); expect(first.commandId).toBe(id); expect(first.outcome).toBe('Confirmed')
    expect(value).toBe(30); expect(writes).toBe(1)
    const duplicate = await write(tag.id, 61, id); expect(duplicate.commandId).toBe(id); expect(duplicate.success).toBe(true); expect(writes).toBe(1)
    expect((await write(tag.id, 63, id)).success).toBe(false); expect(writes).toBe(1)
    const query = await request.get(`/api/commands/${id}`, { headers }); expect(query.ok()).toBeTruthy(); expect((await query.json()).result.outcome).toBe('Confirmed')
    expect(await query.text()).not.toContain('executionToken'); expect(await query.text()).not.toContain('sessionId')

    await page.goto('/login')
    await page.evaluate(data => { localStorage.setItem('scada_token', data.token); localStorage.setItem('scada_user', JSON.stringify(data.user)) }, auth)
    await page.goto('/realtime')
    const row = page.getByRole('row').filter({ hasText: body.name }).filter({ hasText: 'RemoteSetpoint' }); await row.getByRole('button', { name: '写值', exact: true }).click()
    await page.getByRole('dialog').getByRole('spinbutton').fill('63')
    await page.getByRole('dialog').getByRole('button', { name: '下发', exact: true }).click()
    await expect(page.getByRole('dialog')).not.toBeVisible()
    await expect(page.getByText('写入及回读已确认', { exact: false })).toBeVisible(); expect(writes).toBe(2)
    await page.getByRole('button', { name: '查询回执' }).click()
    await expect(page.getByText('回读值：63')).toBeVisible()
    await page.screenshot({ path: test.info().outputPath('remote-command-receipt.png'), fullPage: true })

    const screen = await (await request.post('/api/screens', { headers, data: { name: `Remote command panel ${Date.now()}` } })).json()
    const document = { schemaVersion: 1, width: 1200, height: 720, background: '#101b2d', nodes: [{ id: 'remote-control', type: 'pump', x: 60, y: 60, width: 140, height: 112,
      rotation: 0, text: '远程控制', color: '#7394b8', activeColor: '#34d399', unit: '', decimals: 1, min: 0, max: 100, activeThreshold: .5, tagId: tag.id,
      action: { type: 'command', tagId: tag.id, screenId: null, value: 61 } }] }
    const saved = await (await request.put(`/api/screens/${screen.id}`, { headers, data: { name: 'Remote command panel', revision: screen.revision, document } })).json()
    const published = await (await request.post(`/api/screens/${screen.id}/publish`, { headers, data: { revision: saved.revision } })).json()
    const action = await request.post(`/api/screens/${screen.id}/actions/remote-control`, { headers, data: { publishedRevision: published.publishedRevision, requestId: id } })
    expect(action.ok(), await action.text()).toBeTruthy(); expect((await action.json()).commandId).toBe(id); expect(writes).toBe(2)

    loseAuthorization = true
    const lostGrant = await write(tag.id, 65); loseAuthorization = false; expect(lostGrant.success).toBe(false); expect(lostGrant.outcome).toBe('NotSent'); expect(writes).toBe(2)

    offline = true; const expired = await write(tag.id, 67); expect(expired.outcome).toBe('NotSent'); offline = false
    await expect.poll(() => status().state).toBe('Running')
    await expect.poll(() => status().devices?.[0]?.online).toBe(true); expect(writes).toBe(2)

    loseWriteAck = true
    const lostPlcAck = await write(tag.id, 69); expect(lostPlcAck.success).toBe(false); expect(lostPlcAck.outcome).toBe('Unknown'); expect(writes).toBe(3)
    loseWriteAck = false; const recoveredAfter = Date.now()
    await expect.poll(() => status().devices?.[0]?.online, { timeout: 15000 }).toBe(true)
    await expect.poll(async () => {
      const centre = await (await request.get('/api/health')).json(), deviceStatus = centre.devices.find((x: any) => x.deviceId === device.id)
      return deviceStatus?.online && deviceStatus.lastPollTs >= recoveredAfter
    }, { timeout: 15000 }).toBe(true)

    blockReadsAfterWrite = true; const crashId = randomUUID(), pending = write(tag.id, 71, crashId)
    await expect.poll(() => writes).toBe(4); await stop(agent)
    const unknown = await pending; expect(unknown.outcome).toBe('Unknown')
    blockReadsAfterWrite = blockedReads = false
    expect((await request.post(`/api/gateways/${identity.id}/release-session`, { headers })).ok()).toBeTruthy(); agent = start()
    await expect.poll(async () => (await (await request.get(`/api/commands/${crashId}`, { headers })).json()).result?.message, { timeout: 15000 }).toContain('重启')
    await expect.poll(() => status().devices?.[0]?.online).toBe(true); expect(writes).toBe(4)
    expect((await write(tag.id, 71, crashId)).outcome).toBe('Unknown'); expect(writes).toBe(4)
    for (const log of logs) expect(log).not.toContain(identity.token)
  } catch (error) {
    await test.info().attach('command-process-status', { body: JSON.stringify({ status: status(), writes, value, processes: processes.map(x => ({ pid: x.pid, exitCode: x.exitCode, signalCode: x.signalCode })), logs: logs.map(x => x.replaceAll(identity.token, '[redacted]')) }, null, 2), contentType: 'application/json' }); throw error
  } finally {
    for (const child of processes) await stop(child)
    proxy.closeAllConnections(); await new Promise<void>(done => proxy.close(() => done())); for (const socket of sockets) socket.destroy(); await new Promise<void>(done => modbus.close(() => done()))
    if (resolve(root).startsWith(resolve(tmpdir()) + sep) && root.split(sep).at(-1)!.startsWith('scada-command-e2e-')) rmSync(root, { recursive: true, force: true })
  }
})
