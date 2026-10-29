import { test, expect, type APIRequestContext, type Page } from './fixtures'
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

test('field control loop: pre-check and confirm, interlock, lockout, dual approval and sequence through the UI', async ({ page, request }) => {
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
    const device = await (await request.post('/api/devices', { headers, data: { name: `控制站-${u}`, protocol: 'ModbusTcp', host: '127.0.0.1', port, stationNumber: 1, byteOrder: 'ABCD', pollIntervalMs: 200, timeoutMs: 500, enabled: true, areaId: 1 } })).json(); deviceId = device.id
    const tag = async (name: string, address: number) => {
      const r = await request.post('/api/tags', { headers, data: { deviceId: device.id, name, area: 'HoldingRegister', address, dataType: 'UInt16', scale: 1, offset: 0, writable: true, archiveMode: 'None', archivePeriodSec: 600, deadband: 0 } })
      expect(r.ok(), await r.text()).toBeTruthy(); return (await r.json()).id as number
    }
    const setpoint = await tag(`给定-${u}`, 0), permit = await tag(`就绪-${u}`, 1), pump = await tag(`泵-${u}`, 2), running = await tag(`运行反馈-${u}`, 3)
    const point = (extra: object) => ({ requireConfirm: true, dualApproval: false, approvalTimeoutSec: 300, permitExpression: null, permitMessage: null, limitMin: null, limitMax: null, maxStep: null, feedbackExpression: null, feedbackTimeoutMs: 10000, ...extra })
    expect((await request.put(`/api/control/points/${setpoint}`, { headers, data: point({ limitMin: 0, limitMax: 100, permitExpression: `#${permit} == 1`, permitMessage: '泵未就绪' }) })).ok()).toBeTruthy()
    await expect.poll(async () => (await (await request.get(`/api/realtime?tagIds=${permit}`, { headers })).json())[0]?.quality, { timeout: 15000 }).toBe('Good')

    // 1. 实时监视写值：预检查拒绝（限幅 + 互锁），确认按钮不可用；条件满足后确认下发并回读。
    await signIn(page, admin); await page.goto('/realtime')
    const row = page.getByRole('row').filter({ hasText: `给定-${u}` })
    await row.getByRole('button', { name: '写值', exact: true }).click()
    await page.getByRole('dialog').getByRole('spinbutton').fill('150')
    await page.getByRole('dialog').getByRole('button', { name: '下发', exact: true }).click()
    const confirm = page.locator('[data-test="control-confirm"]')
    await expect(confirm).toContainText('超出操作限幅'); await expect(confirm).toContainText('泵未就绪')
    await expect(page.locator('[data-test="control-confirm-ok"]')).toBeDisabled()
    await page.screenshot({ path: test.info().outputPath('control-refused.png'), animations: 'disabled' })
    await confirm.getByRole('button', { name: '取消' }).click()
    registers[1] = 1; await expect.poll(async () => (await (await request.get(`/api/realtime?tagIds=${permit}`, { headers })).json())[0]?.value, { timeout: 10000 }).toBe(1)
    await page.getByRole('dialog').getByRole('spinbutton').fill('40')
    await page.getByRole('dialog').getByRole('button', { name: '下发', exact: true }).click()
    await expect(confirm).toContainText('40'); await page.locator('[data-test="control-confirm-ok"]').click()
    await expect(page.getByText('写入成功', { exact: false })).toBeVisible({ timeout: 15000 }); expect(registers[0]).toBe(40)
    // 绕过界面直接调接口但不带确认标记：拒绝
    const raw = await (await request.post('/api/commands', { headers, data: { tagId: setpoint, value: 41, requestId: randomUUID() } })).json()
    expect(raw.success).toBe(false); expect(raw.message).toContain('确认')

    // 2. 挂牌：界面挂“禁止操作”，下发被拒；摘牌后恢复。
    await page.goto('/control'); await page.getByRole('tab', { name: '挂牌' }).click()
    await page.locator('[data-test="lock-target"]').click(); await page.getByRole('option', { name: `控制站-${u}/给定-${u}` }).click()
    await page.locator('[data-test="lock-reason"]').fill('管线检漏')
    await page.locator('[data-test="lock-place"]').click()
    await expect(page.locator('[data-test="lockouts"]')).toContainText('管线检漏')
    const locked = await (await request.post('/api/commands', { headers, data: { tagId: setpoint, value: 42, requestId: randomUUID(), confirmed: true } })).json()
    expect(locked.message).toContain('禁止操作')
    const lockRow = page.locator('[data-test="lockouts"] .el-table__row', { hasText: '管线检漏' })
    await lockRow.getByRole('button', { name: '摘牌' }).click()
    await page.locator('.el-message-box input').fill('检漏完成'); await page.locator('.el-message-box').getByRole('button', { name: 'OK' }).or(page.locator('.el-message-box').getByRole('button', { name: '确定' })).first().click()
    await expect(page.locator('[data-test="lockouts"]')).not.toContainText('管线检漏')

    // 3. 双人复核：管理员申请，另一位操作员在界面批准后执行。
    expect((await request.put(`/api/control/points/${setpoint}`, { headers, data: point({ limitMax: 100, dualApproval: true }) })).ok()).toBeTruthy()
    const pendingResult = await (await request.post('/api/commands', { headers, data: { tagId: setpoint, value: 55, requestId: randomUUID(), confirmed: true } })).json()
    expect(pendingResult.pending).toBe(true); expect(registers[0]).toBe(40)
    const reviewer = `review-${u}`
    expect((await request.post('/api/users', { headers, data: { userName: reviewer, password: 'test-password-123', displayName: '复核员', role: 'Operator', enabled: true } })).status()).toBe(201)
    await signIn(page, await auth(request, reviewer, 'test-password-123')); await page.goto('/control')
    await page.locator(`[data-test="approve-${pendingResult.approvalId}"]`).click()
    await page.locator('.el-message-box input').fill('已与现场核对')
    await page.locator('.el-message-box').getByRole('button', { name: '批准' }).click()
    await expect(page.getByText('已执行', { exact: false }).first()).toBeVisible({ timeout: 15000 }); expect(registers[0]).toBe(55)

    // 4. 顺控：启泵 → 等运行反馈；界面执行并看到完成。
    const sequence = await (await request.post('/api/control/sequences', { headers, data: { name: `启泵-${u}`, areaId: 1, dualApproval: false, steps: [
      { name: '启泵', tagId: pump, value: 1, confirmExpression: `#${pump} == 1`, timeoutMs: 5000, delayMs: 0 },
      { name: '置运行', tagId: running, value: 1, confirmExpression: `#${running} == 1`, timeoutMs: 5000, delayMs: 0 }] } })).json()
    await signIn(page, admin); await page.goto('/control'); await page.getByRole('tab', { name: '顺控' }).click()
    await page.locator(`[data-test="sequence-start-${sequence.id}"]`).click()
    await page.locator('.el-message-box').getByRole('button', { name: '确认执行' }).click()
    await expect(page.locator('[data-test="runs"]')).toContainText('完成', { timeout: 20000 })
    expect(registers[2]).toBe(1); expect(registers[3]).toBe(1)
    await page.screenshot({ path: test.info().outputPath('control-sequence.png'), animations: 'disabled' })
    const logs = await (await request.get('/api/logs?pageSize=200', { headers })).text()
    expect(logs).toContain(`顺控结束 启泵-${u}`)
  } finally {
    if (deviceId) await request.delete(`/api/devices/${deviceId}`, { headers }).catch(() => {})
    for (const s of sockets) s.destroy(); plc.close()
  }
})
