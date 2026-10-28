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

test('recipe management: edit creates a version, pre-check, download through control pipeline, compare with live values', async ({ page, request }) => {
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
    const device = await (await request.post('/api/devices', { headers, data: { name: `配方站-${u}`, protocol: 'ModbusTcp', host: '127.0.0.1', port, stationNumber: 1, byteOrder: 'ABCD', pollIntervalMs: 200, timeoutMs: 500, enabled: true, areaId: 1 } })).json(); deviceId = device.id
    const tag = async (name: string, address: number) => {
      const r = await request.post('/api/tags', { headers, data: { deviceId: device.id, name, area: 'HoldingRegister', address, dataType: 'UInt16', scale: 1, offset: 0, writable: true, archiveMode: 'None', archivePeriodSec: 600, deadband: 0 } })
      expect(r.ok(), await r.text()).toBeTruthy(); return (await r.json()).id as number
    }
    const temp = await tag(`温度设定-${u}`, 0), speed = await tag(`转速设定-${u}`, 1)
    const t = await request.post('/api/recipes/templates', { headers, data: { name: `挤出-${u}`, areaId: 1, dualApproval: false, parameters: [
      { key: 'temp', name: '温度', tagId: temp, unit: '℃', min: 0, max: 300 }, { key: 'speed', name: '转速', tagId: speed, unit: 'rpm', min: 0, max: 1500 }] } })
    expect(t.ok(), await t.text()).toBeTruthy(); const template = await t.json()
    const created = await (await request.post('/api/recipes', { headers, data: { templateId: template.id, name: `PE-${u}`, values: [{ key: 'temp', value: 180 }, { key: 'speed', value: 900 }], note: '初版' } })).json()
    const recipeId = created.recipe.id as number
    await expect.poll(async () => (await (await request.get(`/api/realtime?tagIds=${temp}`, { headers })).json())[0]?.quality, { timeout: 15000 }).toBe('Good')

    // 1. 界面编辑：保存生成 v2
    await signIn(page, admin); await page.goto('/recipes')
    const row = page.locator('[data-test="recipes"] .el-table__row', { hasText: `PE-${u}` })
    await row.getByRole('button', { name: '编辑' }).click()
    await page.locator('[data-test="recipe-value-temp"] input').fill('200')
    await page.locator('[data-test="recipe-note"]').fill('提温')
    await page.locator('[data-test="recipe-save"]').click()
    await expect(row).toContainText('v2')
    await expect(row.locator('[data-test="recipe-parameter-temp"]')).toContainText('温度')
    await expect(row.locator('[data-test="recipe-parameter-temp"]')).toContainText('200')

    // 2. 预检查拒绝：挂“禁止操作”牌后确认按钮不可用，现场寄存器不变
    const lock = await (await request.post('/api/control/lockouts', { headers, data: { tagId: speed, kind: 'NoOperate', reason: '换模' } })).json()
    await page.locator(`[data-test="recipe-download-${recipeId}"]`).click()
    await expect(page.locator('[data-test="recipe-check"]')).toContainText('禁止操作')
    await expect(page.locator('[data-test="recipe-download-confirm"]')).toBeDisabled()
    await page.getByRole('dialog', { name: '下发配方确认' }).getByRole('button', { name: '取消' }).click()
    expect(registers[0]).toBe(0)
    expect((await request.post(`/api/control/lockouts/${lock.id}/remove`, { headers, data: { reason: '换模完成' } })).ok()).toBeTruthy()

    // 3. 下发：两个参数都写入并回读，记录显示完成
    await page.locator(`[data-test="recipe-download-${recipeId}"]`).click()
    await expect(page.locator('[data-test="recipe-check"]')).toContainText('200')
    await page.locator('[data-test="recipe-download-confirm"]').click()
    await expect(page.locator('[data-test="recipe-downloads"]')).toContainText('完成', { timeout: 20000 })
    expect(registers[0]).toBe(200); expect(registers[1]).toBe(900)
    await page.screenshot({ path: test.info().outputPath('recipe-download.png'), animations: 'disabled' })

    // 4. 与现场对比：一致
    await expect.poll(async () => (await (await request.get(`/api/realtime?tagIds=${speed}`, { headers })).json())[0]?.value, { timeout: 10000 }).toBe(900)
    await row.locator(`[data-test="recipe-more-${recipeId}"]`).click()
    await page.locator(`[data-test="recipe-compare-live-${recipeId}"]:visible`).click()
    await expect(page.getByText('0 处不同')).toBeVisible()
    const logs = await (await request.get('/api/logs?pageSize=200', { headers })).text()
    expect(logs).toContain(`配方下发结束 PE-${u} v2`)
  } finally {
    if (deviceId) await request.delete(`/api/devices/${deviceId}`, { headers }).catch(() => {})
    for (const s of sockets) s.destroy(); plc.close()
  }
})
