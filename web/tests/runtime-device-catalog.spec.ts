import { test, expect, type APIRequestContext } from '@playwright/test'

async function login(request: APIRequestContext, userName: string, password: string) {
  const response = await request.post('/api/auth/login', { data: { userName, password } })
  expect(response.ok(), await response.text()).toBeTruthy()
  const body = await response.json()
  return { ...body, headers: { Authorization: `Bearer ${body.token}` } }
}

test('a failed device catalog does not look like missing control permission', async ({ page, request }) => {
  const admin = (await login(request, 'admin', 'admin123')).headers
  const suffix = Date.now()
  const post = async (url: string, data: unknown) => {
    const response = await request.post(url, { headers: admin, data })
    expect(response.ok(), await response.text()).toBeTruthy()
    return response.json()
  }
  const area = await post('/api/areas', { name: `DEV-A-${suffix}` })
  const role = await post('/api/roles', { name: `DEV-控制-${suffix}`, permissions: ['View', 'Control'] })
  const user = await post('/api/users', { userName: `devctl-${suffix}`, password: 'secret123', displayName: '区域控制', role: 'None', enabled: true })
  const assigned = await request.put(`/api/users/${user.id}/assignments`, { headers: admin, data: { assignments: [{ roleId: role.id, areaId: area.id }] } })
  expect(assigned.ok(), await assigned.text()).toBeTruthy()
  const port = 21000 + (suffix % 20000)
  const device = await post('/api/devices', {
    name: `DEV-D-${suffix}`, protocol: 'ModbusTcp', host: '127.0.0.1', port, stationNumber: 1,
    byteOrder: 'ABCD', pollIntervalMs: 1000, timeoutMs: 1000, enabled: false, areaId: area.id
  })
  const tag = await post('/api/tags', {
    deviceId: device.id, name: `DEV-T-${suffix}`, area: 'HoldingRegister', address: 0, dataType: 'UInt16',
    scale: 1, offset: 0, writable: true, archiveMode: 'None', archivePeriodSec: 60, deadband: 0
  })
  const label = `DEV-CMD-${suffix}`
  const screenName = `DEV-SCR-${suffix}`
  const screen = await post('/api/screens', { name: screenName })
  const saved = await request.put(`/api/screens/${screen.id}`, {
    headers: admin,
    data: {
      name: screenName, revision: screen.revision,
      document: {
        schemaVersion: 1, width: 800, height: 240, background: '#101b2d',
        nodes: [{
          id: 'cmd', type: 'button', x: 20, y: 40, width: 180, height: 48, rotation: 0, text: label,
          color: '#7394b8', activeColor: '#34d399', unit: '', decimals: 1, min: 0, max: 100, activeThreshold: 0.5, tagId: tag.id,
          action: { type: 'command', tagId: tag.id, screenId: null, value: 1 }
        }]
      }
    }
  })
  expect(saved.ok(), await saved.text()).toBeTruthy()
  const published = await request.post(`/api/screens/${screen.id}/publish`, { headers: admin, data: { revision: (await saved.json()).revision } })
  expect(published.ok(), await published.text()).toBeTruthy()

  await page.goto('/login')
  await page.getByPlaceholder(/用户名/).fill(user.userName)
  await page.getByPlaceholder(/密码/).fill('secret123')
  await page.keyboard.press('Enter')
  await expect(page).toHaveURL(/overview/)

  let fail = true
  await page.route((url) => url.pathname === '/api/devices', (route) => {
    if (fail && route.request().method() === 'GET') {
      return route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message: '设备列表不可用' }) })
    }
    return route.continue()
  })
  await page.goto(`/screens/${screen.id}`)
  const runtime = page.locator('[data-testid="screen-runtime"]')
  await expect(runtime.locator('[data-test="runtime-device-error"]')).toContainText('设备列表不可用')
  await runtime.getByRole('button', { name: label }).click()
  await expect(page.getByText('当前用户没有控制权限')).toHaveCount(0)
  await expect(page.locator('[data-test="control-confirm"]')).not.toBeVisible()

  fail = false
  await runtime.getByRole('button', { name: '刷新', exact: true }).click()
  await expect(runtime.locator('[data-test="runtime-device-error"]')).toHaveCount(0)
  await runtime.getByRole('button', { name: label }).click()
  await expect(page.locator('[data-test="control-confirm"]')).toBeVisible()
})
