import { test, expect, type APIRequestContext, type Locator, type Page } from '@playwright/test'
import { createHash, randomUUID } from 'node:crypto'
import { spawn, type ChildProcess } from 'node:child_process'
import { createServer, request as forward } from 'node:http'
import { once } from 'node:events'
import { mkdtempSync, readFileSync, rmSync } from 'node:fs'
import { join, resolve, sep } from 'node:path'
import { tmpdir } from 'node:os'

async function auth(request: APIRequestContext) { const result = await (await request.post('/api/auth/login', { data: { userName: 'admin', password: 'admin123' } })).json(); return { ...result, headers: { Authorization: `Bearer ${result.token}` } } }
async function enter(page: Page, session: any, path: string) { await page.goto('/login'); await page.evaluate(data => { localStorage.setItem('scada_token', data.token); localStorage.setItem('scada_user', JSON.stringify(data.user)) }, session); await page.goto(path) }
const hash = (text: string) => createHash('sha256').update(text).digest('hex')
const field = (dialog: Locator, label: string) => dialog.locator('.el-form-item').filter({ has: dialog.page().getByText(label, { exact: true }) })
async function select(page: Page, dialog: Locator, label: string, value: string) { await field(dialog, label).locator('.el-select').click(); await page.getByRole('option', { name: value, exact: true }).click() }
async function menu(page: Page, row: Locator, label: string) { await row.getByRole('button', { name: '更多网关操作', exact: true }).click(); await page.getByRole('menuitem', { name: label, exact: true }).click() }

test('administrator manages identities, one-time keys and stale edits without storing or automatically replaying secrets', async ({ page, request }) => {
  test.setTimeout(60000); const session = await auth(request), name = `Managed-${Date.now()}`
  await enter(page, session, '/config/gateways'); await page.getByRole('button', { name: '新建网关', exact: true }).click()
  let dialog = page.getByRole('dialog', { name: '新建网关', exact: true }); await dialog.getByRole('textbox', { name: '网关名称' }).fill(name)
  const createdResponse = page.waitForResponse(r => new URL(r.url()).pathname === '/api/gateways' && r.request().method() === 'POST')
  await dialog.getByRole('button', { name: '创建并生成密钥' }).click(); const identity = await (await createdResponse).json()
  dialog = page.getByRole('dialog', { name: '保存本次网关密钥' }); await expect(dialog).toBeVisible()
  const key = dialog.getByLabel('本次网关密钥'); expect(hash(await key.inputValue())).toBe(hash(identity.token)); await expect(key).toHaveAttribute('type', 'password')
  const download = page.waitForEvent('download'); await dialog.getByRole('button', { name: '下载密钥记录' }).click()
  const saved = JSON.parse(readFileSync((await (await download).path())!, 'utf8')); expect(hash(saved.token)).toBe(hash(identity.token)); expect(saved.gatewayId).toBe(identity.id)
  expect(await page.evaluate(value => JSON.stringify(localStorage).includes(value) || JSON.stringify(sessionStorage).includes(value), identity.token)).toBe(false)
  await page.screenshot({ path: test.info().outputPath('gateway-key-masked.png'), fullPage: true, animations: 'disabled' }); await dialog.getByRole('button', { name: '关闭并清除' }).click()
  await expect(page.getByLabel('本次网关密钥')).toHaveCount(0)
  await page.getByPlaceholder('名称或编号').fill(name); let row = page.getByRole('row').filter({ hasText: name }); await expect(row).toContainText('未上报')
  await page.locator('.panel .el-switch__core').click() // Keep the edit snapshot stable while another administrator changes it.
  await row.getByRole('button', { name: '编辑', exact: true }).click(); dialog = page.getByRole('dialog', { name: '编辑网关', exact: true })
  await dialog.getByRole('textbox', { name: '网关名称' }).fill(`${name}-stale`)
  expect((await request.put(`/api/gateways/${identity.id}`, { headers: session.headers, data: { name: `${name}-external`, enabled: true } })).ok()).toBeTruthy()
  await dialog.getByRole('button', { name: '保存名称' }).click(); await expect(dialog.getByText('网关管理状态已变化', { exact: false })).toBeVisible()
  expect((await (await request.get(`/api/gateways/${identity.id}`, { headers: session.headers })).json()).name).toBe(`${name}-external`)
  await dialog.getByRole('button', { name: '取消', exact: true }).click(); row = page.getByRole('row').filter({ hasText: `${name}-external` })
  await menu(page, row, '停用网关'); await page.getByRole('button', { name: '确认操作', exact: true }).click(); await expect(row).toContainText('停用')
  await menu(page, row, '启用网关'); await page.getByRole('button', { name: '确认操作', exact: true }).click()
  const oldAgent = { Authorization: `Gateway ${identity.id}:${identity.token}` }, streamId = randomUUID()
  const hello = await (await request.post('/api/gateway-agent/hello', { headers: oldAgent, data: { version: 1, instanceId: randomUUID(), streamId, agentVersion: 'management-test' } })).json()
  await page.getByRole('button', { name: '刷新网关' }).click(); await menu(page, row, '释放会话')
  dialog = page.getByRole('dialog', { name: '释放网关会话', exact: true }); await expect(dialog.getByRole('button', { name: '释放已停止进程的会话' })).toBeDisabled()
  await dialog.getByText('已确认旧网关进程及现场 I/O 均已停止', { exact: true }).click(); await expect(dialog.getByRole('checkbox')).toBeChecked()
  await dialog.getByRole('button', { name: '释放已停止进程的会话' }).click(); await expect(dialog).not.toBeVisible()
  expect((await request.post('/api/gateway-agent/heartbeat', { headers: { ...oldAgent, 'X-Scada-Session': hello.sessionId }, data: { pendingBatches: 0, pendingBytes: 0 } })).status()).toBe(409)
  await menu(page, row, '轮换密钥'); const rotatedResponse = page.waitForResponse(r => new URL(r.url()).pathname.endsWith('/rotate-key'))
  await page.getByRole('button', { name: '确认轮换', exact: true }).click(); const rotated = await (await rotatedResponse).json()
  dialog = page.getByRole('dialog', { name: '保存本次网关密钥' }); expect(hash(await dialog.getByLabel('本次网关密钥').inputValue())).toBe(hash(rotated.token)); expect(hash(rotated.token)).not.toBe(hash(identity.token))
  await dialog.getByRole('button', { name: '关闭并清除' }).click(); expect((await request.post('/api/gateway-agent/hello', { headers: oldAgent, data: { version: 1, instanceId: randomUUID(), streamId, agentVersion: 'old-key' } })).status()).toBe(401)
  let posts = 0
  await page.route('**/api/gateways', async route => {
    if (route.request().method() !== 'POST') { await route.continue(); return }
    posts++; await route.fetch(); await route.fulfill({ status: 503, contentType: 'application/json', body: JSON.stringify({ message: 'simulated lost creation response' }) })
  })
  await page.getByRole('button', { name: '新建网关', exact: true }).click(); dialog = page.getByRole('dialog', { name: '新建网关', exact: true }); await dialog.getByRole('textbox', { name: '网关名称' }).fill(`${name}-uncertain`)
  await dialog.getByRole('button', { name: '创建并生成密钥' }).click(); await expect(dialog.getByText('不会自动重复提交', { exact: false })).toBeVisible(); expect(posts).toBe(1)
  const listResponse = await request.get('/api/gateways', { headers: session.headers }); expect(listResponse.headers()['cache-control']).toBe('no-store')
  const listed = await listResponse.json(); expect(listed.filter((x: any) => x.name === `${name}-uncertain`)).toHaveLength(1)
  const listedText = JSON.stringify(listed); expect(listedText.includes(identity.token) || listedText.includes(rotated.token)).toBe(false)
  await dialog.getByRole('button', { name: '取消', exact: true }).click(); await page.reload(); await expect(page.getByLabel('本次网关密钥')).toHaveCount(0)
  await page.getByPlaceholder('名称或编号').fill(name); await page.screenshot({ path: test.info().outputPath('gateway-management-list.png'), fullPage: true, animations: 'disabled' })
})

test('device onboarding and migration use the real gateway stop acknowledgement before changing collection location', async ({ page, request }) => {
  test.setTimeout(120000); const session = await auth(request), root = mkdtempSync(join(tmpdir(), 'scada-managed-gateway-'))
  const identity = await (await request.post('/api/gateways', { headers: session.headers, data: { name: `ManagedProcess-${Date.now()}` } })).json()
  let child: ChildProcess | undefined, holdConfiguration = false, heldConfigurationRequests = 0, log = ''
  const proxyRequests: string[] = []
  const proxy = createServer((req, res) => {
    const path = new URL(req.url!, 'http://127.0.0.1').pathname
    proxyRequests.push(`${req.method} ${path} conditional=${!!req.headers['if-none-match']} held=${holdConfiguration}`)
    if (proxyRequests.length > 200) proxyRequests.shift()
    if (holdConfiguration && path === '/api/gateway-agent/configuration') {
      heldConfigurationRequests++; res.writeHead(req.headers['if-none-match'] ? 304 : 503); res.end(); return
    }
    const upstream = forward({ hostname: '127.0.0.1', port: 18581, path: req.url, method: req.method, headers: req.headers }, reply => { res.writeHead(reply.statusCode!, reply.headers); reply.pipe(res) })
    upstream.on('error', () => { if (!res.headersSent) res.writeHead(503); res.end() }); req.pipe(upstream); res.on('close', () => upstream.destroy())
  })
  proxy.listen(0, '127.0.0.1'); await once(proxy, 'listening')
  const fieldName = `ManagedDevice-${Date.now()}`
  try {
    await enter(page, session, `/config/gateways?gatewayId=${identity.id}`); let details = page.getByRole('dialog', { name: `网关 #${identity.id} · ${`ManagedProcess-`}`, exact: false })
    await details.getByRole('button', { name: '打开设备配置' }).click(); await page.getByRole('button', { name: '新增设备', exact: true }).click()
    let dialog = page.getByRole('dialog', { name: '新增设备', exact: true }); await field(dialog, '设备名称').locator('input').fill(fieldName)
    await expect(field(dialog, '启用').getByRole('switch')).not.toBeChecked(); await select(page, dialog, '协议', 'OPC UA'); await select(page, dialog, '安全模式', '无（None）')
    await field(dialog, '端点 URL').locator('input').fill('opc.tcp://localhost:18583/ScadaSimulator'); await field(dialog, '采集周期 (ms)').locator('input').fill('200')
    const createdResponse = page.waitForResponse(r => new URL(r.url()).pathname === '/api/devices' && r.request().method() === 'POST')
    await dialog.getByRole('button', { name: '保存', exact: true }).click(); const device = await (await createdResponse).json()
    const browse = async (data: object) => { const response = await request.post(`/api/devices/${device.id}/opc/browse`, { headers: session.headers, data }); expect(response.ok(), await response.text()).toBeTruthy(); return response.json() }
    const folder = (await browse({})).nodes.find((x: any) => x.displayName === 'Scada')
    const temperature = (await browse({ nodeId: folder.nodeId, limit: 200 })).nodes.find((x: any) => x.displayName === 'Temperature')
    dialog = page.getByRole('dialog', { name: '更改采集位置', exact: true }); await expect(dialog.locator('.el-select')).toContainText(`网关 #${identity.id}`)
    await dialog.getByRole('button', { name: '保存采集位置' }).click(); await page.getByRole('button', { name: '确认迁移', exact: true }).click(); await expect(dialog).not.toBeVisible()
    const tag = await (await request.post('/api/tags', { headers: session.headers, data: { deviceId: device.id, name: 'ManagedTemperature', area: 'OpcNode', nodeId: temperature.nodeId,
      address: 0, dataType: 'Float', scale: 1, offset: 0, writable: false, archiveMode: 'None', archivePeriodSec: 600, deadband: 0 } })).json()
    child = spawn('dotnet', [resolve('../src/Scada.Gateway.Host/bin/Release/net10.0/Scada.Gateway.Host.dll')], { windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'], env: { ...process.env,
      GatewayAgent__ServerUrl: `http://127.0.0.1:${(proxy.address() as { port: number }).port}/`, GatewayAgent__GatewayId: String(identity.id), GatewayAgent__Token: identity.token, GatewayAgent__DataRoot: root,
      GatewayAgent__AllowLoopbackHttp: 'true', GatewayAgent__HeartbeatIntervalMs: '500', GatewayAgent__ConfigurationPollMs: '500', GatewayAgent__RetryDelayMs: '200' } })
    const capture = (chunk: Buffer) => { log = (log + String(chunk)).slice(-16000) }; child.stdout!.on('data', capture); child.stderr!.on('data', capture)
    const row = page.getByRole('row').filter({ hasText: fieldName }); await row.getByRole('button', { name: '编辑', exact: true }).click(); dialog = page.getByRole('dialog', { name: '编辑设备', exact: true })
    await field(dialog, '启用').locator('.el-switch__core').click(); await dialog.getByRole('button', { name: '保存', exact: true }).click(); await expect(dialog).not.toBeVisible()
    await expect.poll(async () => (await (await request.get(`/api/realtime?tagIds=${tag.id}`, { headers: session.headers })).json())[0]?.value, { timeout: 20000 }).toBe(23.5)
    await row.getByRole('link', { name: `网关 #${identity.id}`, exact: true }).click(); details = page.getByRole('dialog').filter({ hasText: '已分配设备' })
    await expect(details.getByText('已确认当前配置', { exact: true })).toBeVisible(); await expect(details.getByRole('row').filter({ hasText: fieldName })).toContainText('在线')
    await page.screenshot({ path: test.info().outputPath('gateway-management-details.png'), fullPage: true, animations: 'disabled' })
    await details.getByRole('row').filter({ hasText: fieldName }).getByRole('button', { name: '更改采集位置' }).click(); dialog = page.getByRole('dialog', { name: '更改采集位置', exact: true })
    await expect(dialog.getByText('请先停用设备', { exact: false })).toBeVisible(); await expect(dialog.getByRole('button', { name: '保存采集位置' })).toBeDisabled(); await dialog.getByRole('button', { name: '取消', exact: true }).click()
    holdConfiguration = true
    await expect.poll(() => heldConfigurationRequests, { timeout: 15000, message: 'proxy must observe a held configuration poll before disabling the device' }).toBeGreaterThan(0)
    await details.getByRole('button', { name: '打开设备配置' }).click(); await page.getByRole('row').filter({ hasText: fieldName }).getByRole('button', { name: '编辑', exact: true }).click()
    dialog = page.getByRole('dialog', { name: '编辑设备', exact: true }); await field(dialog, '启用').locator('.el-switch__core').click(); await dialog.getByRole('button', { name: '保存', exact: true }).click(); await expect(dialog).not.toBeVisible()
    await page.getByRole('row').filter({ hasText: fieldName }).getByRole('button', { name: '更改采集位置' }).click(); dialog = page.getByRole('dialog', { name: '更改采集位置', exact: true })
    await expect(dialog.getByText('原网关尚未确认停用配置', { exact: false })).toBeVisible(); await expect(dialog.getByRole('button', { name: '保存采集位置' })).toBeDisabled()
    await page.screenshot({ path: test.info().outputPath('gateway-migration-waits-for-stop.png'), fullPage: true, animations: 'disabled' }); holdConfiguration = false
    await expect.poll(async () => (await (await request.get(`/api/gateways/assignments/${device.id}`, { headers: session.headers })).json()).canMove, { timeout: 15000 }).toBe(true)
    await dialog.getByRole('button', { name: '刷新迁移条件' }).click(); await dialog.getByRole('combobox', { name: '目标采集位置' }).press('ArrowDown'); await page.getByRole('option', { name: '中心本机', exact: true }).click()
    await dialog.getByRole('button', { name: '保存采集位置' }).click(); await page.getByRole('button', { name: '确认迁移', exact: true }).click(); await expect(dialog).not.toBeVisible()
    const moved = await (await request.get(`/api/devices/${device.id}`, { headers: session.headers })).json(); expect(moved.gatewayId).toBeNull(); expect(moved.enabled).toBe(false)
    expect(log.includes(identity.token)).toBe(false)
  } catch (error) {
    await test.info().attach('managed-process', { body: log.replaceAll(identity.token, '[redacted]'), contentType: 'text/plain' })
    await test.info().attach('managed-proxy', { body: `exitCode=${child?.exitCode} signal=${child?.signalCode}\n${proxyRequests.join('\n')}`, contentType: 'text/plain' }); throw error
  } finally {
    holdConfiguration = false
    if (child && child.exitCode === null && child.signalCode === null) { const done = once(child, 'exit'); child.kill(); await done }
    proxy.closeAllConnections(); await new Promise<void>(done => proxy.close(() => done()))
    if (resolve(root).startsWith(resolve(tmpdir()) + sep) && root.split(sep).at(-1)!.startsWith('scada-managed-gateway-')) rmSync(root, { recursive: true, force: true })
  }
})

test('management routes enforce admin access and leaving the page aborts its read and removes automatic polling', async ({ page, request }) => {
  const session = await auth(request), name = `GatewayViewer-${Date.now()}`
  await request.post('/api/users', { headers: session.headers, data: { userName: name, password: 'test-password-123', displayName: name, role: 'Viewer', enabled: true } })
  const viewer = await (await request.post('/api/auth/login', { data: { userName: name, password: 'test-password-123' } })).json(), viewerHeaders = { Authorization: `Bearer ${viewer.token}` }
  for (const path of ['/api/gateways', '/api/gateways/1/devices', '/api/gateways/assignments/1']) expect((await request.get(path, { headers: viewerHeaders })).status()).toBe(403)
  for (const path of ['/api/gateways', '/api/gateways/1/rotate-key', '/api/gateways/1/release-session', '/api/gateways/assignments/1'])
    expect((await request.post(path, { headers: viewerHeaders, data: { name: 'forbidden', gatewayId: null } })).status()).toBe(403)
  expect((await request.put('/api/gateways/1', { headers: viewerHeaders, data: { name: 'forbidden', enabled: false } })).status()).toBe(403)
  await enter(page, viewer, '/config/gateways'); await expect(page).toHaveURL(/\/overview$/)
  await page.clock.install(); await enter(page, session, '/realtime')
  let reads = 0, aborted = 0; let release!: () => void; const held = new Promise<void>(done => { release = done })
  await page.route('**/api/gateways', async route => { reads++; await held; try { await route.continue() } catch { /* the component deliberately cancelled its read */ } })
  page.on('requestfailed', r => { if (new URL(r.url()).pathname === '/api/gateways') aborted++ })
  await page.getByText('组态配置', { exact: true }).click(); await page.getByRole('menuitem', { name: '采集网关', exact: true }).click(); await expect.poll(() => reads).toBe(1)
  await page.getByRole('menuitem', { name: '实时监视', exact: true }).click(); await expect.poll(() => aborted).toBeGreaterThan(0); release()
  await page.clock.fastForward(15000); expect(reads).toBe(1); await expect(page.getByRole('dialog', { name: '保存本次网关密钥' })).not.toBeVisible()
})
