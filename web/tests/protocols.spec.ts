import { test, expect, type APIRequestContext, type Locator, type Page } from '@playwright/test'

async function login(request: APIRequestContext) {
  const auth = await (await request.post('/api/auth/login', { data: { userName: 'admin', password: 'admin123' } })).json()
  return { ...auth, headers: { Authorization: `Bearer ${auth.token}` } }
}
const deviceInput = (name: string, protocol = 'SiemensS7') => ({ name, protocol, host: '127.0.0.1', port: 102, stationNumber: 1,
  byteOrder: 'ABCD', pollIntervalMs: 1000, timeoutMs: 500, enabled: false, serialPort: 'COM90', baudRate: 9600, dataBits: 8,
  parity: 'None', stopBits: 'One', cpu: 'S71200', rack: 0, slot: 1 })
const tagInput = (deviceId: number, name: string) => ({ deviceId, name, area: 'DataBlock', address: 100, dbNumber: 17, bitOffset: 6,
  dataType: 'Bool', scale: 1, offset: 0, writable: true, archiveMode: 'OnChange', deadband: 0, archivePeriodSec: 600 })

test('protocol APIs validate S7 addressing, preserve explicit zero settings and protect existing tags', async ({ request }) => {
  const { headers } = await login(request), suffix = Date.now()
  const input = { ...deviceInput(`S7-${suffix}`), cpu: 'S7200', slot: 0 }
  const response = await request.post('/api/devices', { headers, data: input }); expect(response.status()).toBe(201)
  const device = await response.json(); expect(device.cpu).toBe('S7200'); expect(device.slot).toBe(0)
  const tag = tagInput(device.id, 'db-bit')
  expect((await request.post('/api/tags', { headers, data: { ...tag, bitOffset: 8 } })).status()).toBe(400)
  expect((await request.post('/api/tags', { headers, data: { ...tag, area: 'Input' } })).status()).toBe(400)
  expect((await request.post('/api/tags', { headers, data: { ...tag, area: 'Coil' } })).status()).toBe(400)
  expect((await request.post('/api/tags', { headers, data: tag })).status()).toBe(201)
  expect((await request.put(`/api/devices/${device.id}`, { headers, data: { ...input, protocol: 'ModbusTcp' } })).status()).toBe(400)
  expect((await (await request.get(`/api/devices/${device.id}`, { headers })).json()).protocol).toBe('SiemensS7')
  expect((await request.post('/api/devices', { headers, data: { ...input, name: 'invalid-protocol', protocol: 99 } })).status()).toBe(400)
  const exported = await request.get('/api/tags/export', { headers }); expect(exported.ok()).toBeTruthy()
  const imported = await request.post('/api/tags/import', { headers, multipart: { file: { name: 'tags.xlsx', mimeType: 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet', buffer: await exported.body() } } })
  expect(imported.ok()).toBeTruthy(); expect((await imported.json()).errors).toEqual([])
  const saved = await (await request.get(`/api/tags?deviceId=${device.id}`, { headers })).json()
  expect(saved[0].dbNumber).toBe(17); expect(saved[0].bitOffset).toBe(6)
})

test('RTU configuration prevents conflicting bus parameters, duplicate stations and broadcasts', async ({ request }) => {
  const { headers } = await login(request), suffix = Date.now()
  const input = { ...deviceInput(`RTU-${suffix}`, 'ModbusRtu'), serialPort: `COM-${suffix}`, enabled: true }
  expect((await request.post('/api/devices', { headers, data: input })).status()).toBe(201)
  expect((await request.post('/api/devices', { headers, data: { ...input, name: `duplicate-${suffix}`, serialPort: input.serialPort.toLowerCase() } })).status()).toBe(400)
  expect((await request.post('/api/devices', { headers, data: { ...input, name: `conflict-${suffix}`, stationNumber: 2, baudRate: 19200 } })).status()).toBe(400)
  expect((await request.post('/api/devices', { headers, data: { ...input, name: `station2-${suffix}`, stationNumber: 2 } })).status()).toBe(201)
  expect((await request.post('/api/devices', { headers, data: { ...input, name: `broadcast-${suffix}`, stationNumber: 0 } })).status()).toBe(400)
  expect((await request.post('/api/devices', { headers, data: { ...input, name: `seven-bits-${suffix}`, dataBits: 7 } })).status()).toBe(400)
})

function field(dialog: Locator, label: string) {
  return dialog.locator('.el-form-item').filter({ has: dialog.page().locator('label').filter({ hasText: new RegExp(`^${label}$`) }) })
}
async function select(page: Page, dialog: Locator, label: string, value: string) {
  await field(dialog, label).locator('.el-select').click(); await page.getByRole('option', { name: value, exact: true }).click()
  await expect(page.getByRole('option', { name: value, exact: true })).not.toBeVisible()
}

test('S7 template instances preserve DB and bit addressing through a version upgrade', async ({ request }) => {
  const { headers } = await login(request), suffix = Date.now()
  const device = await (await request.post('/api/devices', { headers, data: deviceInput(`template-S7-${suffix}`) })).json()
  const document = await (await request.get('/api/object-templates/examples/pump', { headers })).json()
  for (const item of document.tags) {
    item.properties.area = item.key === 'run' ? 'Memory' : 'DataBlock'
    item.properties.dbNumber = 17; item.properties.bitOffset = item.key === 'run' ? 3 : 0
    item.addressOffset = item.key === 'fault' ? 4 : 0
  }
  const templateResponse = await request.post('/api/object-templates', { headers, data: { name: `S7-template-${suffix}`, revision: 0, document } })
  expect(templateResponse.status()).toBe(201); const template = await templateResponse.json()
  expect((await request.post(`/api/object-templates/${template.id}/publish`, { headers, data: { revision: template.revision } })).ok()).toBeTruthy()
  const response = await request.post(`/api/object-templates/${template.id}/instantiate`, { headers, data: { version: 1, instances: [{ name: 'S7-P01', deviceId: device.id, baseAddress: 200 }] } })
  expect(response.ok()).toBeTruthy(); const objects = await response.json()
  const before = await (await request.get(`/api/object-instances/${objects[0].id}`, { headers })).json()
  expect(before.baseline.tags.frequency.dbNumber).toBe(17); expect(before.baseline.tags.frequency.address).toBe(200)
  expect(before.baseline.tags.fault.address).toBe(204); expect(before.baseline.tags.run.bitOffset).toBe(3)
  for (const item of document.tags) item.properties.dbNumber = 23
  const current = await (await request.get(`/api/object-templates/${template.id}`, { headers })).json()
  const saved = await (await request.put(`/api/object-templates/${template.id}`, { headers, data: { name: current.name, revision: current.revision, document } })).json()
  expect((await request.post(`/api/object-templates/${template.id}/publish`, { headers, data: { revision: saved.revision } })).ok()).toBeTruthy()
  const preview = await (await request.post(`/api/object-instances/${before.id}/upgrade/preview`, { headers, data: { version: 2, revision: before.revision } })).json()
  expect(preview.canApply).toBeTruthy(); expect(preview.conflicts).toEqual([])
  expect((await request.post('/api/object-instances/upgrade', { headers, data: { items: [{ id: before.id, version: 2, revision: before.revision, expectedState: preview.state, resolutions: {} }] } })).ok()).toBeTruthy()
  const after = await (await request.get(`/api/object-instances/${before.id}`, { headers })).json()
  expect(after.baseline.tags.frequency.dbNumber).toBe(23); expect(after.baseline.tags.run.bitOffset).toBe(3)
})

test('device and tag editors configure S7 and RTU without exposing the wrong protocol fields', async ({ page, request }) => {
  const auth = await login(request), suffix = Date.now(), name = `UI-S7-${suffix}`
  await page.addInitScript(({ token, user }) => { localStorage.setItem('scada_token', token); localStorage.setItem('scada_user', JSON.stringify(user)) }, auth)
  const errors: string[] = []; page.on('pageerror', error => errors.push(error.message))
  await page.goto('/config/devices'); await page.getByRole('button', { name: '新增设备', exact: true }).click()
  let dialog = page.getByRole('dialog', { name: '新增设备', exact: true })
  await field(dialog, '设备名称').locator('input').fill(name); await select(page, dialog, '协议', 'Siemens S7')
  await expect(field(dialog, 'CPU')).toBeVisible(); await expect(field(dialog, '站号')).toHaveCount(0)
  await expect(field(dialog, '端口').locator('input')).toHaveValue('102')
  await field(dialog, '启用').locator('.el-switch__core').click()
  await page.screenshot({ path: 'test-results/s7-device-editor.png' })
  await dialog.getByRole('button', { name: '保存', exact: true }).click(); await expect(dialog).not.toBeVisible()
  const device = (await (await request.get('/api/devices', { headers: auth.headers })).json()).find((x: { name: string }) => x.name === name)
  expect(device.protocol).toBe('SiemensS7')
  await page.goto('/config/tags'); await page.getByRole('button', { name: '新增测点', exact: true }).click()
  dialog = page.getByRole('dialog', { name: '新增测点', exact: true })
  await select(page, dialog, '所属设备', name); await field(dialog, '测点名称').locator('input').fill(`UI-bit-${suffix}`)
  await select(page, dialog, '数据类型', '布尔')
  await field(dialog, 'DB 编号').locator('input').fill('17'); await field(dialog, '位偏移').locator('input').fill('6')
  await field(dialog, '地址').locator('input').fill('100')
  await page.screenshot({ path: 'test-results/s7-tag-editor.png' })
  await dialog.getByRole('button', { name: '保存', exact: true }).click(); await expect(dialog).not.toBeVisible()
  const tags = await (await request.get(`/api/tags?deviceId=${device.id}`, { headers: auth.headers })).json()
  expect(tags[0].area).toBe('DataBlock'); expect(tags[0].dbNumber).toBe(17); expect(tags[0].bitOffset).toBe(6)
  await page.goto('/config/devices'); await page.getByRole('button', { name: '新增设备', exact: true }).click()
  dialog = page.getByRole('dialog', { name: '新增设备', exact: true }); await select(page, dialog, '协议', 'Modbus RTU（串口）')
  await expect(field(dialog, '串口')).toBeVisible(); await expect(field(dialog, '主机')).toHaveCount(0)
  await page.screenshot({ path: 'test-results/rtu-device-editor.png' }); expect(errors).toEqual([])
})
