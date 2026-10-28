import { createConnection, createServer, type Socket } from 'node:net'
import { test, expect, type APIRequestContext, type Page } from '@playwright/test'

async function plcSimulator() {
  const registers = new Uint16Array(256)
  const sockets = new Set<Socket>()
  const writes: number[] = []
  const server = createServer(socket => {
    sockets.add(socket)
    socket.on('close', () => sockets.delete(socket))
    socket.on('error', () => {})
    let buffered = Buffer.alloc(0)
    socket.on('data', chunk => {
      buffered = Buffer.concat([buffered, chunk])
      while (buffered.length >= 7 && buffered.length >= 6 + buffered.readUInt16BE(4)) {
        const length = 6 + buffered.readUInt16BE(4)
        const frame = buffered.subarray(0, length)
        buffered = buffered.subarray(length)
        const command = frame[7], address = frame.readUInt16BE(8)
        let payload: Buffer
        if (command === 3 || command === 4) {
          const count = frame.readUInt16BE(10)
          payload = Buffer.alloc(2 + count * 2); payload[0] = command; payload[1] = count * 2
          for (let index = 0; index < count; index++) payload.writeUInt16BE(registers[address + index] ?? 0, 2 + index * 2)
        } else if (command === 6) {
          registers[address] = frame.readUInt16BE(10)
          if (address === 100) writes.push(registers[address]!)
          payload = Buffer.from(frame.subarray(7, 12))
        } else if (command === 16) {
          const count = frame.readUInt16BE(10)
          for (let index = 0; index < count; index++) registers[address + index] = frame.readUInt16BE(13 + index * 2)
          if (address === 100) writes.push(registers[address]!)
          payload = Buffer.from(frame.subarray(7, 12))
        } else { payload = Buffer.from([command! | 128, 1]) }
        const response = Buffer.alloc(7 + payload.length)
        frame.copy(response, 0, 0, 7); response.writeUInt16BE(payload.length + 1, 4); payload.copy(response, 7)
        socket.write(response)
      }
    })
  })
  await new Promise<void>(resolve => server.listen(0, '127.0.0.1', resolve))
  const address = server.address()
  if (!address || typeof address === 'string') throw new Error('No simulator port')
  return { registers, writes, port: address.port, close: async () => { for (const socket of sockets) socket.destroy(); await new Promise<void>(resolve => server.close(() => resolve())) } }
}

async function setup(request: APIRequestContext, page: Page, port: number) {
  const authResponse = await request.post('/api/auth/login', { data: { userName: 'admin', password: 'admin123' } })
  expect(authResponse.ok()).toBeTruthy()
  const auth = await authResponse.json()
  const headers = { Authorization: `Bearer ${auth.token}` }
  const post = async (url: string, data: unknown) => {
    const response = await request.post(url, { headers, data })
    expect(response.ok(), await response.text()).toBeTruthy()
    return response.json()
  }
  const device = await post('/api/devices', { name: `Trace-PLC-${port}`, protocol: 'ModbusTcp', host: '127.0.0.1', port,
    stationNumber: 1, byteOrder: 'ABCD', pollIntervalMs: 100, timeoutMs: 1000, enabled: true })
  const point = (name: string, address: number, writable = false) => post('/api/tags', { deviceId: device.id, name,
    area: 'HoldingRegister', address, dataType: 'UInt16', scale: 1, offset: 0, writable, archiveMode: 'None', deadband: 0, archivePeriodSec: 600 })
  const trigger = await point('D100', 100, true), current = await point('Current', 110), voltage = await point('Voltage', 111)
  const snFirst = await point('SN-word-1', 120), snSecond = await point('SN-word-2', 121)
  await expect.poll(async () => {
    const result = await request.get(`/api/realtime?tagIds=${trigger.id}`, { headers })
    const values = await result.json(); return values[0]?.quality
  }, { timeout: 15000 }).toBe('Good')
  await page.addInitScript(({ token, user }) => { localStorage.setItem('scada_token', token); localStorage.setItem('scada_user', JSON.stringify(user)) }, auth)
  return { headers, device, trigger, current, voltage, snFirst, snSecond, post }
}

async function select(page: Page, selector: string, name: string) {
  const control = page.locator(`[data-test="${selector}"]`)
  await control.click()
  const listId = await control.getByRole('combobox').getAttribute('aria-controls')
  const option = page.locator(`[id="${listId}"]`).getByRole('option', { name, exact: true })
  await option.click()
  await expect(option).not.toBeVisible()
}

test('web scan captures current and voltage once, replies 2, and replies 3 without a new SN', async ({ page, request }, testInfo) => {
  test.setTimeout(90000)
  const plc = await plcSimulator()
  const sn = `QR-SN-${Date.now()}-${plc.port}`
  try {
    plc.registers[110] = 12; plc.registers[111] = 220
    const setupResult = await setup(request, page, plc.port)
    const errors: string[] = []; page.on('pageerror', error => errors.push(error.message))
    await page.goto('/traceability')
    await page.getByRole('tab', { name: '任务配置', exact: true }).click()
    await page.locator('[data-test="trace-add"]').click()
    await page.locator('[data-test="trace-name"]').fill(`Web-${plc.port}`)
    await page.locator('[data-test="trace-enabled"]').click()
    await select(page, 'trace-device', setupResult.device.name)
    await select(page, 'trace-trigger', 'D100')
    await page.locator('[data-test="trace-source"]').getByText('网页扫码', { exact: true }).click()
    await expect(page.locator('[data-test="trace-sn-registers"]')).toHaveCount(0)
    await page.locator('[data-test="trace-field-name-0"]').fill('电流')
    await select(page, 'trace-field-tag-0', 'Current')
    await page.locator('[data-test="trace-add-field"]').click()
    await page.locator('[data-test="trace-field-name-1"]').fill('电压')
    await select(page, 'trace-field-tag-1', 'Voltage')
    await page.screenshot({ path: testInfo.outputPath('configuration-desktop.png') })
    await page.locator('[data-test="trace-save"]').click()
    await expect(page.getByRole('dialog')).not.toBeVisible()
    await page.getByRole('tab', { name: '扫码绑定', exact: true }).click()
    await select(page, 'trace-scan-job', `Web-${plc.port}`)
    await page.locator('[data-test="trace-scan-input"]').fill(sn)
    await page.locator('[data-test="trace-scan-input"]').press('Enter')
    await expect(page.locator('[data-test="trace-pending-sn"]')).toHaveText(sn)
    await page.screenshot({ path: testInfo.outputPath('scan-desktop.png') })
    plc.registers[100] = 1
    await expect.poll(() => plc.registers[100], { timeout: 15000 }).toBe(2)
    await page.getByRole('tab', { name: '追溯查询', exact: true }).click()
    await page.locator('[data-test="trace-sn-query"]').fill(sn)
    await page.locator('[data-test="trace-query"]').click()
    await expect(page.locator('[data-test="trace-records"]')).toContainText(sn)
    await expect(page.locator('[data-test="trace-records"]')).toContainText('已回复 2')
    await page.locator('[data-test="trace-records"] .el-table__expand-icon').click()
    await expect(page.locator('[data-test="trace-records"]')).toContainText('220')
    await page.screenshot({ path: testInfo.outputPath('records-desktop.png') })
    plc.registers[100] = 1
    await expect.poll(() => plc.registers[100], { timeout: 15000 }).toBe(3)
    const response = await request.get(`/api/traceability/records?sn=${sn}`, { headers: setupResult.headers })
    const result = await response.json()
    expect(result.total).toBe(1)
    expect(result.items[0].values.map((value: { value: number }) => value.value)).toEqual([12, 220])
    expect(plc.writes).toEqual([2, 3])
    await page.setViewportSize({ width: 390, height: 844 })
    await page.screenshot({ path: testInfo.outputPath('records-mobile.png') })
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth + 1)).toBeTruthy()
    expect(errors).toEqual([])
  } finally { await plc.close() }
})

test('PLC ASCII SN is traceable and mutually exclusive source configuration is rejected', async ({ page, request }) => {
  test.setTimeout(60000)
  const plc = await plcSimulator()
  try {
    plc.registers[110] = 10; plc.registers[111] = 230
    plc.registers[120] = 0x4241; plc.registers[121] = 0x0031
    const setupResult = await setup(request, page, plc.port)
    const input = { name: `PLC-${plc.port}`, enabled: true, triggerTagId: setupResult.trigger.id,
      snSource: 'Plc', snFormat: 'AsciiLowByteFirst', snTagIds: [setupResult.snFirst.id, setupResult.snSecond.id],
      fields: [{ name: 'Current', tagId: setupResult.current.id }, { name: 'Voltage', tagId: setupResult.voltage.id }], scanExpirySeconds: 300 }
    const invalid = await request.post('/api/traceability/jobs', { headers: setupResult.headers, data: { ...input, snSource: 'WebScan' } })
    expect(invalid.status()).toBe(400)
    const job = await setupResult.post('/api/traceability/jobs', input)
    const scan = await request.post(`/api/traceability/jobs/${job.id}/scan`, { headers: setupResult.headers, data: { sn: 'WRONG' } })
    expect(scan.status()).toBe(409)
    plc.registers[100] = 1
    await expect.poll(() => plc.registers[100], { timeout: 15000 }).toBe(2)
    const result = await (await request.get(`/api/traceability/records?sn=AB1&jobId=${job.id}`, { headers: setupResult.headers })).json()
    expect(result.total).toBe(1)
    expect(result.items[0].sn).toBe('AB1')
    const stale = await request.put(`/api/traceability/jobs/${job.id}`, { headers: setupResult.headers, data: { ...input, revision: 0 } })
    expect(stale.status()).toBe(409)
    expect((await request.get('/api/traceability/records')).status()).toBe(401)
  } finally { await plc.close() }
})

test('mapped failed inspections keep reply 2, persist their snapshot and filter independently', async ({ page, request }) => {
  test.setTimeout(90000)
  const plc = await plcSimulator()
  try {
    plc.registers[110] = 2; plc.registers[111] = 220; plc.registers[120] = 771
    const fixture = await setup(request, page, plc.port)
    const input = {
      name: `Outcome-${plc.port}`, enabled: false, triggerTagId: fixture.trigger.id, snSource: 'Plc',
      snFormat: 'Numeric', snTagIds: [fixture.snFirst.id], fields: [{ name: 'Inspection', tagId: fixture.current.id }]
    }
    const job = await fixture.post('/api/traceability/jobs', input)
    await page.goto('/traceability')
    await page.getByRole('tab', { name: '任务配置', exact: true }).click()
    await page.locator('[data-test="trace-jobs"] .el-table__row').filter({ hasText: input.name }).locator('[data-test="trace-edit"]').click()
    await select(page, 'trace-outcome-field', 'Inspection')
    await page.getByRole('dialog').getByRole('button', { name: '取消', exact: true }).click()
    await expect(page.locator('.el-message-box')).toContainText('尚有未保存的修改')
    await page.locator('.el-message-box').getByRole('button', { name: '继续编辑', exact: true }).click()
    await page.locator('[data-test="trace-fail-value"] input').fill('1')
    await page.locator('[data-test="trace-save"]').click()
    await expect(page.locator('[data-test="trace-form-error"]')).toContainText('两个不同')
    await page.locator('[data-test="trace-fail-value"] input').fill('2')
    await page.locator('[data-test="trace-enabled"]').click()
    await page.locator('[data-test="trace-save"]').click()
    await expect(page.getByRole('dialog')).toBeHidden()
    plc.registers[100] = 1
    await expect.poll(() => plc.registers[100], { timeout: 15000 }).toBe(2)
    await page.getByRole('tab', { name: '追溯查询', exact: true }).click()
    await page.locator('[data-test="trace-sn-query"]').fill('771')
    await select(page, 'trace-outcome-filter', '不合格')
    const filtered = page.waitForRequest(request => {
      const url = new URL(request.url())
      return url.pathname === '/api/traceability/records' && url.searchParams.get('outcome') === 'Failed'
    })
    await page.locator('[data-test="trace-query"]').click()
    await filtered
    await expect(page.locator('[data-test="trace-records"]')).toContainText('不合格')
    await expect(page.locator('[data-test="trace-records"]')).toContainText('已回复 2')
    const jobs = await (await request.get('/api/traceability/jobs', { headers: fixture.headers })).json()
    const saved = jobs.find((item: { id: number }) => item.id === job.id)
    expect(saved.fields[0].outcomeMapping).toEqual({ passValue: 1, failValue: 2 })
    const changed = await request.put(`/api/traceability/jobs/${job.id}`, {
      headers: fixture.headers,
      data: { ...input, revision: saved.revision, fields: [{ ...input.fields[0], outcomeMapping: { passValue: 2, failValue: 1 } }] }
    })
    expect(changed.ok(), await changed.text()).toBeTruthy()
    await page.locator('[data-test="trace-query"]').click()
    await expect(page.locator('[data-test="trace-records"]')).toContainText('不合格')
    const result = await (await request.get(`/api/traceability/records?sn=771&jobId=${job.id}`, { headers: fixture.headers })).json()
    expect(result.items[0].outcome).toBe('Failed')
    expect(result.items[0].values[0].outcome).toBe('Failed')
    expect(result.items[0].acknowledged).toBe(true)
    expect(plc.writes).toEqual([2])
  } finally { await plc.close() }
})

for (const mode of ['Client', 'Server'] as const) {
  test(`TCP scanner ${mode} receives SN and captures PLC data`, async ({ page, request }, testInfo) => {
    test.setTimeout(90000)
    const plc = await plcSimulator()
    const scannerSockets = new Set<Socket>()
    let connected: (socket: Socket) => void = () => {}
    const socketReady = new Promise<Socket>(resolve => { connected = resolve })
    const scannerServer = createServer(socket => { scannerSockets.add(socket); socket.on('error', () => {}); connected(socket) })
    await new Promise<void>(resolve => scannerServer.listen(0, '127.0.0.1', resolve))
    const address = scannerServer.address()
    if (!address || typeof address === 'string') throw new Error('No scanner port')
    if (mode === 'Server') await new Promise<void>(resolve => scannerServer.close(() => resolve()))
    let jobId: number | undefined
    let headers: Record<string, string> | undefined
    try {
      plc.registers[110] = 17; plc.registers[111] = 225
      const fixture = await setup(request, page, plc.port)
      headers = fixture.headers
      await page.goto('/traceability')
      await page.getByRole('tab', { name: '任务配置', exact: true }).click()
      await page.locator('[data-test="trace-add"]').click()
      await page.locator('[data-test="trace-name"]').fill(`TCP-${mode}-${plc.port}`)
      await page.locator('[data-test="trace-enabled"]').click()
      await select(page, 'trace-device', fixture.device.name)
      await select(page, 'trace-trigger', 'D100')
      await page.locator('[data-test="trace-source"]').getByText('TCP 扫码枪', { exact: true }).click()
      await page.locator('[data-test="trace-tcp-mode"]').getByText(mode === 'Client' ? '主动连接' : '监听接入', { exact: true }).click()
      await page.locator('[data-test="trace-tcp-port"] input').fill(String(address.port))
      await page.locator('[data-test="trace-field-name-0"]').fill('Current')
      await select(page, 'trace-field-tag-0', 'Current')
      await page.screenshot({ path: testInfo.outputPath('tcp-config-desktop.png') })
      await page.locator('[data-test="trace-save"]').click()
      await expect(page.getByRole('dialog')).not.toBeVisible()
      const jobs = await (await request.get('/api/traceability/jobs', { headers })).json()
      const job = jobs.find((item: { name: string }) => item.name === `TCP-${mode}-${plc.port}`)
      jobId = job.id
      let scanner: Socket
      if (mode === 'Client') scanner = await socketReady
      else {
        await expect.poll(async () => {
          const current = await (await request.get('/api/traceability/jobs', { headers })).json()
          return current.find((item: { id: number }) => item.id === jobId)?.scannerStatus.state
        }, { timeout: 15000 }).toBe('listening')
        scanner = createConnection({ host: '127.0.0.1', port: address.port })
        scannerSockets.add(scanner); scanner.on('error', () => {})
        await new Promise<void>((resolve, reject) => { scanner.once('connect', resolve); scanner.once('error', reject) })
      }
      await page.getByRole('tab', { name: '扫码绑定', exact: true }).click()
      await select(page, 'trace-scan-job', job.name)
      const sn = `TCP-${mode}-${plc.port}`
      scanner.write(sn.slice(0, 4)); scanner.write(sn.slice(4) + '\r\n')
      await expect(page.locator('[data-test="trace-pending-sn"]')).toHaveText(sn)
      await expect(page.locator('[data-test="trace-scanner-state"]')).toHaveText('已连接')
      await expect(page.locator('[data-test="trace-scan-input"]')).toHaveCount(0)
      scanner.write('MUST-NOT-REPLACE\r\n')
      await expect(page.locator('[data-test="trace-scanner-error"]')).toBeVisible()
      await expect(page.locator('[data-test="trace-pending-sn"]')).toHaveText(sn)
      plc.registers[100] = 1
      await expect.poll(() => plc.registers[100], { timeout: 15000 }).toBe(2)
      const records = await (await request.get(`/api/traceability/records?jobId=${jobId}`, { headers })).json()
      expect(records.items[0].sn).toBe(sn)
      expect(records.items[0].values[0].value).toBe(17)
      await page.setViewportSize({ width: 390, height: 844 })
      await page.screenshot({ path: testInfo.outputPath('tcp-status-mobile.png') })
      expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth + 1)).toBeTruthy()
    } finally {
      if (jobId && headers) {
        const jobs = await (await request.get('/api/traceability/jobs', { headers })).json()
        const job = jobs.find((item: { id: number }) => item.id === jobId)
        if (job) await request.put(`/api/traceability/jobs/${jobId}`, { headers, data: { ...job, enabled: false } })
      }
      for (const socket of scannerSockets) socket.destroy()
      if (scannerServer.listening) await new Promise<void>(resolve => scannerServer.close(() => resolve()))
      await plc.close()
    }
  })
}

test('RS232 scanner settings remain usable on mobile and persist', async ({ page, request }, testInfo) => {
  const plc = await plcSimulator()
  try {
    const fixture = await setup(request, page, plc.port)
    await page.setViewportSize({ width: 390, height: 844 })
    await page.goto('/traceability')
    await page.getByRole('tab', { name: '任务配置', exact: true }).click()
    await page.locator('[data-test="trace-add"]').click()
    await page.locator('[data-test="trace-name"]').fill(`Serial-${plc.port}`)
    await select(page, 'trace-device', fixture.device.name)
    await select(page, 'trace-trigger', 'D100')
    await page.locator('[data-test="trace-source"]').getByText('RS232 扫码枪', { exact: true }).click()
    await page.locator('[data-test="trace-serial-port"]').fill('COM8')
    await page.locator('[data-test="trace-baud-rate"] input').fill('19200')
    await page.locator('[data-test="trace-field-name-0"]').fill('Current')
    await select(page, 'trace-field-tag-0', 'Current')
    await page.screenshot({ path: testInfo.outputPath('serial-config-mobile.png'), fullPage: true })
    expect(await page.getByRole('dialog').evaluate(element => element.scrollWidth <= element.clientWidth + 1)).toBeTruthy()
    await page.locator('[data-test="trace-save"]').click()
    await expect(page.getByRole('dialog')).not.toBeVisible()
    const jobs = await (await request.get('/api/traceability/jobs', { headers: fixture.headers })).json()
    const job = jobs.find((item: { name: string }) => item.name === `Serial-${plc.port}`)
    expect(job.snSource).toBe('Serial')
    expect(job.scanner.serialPort).toBe('COM8')
    expect(job.scanner.baudRate).toBe(19200)
    expect(job.enabled).toBe(false)
  } finally { await plc.close() }
})