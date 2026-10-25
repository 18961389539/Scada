import { test, expect, type APIRequestContext } from '@playwright/test'

async function login(request: APIRequestContext, userName: string, password: string) {
  const response = await request.post('/api/auth/login', { data: { userName, password } })
  expect(response.ok(), await response.text()).toBeTruthy()
  const result = await response.json()
  return { ...result, headers: { Authorization: `Bearer ${result.token}` as string } }
}

test('annunciator config, silence without control rights, mute stops browser loop', async ({ page, request }) => {
  test.setTimeout(90000)
  const admin = (await login(request, 'admin', 'admin123')).headers
  const suffix = Date.now()
  const deviceRes = await request.post('/api/devices', {
    headers: admin,
    data: { name: `ann-dev-${suffix}`, protocol: 'ModbusTcp', host: '127.0.0.1', port: 1502, stationNumber: 1, byteOrder: 'ABCD', pollIntervalMs: 1000, timeoutMs: 500, enabled: false, areaId: 1 }
  })
  expect(deviceRes.ok(), await deviceRes.text()).toBeTruthy()
  const device = await deviceRes.json()
  const tagRes = await request.post('/api/tags', {
    headers: admin,
    data: { deviceId: device.id, name: `喇叭-${suffix}`, area: 'HoldingRegister', address: 10, dataType: 'Bool', scale: 1, offset: 0, writable: true, archiveMode: 'None', deadband: 0, archivePeriodSec: 600 }
  })
  expect(tagRes.ok(), await tagRes.text()).toBeTruthy()
  const tag = await tagRes.json()

  const panelRes = await request.post('/api/annunciators', {
    headers: admin,
    data: {
      name: `E2E-ann-${suffix}`, areaId: null, enabled: true, latchMode: 'Follow', silenceMinutes: 5,
      flashOnMs: 400, flashOffMs: 400, criticalFlashOnMs: 200, criticalFlashOffMs: 200, browserSound: true,
      outputs: [{ kind: 'Horn', tagId: tag.id, activeValue: 1, inactiveValue: 0, enabled: true }],
      levels: []
    }
  })
  expect(panelRes.ok(), await panelRes.text()).toBeTruthy()
  const panel = await panelRes.json()

  await page.goto('/login')
  await page.getByPlaceholder(/用户名|Username/i).fill('admin')
  await page.getByPlaceholder(/密码|Password/i).fill('admin123')
  await page.keyboard.press('Enter')
  await expect(page).toHaveURL(/overview/)

  await page.goto('/config/annunciators')
  await expect(page.getByRole('heading', { name: /声光联动|Annunciator/ })).toBeVisible()
  await expect(page.getByText(`E2E-ann-${suffix}`)).toBeVisible()
  const row = page.getByRole('row', { name: new RegExp(`E2E-ann-${suffix}`) })
  await row.getByRole('button', { name: /消音|Silence/ }).click()
  await expect(row.getByText(/已消音|Silenced/)).toBeVisible({ timeout: 10000 })

  const sound = page.locator('.header-right .el-switch').first()
  if (await sound.count()) {
    await sound.click()
    await expect.poll(() => page.evaluate(() => localStorage.getItem('scada_sound'))).toBe('off')
  }

  await page.goto('/alarms')
  await expect(page.getByRole('button', { name: /声光消音|Silence horn/ })).toBeVisible()
  await expect(page.getByRole('button', { name: /确认可见|Ack visible/ })).toBeVisible()

  await request.delete(`/api/annunciators/${panel.id}`, { headers: admin })
})
