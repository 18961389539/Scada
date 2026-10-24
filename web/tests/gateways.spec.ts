import { test, expect, type APIRequestContext } from '@playwright/test'
import { createHash, randomUUID } from 'node:crypto'

async function admin(request: APIRequestContext) {
  const session = await (await request.post('/api/auth/login', { data: { userName: 'admin', password: 'admin123' } })).json()
  return { Authorization: `Bearer ${session.token}` }
}
async function gateway(request: APIRequestContext, adminHeaders: Record<string, string>, suffix: string) {
  const response = await request.post('/api/gateways', { headers: adminHeaders, data: { name: `Gateway-${suffix}` } })
  expect(response.status(), await response.text()).toBe(201); const identity = await response.json()
  expect((await request.get(response.headers().location!, { headers: adminHeaders })).status()).toBe(200)
  return { ...identity, headers: { Authorization: `Gateway ${identity.id}:${identity.token}` } }
}
async function hello(request: APIRequestContext, headers: Record<string, string>, streamId = randomUUID(), instanceId = randomUUID()) {
  const input = { version: 1, streamId, instanceId, agentVersion: 'integration-test' }
  const response = await request.post('/api/gateway-agent/hello', { headers, data: input }); expect(response.ok(), await response.text()).toBeTruthy()
  const session = await response.json(); return { ...session, streamId, instanceId, headers: { ...headers, 'X-Scada-Session': session.sessionId } }
}
function deviceInput(name: string, enabled = false) {
  return { name, protocol: 'OpcUa', host: '', port: 502, stationNumber: 1, byteOrder: 'ABCD', pollIntervalMs: 1000, timeoutMs: 3000, enabled,
    opcEndpointUrl: 'opc.tcp://unreachable-edge-only:4840', opcSecurityMode: 'SignAndEncrypt', opcSecurityPolicy: 'Basic256Sha256', opcUserName: 'operator', opcPassword: 'remote-config-private-password' }
}
async function postPacket(request: APIRequestContext, headers: Record<string, string>, packet: object) {
  const text = JSON.stringify(packet), hash = createHash('sha256').update(text).digest('hex')
  return request.post('/api/gateway-agent/telemetry', { headers: { ...headers, 'Content-Type': 'application/json', 'X-Scada-Payload-Sha256': hash }, data: text })
}

test('gateway identities are isolated, sessions are exclusive and idempotent, rotation revokes old credentials', async ({ request }) => {
  const adminHeaders = await admin(request), identity = await gateway(request, adminHeaders, `Auth-${Date.now()}`)
  const first = await hello(request, identity.headers)
  const repeated = await hello(request, identity.headers, first.streamId, first.instanceId); expect(repeated.sessionId).toBe(first.sessionId)
  expect((await request.post('/api/gateway-agent/hello', { headers: identity.headers, data: { version: 1, streamId: first.streamId, instanceId: randomUUID(), agentVersion: 'other-process' } })).status()).toBe(409)
  expect((await request.get('/api/gateway-agent/configuration', { headers: adminHeaders })).status()).toBe(401)
  expect((await request.get('/api/devices', { headers: identity.headers })).status()).toBe(401)
  expect((await request.get('/api/gateways', { headers: identity.headers })).status()).toBe(401)
  const list = await request.get('/api/gateways', { headers: adminHeaders }); const listed = await list.text()
  expect(listed).not.toContain(identity.token); expect(listed).not.toContain('tokenHash'); expect(listed).not.toContain('sessionId'); expect(list.headers()['cache-control']).toBe('no-store')
  expect((await request.post(`/api/gateways/${identity.id}/release-session`, { headers: adminHeaders })).ok()).toBeTruthy()
  expect((await request.post('/api/gateway-agent/heartbeat', { headers: first.headers, data: { appliedRevision: null, pendingBatches: 0, pendingBytes: 0 } })).status()).toBe(409)
  const replacement = await hello(request, identity.headers); expect(replacement.sessionId).not.toBe(first.sessionId)
  const rotation = await request.post(`/api/gateways/${identity.id}/rotate-key`, { headers: adminHeaders }); const next = await rotation.json(); expect(next.token).not.toBe(identity.token)
  expect((await request.get('/api/gateway-agent/configuration', { headers: replacement.headers })).status()).toBe(401)
  const rotated = { Authorization: `Gateway ${identity.id}:${next.token}` }; const restarted = await hello(request, rotated)
  expect((await request.put(`/api/gateways/${identity.id}`, { headers: adminHeaders, data: { name: `Disabled-Gateway-${Date.now()}`, enabled: false } })).ok()).toBeTruthy()
  expect((await request.get('/api/gateway-agent/configuration', { headers: restarted.headers })).status()).toBe(401)
})

test('assigned device telemetry is persisted, deduplicated and backfilled without replacing current values', async ({ request }) => {
  const adminHeaders = await admin(request), identity = await gateway(request, adminHeaders, `Data-${Date.now()}`)
  const body = deviceInput(`Remote-${Date.now()}`)
  const created = await request.post('/api/devices', { headers: adminHeaders, data: body }); expect(created.ok()).toBeTruthy(); const device = await created.json()
  const tagResponse = await request.post('/api/tags', { headers: adminHeaders, data: { deviceId: device.id, name: 'RemoteValue', area: 'OpcNode', nodeId: 'nsu=urn:edge;s=Temperature', address: 0, dataType: 'Float', scale: 1, offset: 0, writable: true, archiveMode: 'Periodic', archivePeriodSec: 600, deadband: .1 } })
  expect(tagResponse.ok(), await tagResponse.text()).toBeTruthy(); const tag = await tagResponse.json()
  expect((await request.post(`/api/gateways/assignments/${device.id}`, { headers: adminHeaders, data: { gatewayId: identity.id } })).ok()).toBeTruthy()
  expect((await request.put(`/api/devices/${device.id}`, { headers: adminHeaders, data: { ...body, enabled: true, opcPassword: null } })).ok()).toBeTruthy()
  const session = await hello(request, identity.headers)
  const configResponse = await request.get('/api/gateway-agent/configuration', { headers: session.headers }); const config = await configResponse.json()
  expect(config.devices).toHaveLength(1); expect(config.devices[0].device.id).toBe(device.id); expect(config.devices[0].opcPassword).toBe(body.opcPassword); expect(config.tags[0].id).toBe(tag.id)
  expect(JSON.stringify(config)).not.toContain('opcPasswordProtected'); expect(configResponse.headers()['cache-control']).toBe('no-store')
  expect(configResponse.headers().etag).toBe(`"${config.revision}"`)
  const unchanged = await request.get('/api/gateway-agent/configuration', { headers: { ...session.headers, 'If-None-Match': `"${config.revision}"` } })
  expect(unchanged.status()).toBe(304); expect(await unchanged.text()).toBe('')
  expect((await request.post('/api/gateway-agent/heartbeat', { headers: session.headers, data: { appliedRevision: config.revision, pendingBatches: 2, pendingBytes: 1024 } })).ok()).toBeTruthy()
  const ts = Date.now(), base = { version: 1, streamId: session.streamId, deviceId: device.id, configurationRevision: config.revision, createdTs: ts }
  const first = { ...base, sequence: 1, values: [{ tagId: tag.id, value: 12.5, quality: 'Good', ts, sourceTs: ts - 1 }] }
  expect((await postPacket(request, session.headers, first)).ok()).toBeTruthy()
  await expect.poll(async () => (await (await request.get(`/api/realtime?tagIds=${tag.id}`, { headers: adminHeaders })).json())[0]?.value).toBe(12.5)
  expect((await postPacket(request, session.headers, first)).ok()).toBeTruthy()
  const old = { ...base, sequence: 2, values: [{ tagId: tag.id, value: 999, quality: 'Good', ts: ts - 3600000 }] }
  expect((await postPacket(request, session.headers, old)).ok()).toBeTruthy()
  const query = `/api/history?tagIds=${tag.id}&from=${ts - 3601000}&to=${Date.now() + 5000}`
  await expect.poll(async () => (await (await request.get(query, { headers: adminHeaders })).json()).series[0]?.points?.length).toBe(2)
  expect((await (await request.get(`/api/realtime?tagIds=${tag.id}`, { headers: adminHeaders })).json())[0].value).toBe(12.5)
  expect((await postPacket(request, session.headers, { ...old, values: [{ tagId: tag.id, value: 88, quality: 'Good', ts }] })).status()).toBe(409)
  expect((await postPacket(request, session.headers, { ...base, sequence: 4, values: first.values })).status()).toBe(409)
  expect((await postPacket(request, session.headers, { ...base, sequence: 3, values: [{ ...first.values[0], tagId: 2147483647 }] })).status()).toBe(403)
  const filtered = { ...base, sequence: 3, values: [{ tagId: tag.id, value: 12.55, quality: 'Good', ts: Date.now() }] }
  expect((await postPacket(request, session.headers, filtered)).ok()).toBeTruthy()
  await expect.poll(async () => (await (await request.get(`/api/realtime?tagIds=${tag.id}`, { headers: adminHeaders })).json())[0]?.value).toBe(12.55)
  const history = await (await request.get(query, { headers: adminHeaders })).json(); expect(history.series[0].points).toHaveLength(2)
  const probe = await request.post(`/api/devices/${device.id}/test`, { headers: adminHeaders, data: { area: 'OpcNode', nodeId: 'nsu=urn:edge;s=Temperature', dataType: 'Float' } }); expect(probe.status()).toBe(409)
  expect((await request.post(`/api/devices/${device.id}/opc/browse`, { headers: adminHeaders, data: {} })).status()).toBe(409)
  const command = await (await request.post('/api/commands', { headers: adminHeaders, data: { tagId: tag.id, value: 20 } })).json(); expect(command.success).toBeFalsy(); expect(command.message).toContain('未下发')
})

test('assignment requires disabled acquisition and the previous gateway must acknowledge its stopped configuration', async ({ request }) => {
  const adminHeaders = await admin(request), identity = await gateway(request, adminHeaders, `Route-${Date.now()}`), body = deviceInput(`Routing-${Date.now()}`)
  const device = await (await request.post('/api/devices', { headers: adminHeaders, data: body })).json()
  expect((await request.post(`/api/gateways/assignments/${device.id}`, { headers: adminHeaders, data: { gatewayId: identity.id } })).ok()).toBeTruthy()
  expect((await request.post(`/api/gateways/assignments/${device.id}`, { headers: adminHeaders, data: { gatewayId: null } })).status()).toBe(409)
  const session = await hello(request, identity.headers)
  const config = await (await request.get('/api/gateway-agent/configuration', { headers: session.headers })).json()
  expect((await request.post('/api/gateway-agent/heartbeat', { headers: session.headers, data: { appliedRevision: config.revision, pendingBatches: 0, pendingBytes: 0 } })).ok()).toBeTruthy()
  expect((await request.post(`/api/gateways/assignments/${device.id}`, { headers: adminHeaders, data: { gatewayId: null } })).ok()).toBeTruthy()
  expect((await (await request.get(`/api/devices/${device.id}`, { headers: adminHeaders })).json()).gatewayId).toBeNull()
  const userName = `gateway-viewer-${Date.now()}`
  await request.post('/api/users', { headers: adminHeaders, data: { userName, password: 'test-password-123', displayName: 'Viewer', role: 'Viewer', enabled: true } })
  const viewer = await (await request.post('/api/auth/login', { data: { userName, password: 'test-password-123' } })).json(); const headers = { Authorization: `Bearer ${viewer.token}` }
  expect((await request.get('/api/gateways', { headers })).status()).toBe(403)
  expect((await request.post('/api/gateways', { headers, data: { name: 'forbidden' } })).status()).toBe(403)
  expect((await request.post(`/api/gateways/assignments/${device.id}`, { headers, data: { gatewayId: identity.id } })).status()).toBe(403)
})
