import { test, expect, type APIRequestContext } from '@playwright/test'
import { createServer, type Socket } from 'node:net'
import { once } from 'node:events'
import { randomUUID } from 'node:crypto'

async function auth(request: APIRequestContext) {
  const response = await request.post('/api/auth/login', { data: { userName: 'admin', password: 'admin123' } })
  expect(response.ok()).toBeTruthy()
  const body = await response.json()
  return { token: body.token as string, headers: { Authorization: `Bearer ${body.token}` } }
}

// 控制页易用性：待审批请求的有效期可见、审批弹窗带请求摘要、列表自动刷新（不必手动点刷新）。
test('control usability: request expiry, approval summary and auto refresh', async ({ page, request }) => {
  test.setTimeout(150000)
  // 简易 Modbus TCP 从站：保持寄存器支持 03 读与 16 写
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
        } else pdu = Buffer.from([(fn ?? 0) | 0x80, 1])
        const header = Buffer.from(frame.subarray(0, 7)); header.writeUInt16BE(pdu.length + 1, 4); socket.write(Buffer.concat([header, pdu]))
      }
    })
  })
  plc.listen(0, '127.0.0.1'); await once(plc, 'listening')
  const port = (plc.address() as { port: number }).port
  const session = await auth(request)
  const h = session.headers
  const suffix = `${Date.now()}`.slice(-6)

  const device = await (await request.post('/api/devices', {
    headers: h,
    data: { name: `UX-CTL-${suffix}`, protocol: 'ModbusTcp', host: '127.0.0.1', port, stationNumber: 1, byteOrder: 'ABCD', pollIntervalMs: 200, timeoutMs: 500, enabled: true, areaId: 1 }
  })).json()

  const mkTag = async (name: string, address: number) => {
    const r = await request.post('/api/tags', {
      headers: h,
      data: { deviceId: device.id, name, area: 'HoldingRegister', address, dataType: 'UInt16', scale: 1, offset: 0, writable: true, archiveMode: 'None', archivePeriodSec: 600, deadband: 0 }
    })
    expect(r.ok(), await r.text()).toBeTruthy()
    return (await r.json()).id as number
  }
  const spA = await mkTag(`UX设定A-${suffix}`, 0)
  const spB = await mkTag(`UX设定B-${suffix}`, 1)

  // 两个测点都启用双人复核：提交命令不会立即执行，而是生成待审批请求
  const point = (tagId: number) => ({
    tagId, requireConfirm: true, dualApproval: true, approvalTimeoutSec: 300, permitExpression: null, permitMessage: null,
    limitMin: null, limitMax: null, maxStep: null, feedbackExpression: null, feedbackTimeoutMs: 10000
  })
  for (const tagId of [spA, spB]) {
    expect((await request.put(`/api/control/points/${tagId}`, { headers: h, data: point(tagId) })).ok()).toBeTruthy()
  }
  // 设备必须先在线，否则预检查就拒绝，不会进入待审批
  await expect.poll(async () => (await (await request.get(`/api/realtime?tagIds=${spA}`, { headers: h })).json())[0]?.quality, { timeout: 20000 }).toBe('Good')

  const submit = async (tagId: number) => {
    const r = await request.post('/api/commands', { headers: h, data: { tagId, value: 55, requestId: randomUUID(), confirmed: true } })
    const body = await r.json()
    expect(body.pending, JSON.stringify(body)).toBe(true)
    return body.approvalId as string
  }
  const approvalA = await submit(spA)

  // 自己不能批自己的请求（mine=true 不显示批准按钮）：另建一个操作员来复核
  const reviewer = `ux-review-${suffix}`
  expect((await request.post('/api/users', { headers: h, data: { userName: reviewer, password: 'test-password-123', displayName: '复核员', role: 'Operator', enabled: true } })).status()).toBe(201)

  await page.goto('/login')
  await page.getByPlaceholder(/用户名/).fill(reviewer)
  await page.getByPlaceholder(/密码/).fill('test-password-123')
  await page.keyboard.press('Enter')
  await expect(page).toHaveURL(/overview/)
  await page.goto('/control')

  // 有效期可见（此前 expiresTs 完全没展示）
  const requests = page.locator('[data-test="control-requests"]')
  await expect(requests).toContainText('剩余')

  // 审批弹窗带上请求摘要，避免模态框遮住表格后盲批
  await page.locator(`[data-test="approve-${approvalA}"]`).click()
  const box = page.locator('.el-message-box')
  await expect(box).toContainText('申请人')
  await expect(box).toContainText('剩余')
  await box.getByRole('button', { name: '取消' }).click()

  // 自动刷新：页面停留期间新提交的请求应自行出现，无需手动点刷新
  const rows = page.locator('[data-test="control-requests"] .el-table__body tr')
  await expect(rows).toHaveCount(1)
  await submit(spB)
  await expect(rows).toHaveCount(2, { timeout: 15000 })

  for (const s of sockets) s.destroy()
  plc.close()
})
