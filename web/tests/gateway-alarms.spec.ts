import { test, expect } from '@playwright/test'
import { randomUUID } from 'node:crypto'

test('gateway lease loss raises a durable device alarm; fresh-session recovery and administrative closure keep distinct reasons', async ({ request, page }) => {
  test.setTimeout(90000)
  const auth = await (await request.post('/api/auth/login', { data: { userName: 'admin', password: 'admin123' } })).json(), headers = { Authorization: `Bearer ${auth.token}` }
  const identity = await (await request.post('/api/gateways', { headers, data: { name: `AlarmGateway-${Date.now()}` } })).json()
  const agent = { Authorization: `Gateway ${identity.id}:${identity.token}` }, streamId = randomUUID()
  const input = { name: `AlarmDevice-${Date.now()}`, protocol: 'ModbusTcp', host: '127.0.0.1', port: 16542, stationNumber: 1, byteOrder: 'ABCD', pollIntervalMs: 200, timeoutMs: 500, enabled: false }
  const device = await (await request.post('/api/devices', { headers, data: input })).json()
  const tag = await request.post('/api/tags', { headers, data: { deviceId: device.id, name: 'AlarmPoint', area: 'HoldingRegister', address: 0, dataType: 'UInt16',
    scale: 1, offset: 0, writable: false, archiveMode: 'None', archivePeriodSec: 600, deadband: 0 } }); expect(tag.ok(), await tag.text()).toBeTruthy()
  expect((await request.post(`/api/gateways/assignments/${device.id}`, { headers, data: { gatewayId: identity.id } })).ok()).toBeTruthy()
  expect((await request.put(`/api/devices/${device.id}`, { headers, data: { ...input, enabled: true } })).ok()).toBeTruthy()
  async function hello() {
    const response = await request.post('/api/gateway-agent/hello', { headers: agent, data: { version: 1, instanceId: randomUUID(), streamId, agentVersion: 'alarm-integration' } }); expect(response.ok(), await response.text()).toBeTruthy()
    const session = await response.json(); return { ...agent, 'X-Scada-Session': session.sessionId }
  }
  async function configuration(caller: Record<string, string>) { const response = await request.get('/api/gateway-agent/configuration', { headers: caller }); expect(response.ok()).toBeTruthy(); return response.json() }
  async function report(caller: Record<string, string>, revision: string, online?: boolean) {
    return request.post('/api/gateway-agent/heartbeat', { headers: caller, data: { appliedRevision: revision, pendingBatches: 0, pendingBytes: 0,
      devices: online === undefined ? [] : [{ deviceId: device.id, name: input.name, online, error: online ? null : 'simulated-link-failure', lastPollTs: Date.now(), statusTs: Date.now(), lastCycleMs: 1, tagCount: 1, reconnectAttempts: 0, commFailures: online ? 0 : 1 }] } })
  }
  const alarms = async () => (await (await request.get('/api/alarms?limit=5000', { headers })).json()).filter((x: any) => x.deviceId === device.id && x.gatewayId === identity.id)
  const initial = await hello(), config = await configuration(initial); expect((await report(initial, config.revision, true)).ok()).toBeTruthy()
  expect(await alarms()).toHaveLength(0)
  await expect.poll(async () => (await alarms()).filter((x: any) => x.endTs === null).length, { timeout: 40000, intervals: [250, 500, 1000] }).toBe(1)
  const lost = (await alarms())[0]; expect(lost.message).toContain('租约'); expect(lost.endReason).toBeNull()
  await page.goto('/login'); await page.evaluate(data => { localStorage.setItem('scada_token', data.token); localStorage.setItem('scada_user', JSON.stringify(data.user)) }, auth); await page.goto('/alarms')
  const row = page.getByRole('row').filter({ hasText: input.name }); await expect(row).toContainText(`网关 #${identity.id}`)
  await row.getByRole('button', { name: '确认', exact: true }).click(); await expect.poll(async () => (await alarms())[0].ackUser).toBe('admin')
  await page.screenshot({ path: test.info().outputPath('remote-device-offline.png'), fullPage: true })
  const replacement = await hello(), current = await configuration(replacement)
  expect((await report(initial, config.revision, true)).status()).toBe(409)
  expect((await report(replacement, current.revision)).ok()).toBeTruthy(); expect((await alarms())[0].endTs).toBeNull()
  expect((await report(replacement, current.revision, true)).ok()).toBeTruthy()
  await expect.poll(async () => (await alarms()).find((x: any) => x.id === lost.id)?.endReason).toContain('通信恢复')
  expect((await alarms()).find((x: any) => x.id === lost.id).ackUser).toBe('admin'); await expect(row).not.toBeVisible()
  expect((await report(replacement, current.revision, false)).ok()).toBeTruthy()
  await expect.poll(async () => (await alarms()).filter((x: any) => x.endTs === null).length, { timeout: 10000 }).toBe(1)
  const second = (await alarms()).find((x: any) => x.endTs === null); expect(second.id).not.toBe(lost.id)
  expect((await request.put(`/api/devices/${device.id}`, { headers, data: input })).ok()).toBeTruthy()
  await expect.poll(async () => (await alarms()).find((x: any) => x.id === second.id)?.endReason).toContain('采集已停用')
  await page.getByRole('tab', { name: '历史报警', exact: true }).click(); await page.getByPlaceholder('按内容过滤').fill(input.name)
  await expect(page.getByRole('row').filter({ hasText: input.name })).toHaveCount(2)
  await expect(page.getByText('当前网关已确认设备通信恢复', { exact: true })).toBeVisible(); await expect(page.getByText('设备采集已停用', { exact: true })).toBeVisible()
  await page.screenshot({ path: test.info().outputPath('remote-device-alarm-history.png'), fullPage: true })
})
