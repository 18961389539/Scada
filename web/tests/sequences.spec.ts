import { test, expect, type APIRequestContext, type Page } from '@playwright/test'
import { createServer, type Socket } from 'node:net'
import { once } from 'node:events'
import { randomUUID } from 'node:crypto'

async function auth(request: APIRequestContext, userName = 'admin', password = 'admin123') {
  const response = await request.post('/api/auth/login', { data: { userName, password } }); expect(response.ok()).toBeTruthy()
  const result = await response.json(); return { ...result, headers: { Authorization: `Bearer ${result.token}` } }
}
async function signIn(page: Page, session: { token: string; user: unknown }) {
  await page.goto('/login'); await page.evaluate(s => { localStorage.setItem('scada_token', s.token); localStorage.setItem('scada_user', JSON.stringify(s.user)) }, session)
}

test('branching sequence: editor shows step graph, condition and parallel run through the UI, invalid loop rejected', async ({ page, request }) => {
  test.setTimeout(120000)
  // 简易 Modbus TCP 从站：保持寄存器 0..9，支持 03 读与 16 写。
  const registers = new Array(10).fill(0), sockets = new Set<Socket>()
  const plc = createServer(socket => {
    sockets.add(socket); socket.on('close', () => sockets.delete(socket)); socket.on('error', () => {})
    let pending = Buffer.alloc(0)
    socket.on('data', chunk => {
      pending = Buffer.concat([pending, chunk])
      while (pending.length >= 7 && pending.length >= 6 + pending.readUInt16BE(4)) {
        const size = 6 + pending.readUInt16BE(4), frame = pending.subarray(0, size); pending = pending.subarray(size)
        const fn = frame[7]; let pdu: Buffer
        if (fn === 3) {
          const start = frame.readUInt16BE(8), count = frame.readUInt16BE(10); pdu = Buffer.alloc(2 + count * 2); pdu[0] = 3; pdu[1] = count * 2
          for (let n = 0; n < count; n++) pdu.writeUInt16BE(registers[start + n] ?? 0, 2 + n * 2)
        } else if (fn === 16) {
          const start = frame.readUInt16BE(8), count = frame.readUInt16BE(10)
          for (let n = 0; n < count; n++) registers[start + n] = frame.readUInt16BE(13 + n * 2)
          pdu = Buffer.from(frame.subarray(7, 12))
        } else pdu = Buffer.from([fn! | 0x80, 1])
        const header = Buffer.from(frame.subarray(0, 7)); header.writeUInt16BE(pdu.length + 1, 4); socket.write(Buffer.concat([header, pdu]))
      }
    })
  })
  plc.listen(0, '127.0.0.1'); await once(plc, 'listening'); const port = (plc.address() as { port: number }).port
  const admin = await auth(request), headers = admin.headers, u = `${Date.now()}`.slice(-6)
  let deviceId: number | undefined
  try {
    const device = await (await request.post('/api/devices', { headers, data: { name: `分支站-${u}`, protocol: 'ModbusTcp', host: '127.0.0.1', port, stationNumber: 1, byteOrder: 'ABCD', pollIntervalMs: 200, timeoutMs: 500, enabled: true, areaId: 1 } })).json(); deviceId = device.id
    const tag = async (name: string, address: number) => {
      const r = await request.post('/api/tags', { headers, data: { deviceId: device.id, name, area: 'HoldingRegister', address, dataType: 'UInt16', scale: 1, offset: 0, writable: true, archiveMode: 'None', archivePeriodSec: 600, deadband: 0 } })
      expect(r.ok(), await r.text()).toBeTruthy(); return (await r.json()).id as number
    }
    const mode = await tag(`模式-${u}`, 0), a = await tag(`甲阀-${u}`, 1), b = await tag(`乙阀-${u}`, 2), c = await tag(`丙泵-${u}`, 3), d = await tag(`丁泵-${u}`, 4)
    await expect.poll(async () => (await (await request.get(`/api/realtime?tagIds=${mode}`, { headers })).json())[0]?.quality, { timeout: 15000 }).toBe('Good')
    const w = (id: string, tagId: number, value: number, next?: string) => ({ id, type: 'write', name: id, tagId, value, confirmExpression: `#${tagId} == ${value}`, timeoutMs: 5000, delayMs: 0, next })
    // 无上限循环被拒绝
    const bad = await request.post('/api/control/sequences', { headers, data: { name: `坏-${u}`, areaId: 1, dualApproval: false, steps: [w('s1', a, 1), w('s2', b, 1, 's1')] } })
    expect(bad.status()).toBe(400); expect(await bad.text()).toContain('没有次数上限的循环')
    const seq = await (await request.post('/api/control/sequences', { headers, data: { name: `分支-${u}`, areaId: 1, dualApproval: false, steps: [
      { id: 'sel', type: 'condition', name: '选模式', expression: `#${mode} == 1`, then: 'wa', else: 'wb' },
      w('wa', a, 1, 'par'), w('wb', b, 1, 'par'),
      { id: 'par', type: 'parallel', name: '双泵', branches: ['pc', 'pd'], join: 'j' },
      w('pc', c, 1, 'j'), w('pd', d, 1, 'j'),
      { id: 'j', type: 'join', name: '汇合', joinMode: 'all' }] } })).json()
    expect(seq.id).toBeTruthy()

    await signIn(page, admin); await page.goto('/control'); await page.getByRole('tab', { name: '顺控' }).click()
    const row = page.locator('[data-test="sequences"] .el-table__row', { hasText: `分支-${u}` })
    await expect(row).toContainText('[条件分支]'); await expect(row).toContainText('[并行]')
    // 编辑器原样保存（往返不丢分支字段）
    await row.getByRole('button', { name: '编辑' }).click()
    await expect(page.locator('[data-test="sequence-steps"]')).toContainText('成立→')
    await expect(page.locator('[data-test="step-expression"]').first()).toHaveValue(`#${mode} == 1`)
    await page.screenshot({ path: test.info().outputPath('sequence-editor.png'), animations: 'disabled' })
    await page.locator('[data-test="sequence-save"]').click()
    await expect(page.getByText('已保存')).toBeVisible()
    const saved = (await (await request.get('/api/control/sequences', { headers })).json()).find((x: { id: number }) => x.id === seq.id)
    expect(saved.steps[0].else).toBe('wb'); expect(saved.steps[3].branches).toEqual(['pc', 'pd'])

    await page.locator(`[data-test="sequence-start-${seq.id}"]`).click()
    await page.locator('.el-message-box').getByRole('button', { name: '确认执行' }).click()
    await expect(page.locator('[data-test="runs"]')).toContainText('完成', { timeout: 20000 })
    expect(registers[1]).toBe(0); expect(registers[2]).toBe(1); expect(registers[3]).toBe(1); expect(registers[4]).toBe(1)
    await expect(page.locator('[data-test="runs"]')).toContainText('跳过')
    await page.screenshot({ path: test.info().outputPath('sequence-run.png'), animations: 'disabled' })
  } finally {
    if (deviceId) await request.delete(`/api/devices/${deviceId}`, { headers }).catch(() => {})
    for (const s of sockets) s.destroy(); plc.close()
  }
})
