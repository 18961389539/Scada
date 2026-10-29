import { test, expect, type APIRequestContext, type Page } from './fixtures'
import type { Device, DeviceInput, LoginResult } from '../src/types'
import type { CollectorDiagnostics } from '../src/api/diagnostics'

const device: Device = {
  id: 501, name: 'Setup Pump', areaId: 1, gatewayId: null, protocol: 'ModbusTcp', host: '127.0.0.1', port: 502,
  stationNumber: 1, byteOrder: 'CDAB', pollIntervalMs: 2000, timeoutMs: 7000, enabled: true,
  serialPort: 'COM1', baudRate: 9600, dataBits: 8, parity: 'None', stopBits: 'One',
  cpu: 'S71200', rack: 0, slot: 1, opcEndpointUrl: 'opc.tcp://localhost:4840',
  opcSecurityMode: 'SignAndEncrypt', opcSecurityPolicy: 'Basic256Sha256', opcUserName: '', hasOpcPassword: false
}
const tag = {
  id: 601, deviceId: device.id, name: 'Setup Temperature', kind: 'Physical', areaId: 1,
  area: 'HoldingRegister', address: 0, dataType: 'Float', description: '', unit: 'C', scale: 1, offset: 0,
  writable: false, archiveMode: 'None', deadband: 0, archivePeriodSec: 600, dbNumber: 1, bitOffset: 0, nodeId: ''
}
function diagnostic(patch: Partial<CollectorDiagnostics> = {}): CollectorDiagnostics {
  return {
    deviceId: device.id, name: device.name, protocol: device.protocol, connection: '127.0.0.1:502', state: 'Online',
    operation: 'Idle', operationTagId: null, currentError: null, enabled: true, configurationPending: false, configVersion: 1,
    capturedTs: Date.now(), startedTs: Date.now() - 60000, pollIntervalMs: 2000, timeoutMs: 7000, staleAfterMs: 60000,
    pendingCommands: 0, pointCount: 1, quality: { good: 1, bad: 0, uncertain: 0, commFailure: 0, unknown: 0, stale: 0 },
    metrics: {
      connectAttempts: 1, connectFailures: 0, pollAttempts: 1, pollSuccesses: 1, pollFailures: 0, degradedPolls: 0,
      consecutiveFailures: 0, lastAttemptTs: Date.now(), lastSuccessTs: Date.now(), lastFailureTs: null,
      nextRetryTs: null, lastFailure: null, commandsSucceeded: 0, commandsFailed: 0, commandsExpired: 0,
      commandsRejected: 0, lastCommandTs: null, lastPollMs: 1, windowAvgMs: 1, windowMaxMs: 1, windowP95Ms: 1, windowSamples: 1
    },
    readGroups: [], points: [], events: [], samples: [], ...patch
  }
}
async function prepare(page: Page, request: APIRequestContext, configureOnly = false): Promise<void> {
  const response = await request.post('/api/auth/login', { data: { userName: 'admin', password: 'admin123' } })
  expect(response.ok()).toBeTruthy()
  const session: LoginResult = await response.json()
  if (configureOnly) {
    session.user = { ...session.user, role: 'None', access: { global: [], areas: [{ areaId: 1, permissions: ['Configure'] }] } }
    await page.route(url => url.pathname === '/api/auth/me', route => route.fulfill({ json: session.user }))
  }
  await page.addInitScript(({ token, user }) => {
    localStorage.setItem('scada_token', token)
    localStorage.setItem('scada_user', JSON.stringify(user))
    localStorage.setItem('scada_locale', 'zh-CN')
  }, session)
  await page.route(url => url.pathname === '/api/devices', route => route.fulfill({ json: [device] }))
  await page.route(url => url.pathname === '/api/areas', route => route.fulfill({ json: [{ id: 1, name: 'Setup Area' }] }))
  await page.route(url => url.pathname === '/api/tags', route => {
    const search = new URL(route.request().url()).searchParams.get('search')
    return route.fulfill({ json: !search || tag.name.includes(search) ? [tag] : [] })
  })
  await page.route(url => url.pathname === '/api/screens', route => route.fulfill({ json: [{ id: 701, name: 'Setup Screen', revision: 1, publishedRevision: 1 }] }))
  await page.route(url => url.pathname === `/api/devices/${device.id}/diagnostics`, route => route.fulfill({ json: { diagnostics: diagnostic(), total: 1, page: 1, pageSize: 1 } }))
}
async function openGuide(page: Page): Promise<void> {
  await page.goto(`/overview?setupDeviceId=${device.id}`)
  const guide = page.locator('[data-test="setup-guide"]')
  await expect(guide).toContainText('Setup Pump')
  if (await guide.getAttribute('open') === null) await guide.locator('summary').click()
  await expect(guide.locator('[data-test="setup-verify"]')).not.toHaveClass(/is-loading/)
}
const state = (page: Page, step: string) => page.locator(`[data-test="setup-step-${step}"] .el-tag`)

test('setup verifies actual data, distinguishes publication scope, expires and retries without writes', async ({ page, request }) => {
  await prepare(page, request)
  let unavailable = false, stale = false, draft = false, writes = 0
  page.on('request', request => { if (request.method() !== 'GET' && /\/api\/devices\/\d+\//.test(request.url())) writes++ })
  await page.route(url => url.pathname === `/api/devices/${device.id}/diagnostics`, route => route.fulfill(unavailable
    ? { status: 503, json: { message: 'Gateway offline' } }
    : { json: { diagnostics: diagnostic({ capturedTs: Date.now() - (stale ? 120000 : 0) }), total: 1, page: 1, pageSize: 1 } }))
  await page.route(url => url.pathname === '/api/screens', route => route.fulfill({ json: [{ id: 701, publishedRevision: draft ? null : 1 }] }))
  await openGuide(page)
  for (const step of ['device', 'tags', 'connected', 'values', 'screen']) await expect(state(page, step)).toHaveText('已验证')
  await expect(page.locator('[data-test="setup-step-screen"]')).toContainText('不验证画面与所选设备的绑定')

  stale = true; draft = true
  await page.locator('[data-test="setup-verify"]').click()
  await expect(state(page, 'connected')).toHaveText('待验证')
  await expect(state(page, 'values')).toHaveText('待验证')
  await expect(state(page, 'screen')).toHaveText('待完成')
  unavailable = true
  await page.locator('[data-test="setup-verify"]').click()
  await expect(page.locator('[data-test="setup-step-connected"]')).toContainText('Gateway offline')
  await expect(state(page, 'connected')).toHaveText('待验证')
  stale = false; unavailable = false
  await page.locator('[data-test="setup-verify"]').click()
  await expect(state(page, 'values')).toHaveText('已验证')
  expect(writes).toBe(0)
  await page.locator('[data-test="setup-diagnostics"]').click()
  await expect(page.getByRole('dialog', { name: `采集诊断 · ${device.name}`, exact: true })).toBeVisible()
})

for (const scenario of ['no-tags', 'disabled', 'bad-quality', 'pending-config', 'tag-failure']) {
  test(`setup cannot verify live data for ${scenario}`, async ({ page, request }) => {
    await prepare(page, request)
    if (scenario === 'no-tags') await page.route(url => url.pathname === '/api/tags', route => route.fulfill({ json: [] }))
    if (scenario === 'tag-failure') await page.route(url => url.pathname === '/api/tags', route => route.fulfill({ status: 500, json: { message: 'Tag catalog unavailable' } }))
    if (scenario === 'disabled') await page.route(url => url.pathname === '/api/devices', route => route.fulfill({ json: [{ ...device, enabled: false }] }))
    await page.route(url => url.pathname === `/api/devices/${device.id}/diagnostics`, route => route.fulfill({ json: {
      diagnostics: diagnostic({
        configurationPending: scenario === 'pending-config',
        ...(scenario === 'bad-quality' ? { state: 'Degraded', quality: { good: 0, bad: 1, uncertain: 0, commFailure: 0, unknown: 0, stale: 0 } } : {})
      }), total: 1, page: 1, pageSize: 1
    } }))
    await openGuide(page)
    await expect(state(page, 'device')).toHaveText('已验证')
    await expect(state(page, 'values')).not.toHaveText('已验证')
    await expect(page.locator('[data-test="setup-continue"]')).toBeVisible()
    if (scenario === 'no-tags') {
      await page.locator('[data-test="setup-continue"]').click()
      await expect(page).toHaveURL(`/config/tags?deviceId=${device.id}`)
      await page.goBack()
      await expect(page).toHaveURL(`/overview?setupDeviceId=${device.id}`)
    }
  })
}

test('setup checks only configurable devices and does not request screens without View permission', async ({ page, request }) => {
  await prepare(page, request, true)
  let screensRequested = 0, otherDiagnosticsRequested = 0
  page.on('request', request => {
    const path = new URL(request.url()).pathname
    if (path === '/api/screens') screensRequested++
    if (path === '/api/devices/502/diagnostics') otherDiagnosticsRequested++
  })
  await page.route(url => url.pathname === '/api/devices', route => route.fulfill({ json: [device, { ...device, id: 502, areaId: 2, name: 'Other Area Pump' }] }))
  await openGuide(page)
  await expect(state(page, 'screen')).toHaveText('待验证')
  await expect(page.locator('[data-test="setup-step-screen"]')).toContainText('无查看画面权限')
  await page.locator('[data-test="setup-device"]').click()
  await expect(page.getByRole('option', { name: 'Other Area Pump' })).toHaveCount(0)
  await page.keyboard.press('Escape')
  expect(screensRequested).toBe(0)
  expect(otherDiagnosticsRequested).toBe(0)
})

for (const kind of ['device', 'tag']) {
  test(`${kind} query, sort and page survive refresh and navigation; drafts wait for Query`, async ({ page, request }) => {
    await prepare(page, request)
    const items = Array.from({ length: 105 }, (_, index) => ({ ...(kind === 'device' ? device : tag), id: 1000 + index, name: `Item ${String(index).padStart(3, '0')}` }))
    await page.route(url => url.pathname === `/api/${kind === 'device' ? 'devices' : 'tags'}`, route => route.fulfill({ json: items }))
    const path = kind === 'device' ? '/config/devices' : '/config/tags'
    await page.goto(`${path}?search=Item&sort=name&order=descending&page=2&keep=1`)
    const table = page.locator(`[data-test="${kind === 'device' ? 'devices' : 'tags'}-table"]`)
    const rows = table.locator('.el-table__body-wrapper .el-table__row')
    await expect(rows).toHaveCount(5)
    await expect(rows.first()).toContainText('Item 004')
    await expect(page.locator(`[data-test="${kind}-query-context"]`)).toContainText('第 2 页')
    await page.reload()
    await expect(rows.first()).toContainText('Item 004')
    const link = rows.first().locator(`[data-test="${kind === 'device' ? 'device-tags' : 'tag-rules'}"]`)
    await link.click()
    await expect(page).toHaveURL(kind === 'device' ? '/config/tags?deviceId=1004' : '/config/rules?tagId=1004')
    await page.goBack()
    await expect(page).toHaveURL(`${path}?search=Item&sort=name&order=descending&page=2&keep=1`)
    await expect(rows.first()).toContainText('Item 004')
    const search = page.getByRole('textbox', { name: kind === 'device' ? '搜索设备名称、地址或区域' : '搜索测点名称/描述' })
    await search.fill('Item 104')
    await expect(rows).toHaveCount(5)
    await expect(page.locator(`[data-test="${kind}-query-pending"]`)).toBeVisible()
    await search.press('Enter')
    await expect(page).not.toHaveURL(/page=2/)
    await expect(page).toHaveURL(/keep=1/)
    await expect(page.locator(`[data-test="${kind}-query-context"]`)).toContainText('Item 104')
  })
}

test('failed tag query preserves result scope and retry succeeds with applied conditions', async ({ page, request }) => {
  await prepare(page, request)
  let fail = true
  await page.goto(`/config/tags?deviceId=${device.id}`)
  const rows = page.locator('[data-test="tags-table"] .el-table__body-wrapper .el-table__row')
  await expect(rows).toHaveCount(1)
  await page.route(url => url.pathname === '/api/tags' && url.searchParams.has('search'), route => route.fulfill(fail
    ? { status: 503, json: { message: 'Query temporarily unavailable' } }
    : { json: [] }))
  await page.getByRole('textbox', { name: '搜索测点名称/描述' }).fill('missing')
  await page.locator('[data-test="tag-query"]').click()
  await expect(page.locator('[data-test="tags-load-error"]')).toContainText('Query temporarily unavailable')
  await expect(rows).toHaveCount(1)
  await expect(page.locator('[data-test="tag-query-context"]')).not.toContainText('missing')
  fail = false
  await page.locator('[data-test="tags-load-error"]').getByRole('button', { name: '重新加载' }).click()
  await expect(page.locator('[data-test="tags-load-error"]')).toHaveCount(0)
  await expect(rows).toHaveCount(0)
  await expect(page.locator('[data-test="tag-query-context"]')).toContainText('missing')
})

test('advanced device settings remain intact while collapsed and saved devices offer next actions', async ({ page, request }) => {
  await prepare(page, request)
  let saved: DeviceInput | undefined
  await page.route(url => url.pathname === `/api/devices/${device.id}`, async route => {
    saved = route.request().postDataJSON()
    await route.fulfill({ json: { ...device, ...saved } })
  })
  await page.goto('/config/devices')
  await page.locator('[data-test="devices-table"]').getByRole('button', { name: '编辑', exact: true }).click()
  const dialog = page.locator('[data-test="device-dialog"]')
  await expect(dialog.locator('[data-test="device-advanced"]')).not.toHaveAttribute('open')
  await expect(dialog.locator('[data-test="device-poll"]')).not.toBeVisible()
  await dialog.locator('[data-test="device-advanced"] summary').click()
  await expect(dialog.locator('[data-test="device-poll"] input')).toHaveValue('2000')
  await dialog.locator('[data-test="device-timeout"] input').fill('9000')
  await dialog.locator('[data-test="device-name"]').fill('Renamed Pump')
  await dialog.locator('[data-test="device-advanced"] summary').click()
  await dialog.locator('[data-test="device-save"]').click()
  await expect(dialog).toBeHidden()
  expect(saved).toMatchObject({ name: 'Renamed Pump', byteOrder: 'CDAB', pollIntervalMs: 2000, timeoutMs: 9000 })
  await expect(page.locator('[data-test="device-saved-next"]')).toContainText('保存配置不等于连接成功')
  await page.locator('[data-test="device-saved-next"]').getByRole('button', { name: '添加 / 查看测点' }).click()
  await expect(page).toHaveURL(`/config/tags?deviceId=${device.id}`)
})

for (const kind of ['device', 'tag']) {
  test(`${kind} test failure offers actionable guidance and non-Good quality never looks successful`, async ({ page, request }) => {
    await prepare(page, request)
    let response: { connected: boolean; message?: string; quality?: string; value?: number } = { connected: false, message: 'Connection timeout' }
    await page.route(url => url.pathname === `/api/devices/${device.id}/test`, route => route.fulfill({ json: response }))
    await page.goto(kind === 'device' ? '/config/devices' : '/config/tags')
    if (kind === 'device') await page.locator('[data-test="devices-table"]').getByRole('button', { name: '测试连接' }).click()
    else await page.locator('[data-test="tag-preview"]').click()
    const dialog = page.getByRole('dialog')
    const read = dialog.getByRole('button', { name: kind === 'device' ? '测试读取' : '读取预览', exact: true })
    await read.click()
    await expect(dialog).toContainText('Connection timeout')
    await expect(dialog.locator('[data-test="connection-guidance"]')).toContainText('核对主机')
    await expect(dialog.getByRole('button', { name: '采集诊断', exact: true })).toBeVisible()
    response = { connected: true, quality: 'Bad', value: 0 }
    await read.click()
    await expect(dialog.locator('.el-alert--success')).toHaveCount(0)
    await expect(dialog.locator('[data-test="connection-guidance"]')).toContainText('当前质量不是 Good')
    response = { connected: false, message: 'Unexpected vendor response XYZ' }
    await read.click()
    await expect(dialog.locator('[data-test="connection-guidance"]')).toContainText('暂不能确定故障原因')
    await dialog.getByRole('spinbutton').first().fill('10')
    await dialog.getByRole('spinbutton').first().press('Tab')
    await expect(dialog.locator('[data-test="connection-guidance"]')).toHaveCount(0)
    let release!: () => void
    const gate = new Promise<void>(resolve => { release = resolve })
    await page.route(url => url.pathname === `/api/devices/${device.id}/test`, async route => {
      await gate
      await route.fulfill({ json: { connected: false, message: 'Old address failure' } })
    })
    const pending = page.waitForRequest(request => new URL(request.url()).pathname === `/api/devices/${device.id}/test`)
    await read.click()
    await pending
    try {
      await dialog.getByRole('spinbutton').first().fill('20')
      await dialog.getByRole('spinbutton').first().press('Tab')
      const completed = page.waitForResponse(response => new URL(response.url()).pathname === `/api/devices/${device.id}/test`)
      release()
      await (await completed).finished()
      await page.evaluate(() => new Promise(requestAnimationFrame))
      await expect(dialog.locator('[data-test="connection-guidance"]')).toHaveCount(0)
      await expect(dialog).not.toContainText('Old address failure')
    } finally { release() }
  })
}

test('configuration pages and setup guide fit a narrow viewport', async ({ page, request }) => {
  await prepare(page, request)
  await page.setViewportSize({ width: 390, height: 844 })
  for (const path of ['/overview', '/config/devices', '/config/tags']) {
    await page.goto(path)
    if (path === '/overview') {
      const guide = page.locator('[data-test="setup-guide"]')
      await expect(guide).toContainText('Setup Pump')
      if (await guide.getAttribute('open') === null) await guide.locator('summary').click()
    } else await expect(page.locator(`[data-test="${path.endsWith('devices') ? 'device' : 'tag'}-query"]`)).toBeVisible()
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth + 1)).toBe(true)
  }
})

test('@smoke setup verifies real OPC simulator collection rather than saved configuration alone', async ({ page, request }) => {
  const response = await request.post('/api/auth/login', { data: { userName: 'admin', password: 'admin123' } })
  expect(response.ok()).toBeTruthy()
  const session: LoginResult = await response.json()
  const headers = { Authorization: `Bearer ${session.token}` }
  const created = await request.post('/api/devices', { headers, data: {
    name: `Verified-Setup-${Date.now()}`, protocol: 'OpcUa', host: 'localhost', port: 18583, stationNumber: 1,
    byteOrder: 'ABCD', pollIntervalMs: 1000, timeoutMs: 5000, enabled: true, areaId: 1,
    opcEndpointUrl: 'opc.tcp://localhost:18583/ScadaSimulator', opcSecurityMode: 'None', opcSecurityPolicy: 'None'
  } })
  expect(created.ok(), await created.text()).toBeTruthy()
  const liveDevice: Device = await created.json()
  const browse = await request.post(`/api/devices/${liveDevice.id}/opc/browse`, { headers, data: {} })
  expect(browse.ok(), await browse.text()).toBeTruthy()
  const root: { nodes: { displayName: string; nodeId: string }[] } = await browse.json()
  const folder = root.nodes.find(node => node.displayName === 'Scada')
  expect(folder).toBeDefined()
  const children = await request.post(`/api/devices/${liveDevice.id}/opc/browse`, { headers, data: { nodeId: folder!.nodeId, limit: 200 } })
  expect(children.ok(), await children.text()).toBeTruthy()
  const variables: { nodes: { displayName: string; nodeId: string }[] } = await children.json()
  const temperature = variables.nodes.find(node => node.displayName === 'Temperature')
  expect(temperature).toBeDefined()
  const tagResponse = await request.post('/api/tags', { headers, data: {
    deviceId: liveDevice.id, name: 'Verified temperature', area: 'OpcNode', address: 0, nodeId: temperature!.nodeId,
    dataType: 'Float', scale: 1, offset: 0, writable: false, archiveMode: 'None', deadband: 0, archivePeriodSec: 600
  } })
  expect(tagResponse.ok(), await tagResponse.text()).toBeTruthy()
  await expect.poll(async () => {
    const response = await request.get(`/api/devices/${liveDevice.id}/diagnostics`, { headers })
    expect(response.ok()).toBeTruthy()
    const result: { diagnostics: CollectorDiagnostics } = await response.json()
    return result.diagnostics.quality.good
  }, { timeout: 15000 }).toBe(1)
  await page.addInitScript(({ token, user }) => {
    localStorage.setItem('scada_token', token)
    localStorage.setItem('scada_user', JSON.stringify(user))
  }, session)
  await page.goto(`/overview?setupDeviceId=${liveDevice.id}`)
  const guide = page.locator('[data-test="setup-guide"]')
  await expect(guide).toContainText(liveDevice.name)
  if (await guide.getAttribute('open') === null) await guide.locator('summary').click()
  await expect(state(page, 'connected')).toHaveText('已验证')
  await expect(state(page, 'values')).toHaveText('已验证')
  const savedInput: DeviceInput = { ...liveDevice, enabled: false }
  const disabled = await request.put(`/api/devices/${liveDevice.id}`, { headers, data: savedInput })
  expect(disabled.ok(), await disabled.text()).toBeTruthy()
  await page.locator('[data-test="setup-verify"]').click()
  await expect(state(page, 'values')).not.toHaveText('已验证')
  await expect(state(page, 'device')).toHaveText('已验证')
})
