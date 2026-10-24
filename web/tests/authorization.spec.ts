import { test, expect, type APIRequestContext } from '@playwright/test'

async function login(request: APIRequestContext, userName: string, password: string) {
  const response = await request.post('/api/auth/login', { data: { userName, password } }); expect(response.ok(), await response.text()).toBeTruthy()
  const result = await response.json(); return { ...result, headers: { Authorization: `Bearer ${result.token}` } }
}

test('area-scoped operator: API filters and rejects cross-area access; UI hides config and admin entries', async ({ page, request }) => {
  test.setTimeout(60000)
  const admin = (await login(request, 'admin', 'admin123')).headers, suffix = Date.now()
  const post = async (url: string, data: unknown) => { const r = await request.post(url, { headers: admin, data }); expect(r.ok(), await r.text()).toBeTruthy(); return r.json() }
  const areaA = await post('/api/areas', { name: `E2E-A-${suffix}` }), areaB = await post('/api/areas', { name: `E2E-B-${suffix}` })
  const device = (name: string, areaId: number) => ({ name, protocol: 'ModbusTcp', host: '127.0.0.1', port: 1502, stationNumber: 1, byteOrder: 'ABCD', pollIntervalMs: 1000, timeoutMs: 500, enabled: false, areaId })
  const devA = await post('/api/devices', device(`E2E-devA-${suffix}`, areaA.id)), devB = await post('/api/devices', device(`E2E-devB-${suffix}`, areaB.id))
  const tag = (deviceId: number, name: string) => ({ deviceId, name, area: 'HoldingRegister', address: 0, dataType: 'Float', scale: 1, offset: 0, writable: true, archiveMode: 'None', deadband: 0, archivePeriodSec: 600 })
  const tagA = await post('/api/tags', tag(devA.id, 'A')), tagB = await post('/api/tags', tag(devB.id, 'B'))
  const role = await post('/api/roles', { name: `E2E操作员-${suffix}`, permissions: ['View', 'Control', 'AckAlarm'] })
  const user = await post('/api/users', { userName: `e2e-op-${suffix}`, password: 'secret123', displayName: 'A区操作员', role: 'None', enabled: true })
  const assigned = await request.put(`/api/users/${user.id}/assignments`, { headers: admin, data: { assignments: [{ roleId: role.id, areaId: areaA.id }] } })
  expect(assigned.ok()).toBeTruthy()

  const op = await login(request, user.userName, 'secret123')
  expect(op.user.access.global).toEqual([])
  expect(op.user.access.areas).toEqual([{ areaId: areaA.id, permissions: ['View', 'Control', 'AckAlarm'] }])
  const devices = await (await request.get('/api/devices', { headers: op.headers })).json()
  expect(devices.map((x: any) => x.id)).toEqual([devA.id])
  expect((await request.get(`/api/history?tagIds=${tagB.id}`, { headers: op.headers })).status()).toBe(403)
  expect((await request.post('/api/commands', { headers: op.headers, data: { tagId: tagB.id, value: 1, requestId: crypto.randomUUID() } })).status()).toBe(403)
  expect((await request.post('/api/tags', { headers: op.headers, data: tag(devA.id, 'X') })).status()).toBe(403)
  expect((await request.get(`/api/history?tagIds=${tagA.id}`, { headers: op.headers })).status()).toBe(200)

  // 审计里能看到被拒绝的控制
  const logs = await (await request.get(`/api/logs?keyword=${encodeURIComponent(`#${tagB.id}`)}`, { headers: admin })).json()
  expect(logs.items.some((x: any) => x.userName === user.userName && String(x.result).startsWith('拒绝'))).toBeTruthy()

  // UI：区域操作员看不到组态配置与系统管理入口
  await page.goto('/login')
  await page.getByPlaceholder(/用户名/).fill(user.userName)
  await page.getByPlaceholder(/密码/).fill('secret123')
  await page.keyboard.press('Enter')
  await expect(page).toHaveURL(/overview/)
  await expect(page.getByText('组态配置')).toHaveCount(0)
  await expect(page.getByText('系统管理')).toHaveCount(0)
  await page.goto('/system')
  await expect(page).toHaveURL(/overview/)

  // 管理员在系统管理里能看到“区域与角色”
  await page.evaluate(() => localStorage.clear())
  await page.goto('/login')
  await page.getByPlaceholder(/用户名/).fill('admin')
  await page.getByPlaceholder(/密码/).fill('admin123')
  await page.keyboard.press('Enter')
  await expect(page).toHaveURL(/overview/)
  await page.goto('/system')
  await page.getByRole('tab', { name: '区域与角色' }).click()
  await expect(page.locator('[data-test="area-table"]').getByText(`E2E-A-${suffix}`, { exact: true })).toBeVisible()
})
