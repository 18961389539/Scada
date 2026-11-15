import { test, expect, type APIRequestContext, type Page } from './fixtures'

async function login(request: APIRequestContext) {
  const response = await request.post('/api/auth/login', { data: { userName: 'admin', password: 'admin123' } })
  expect(response.ok(), await response.text()).toBeTruthy()
  return await response.json()
}
async function sign(page: Page, auth: { token: string; user: unknown }): Promise<void> {
  await page.addInitScript(({ token, user }) => {
    localStorage.setItem('scada_token', token)
    localStorage.setItem('scada_user', JSON.stringify(user))
    localStorage.setItem('scada_locale', 'zh-CN')
  }, auth)
}

// 测点被画面草稿引用时，删除前先展示引用清单，并可把引用批量替换到另一个测点。
test('tag usage dialog lists screen drafts and rebinds before deletion', async ({ page, request }) => {
  const auth = await login(request)
  const headers = { Authorization: `Bearer ${auth.token}` }
  const suffix = `${Date.now()}`
  const device = await (await request.post('/api/devices', { headers, data: {
    name: `usage-${suffix}`, protocol: 'ModbusTcp', host: '127.0.0.1', port: 1502, stationNumber: 1,
    byteOrder: 'ABCD', pollIntervalMs: 1000, timeoutMs: 1000, enabled: false
  } })).json()
  async function tag(name: string, address: number) {
    const response = await request.post('/api/tags', { headers, data: {
      deviceId: device.id, name, area: 'HoldingRegister', address, dataType: 'Float', scale: 1, offset: 0,
      writable: false, archiveMode: 'None', deadband: 0, archivePeriodSec: 600
    } })
    expect(response.ok(), await response.text()).toBeTruthy()
    return await response.json()
  }
  const source = await tag(`原测点${suffix}`, 0)
  const target = await tag(`目标测点${suffix}`, 1)
  const screenName = `引用画面${suffix}`
  const created = await (await request.post('/api/screens', { headers, data: { name: screenName } })).json()
  const saved = await request.put(`/api/screens/${created.id}`, { headers, data: { name: screenName, revision: created.revision, document: {
    schemaVersion: 1, width: 1200, height: 720, background: '#101b2d', nodes: [{
      id: 'value', type: 'value', x: 40, y: 40, width: 220, height: 72, rotation: 0, text: screenName,
      color: '#7394b8', activeColor: '#34d399', unit: '', decimals: 1, min: 0, max: 100,
      activeThreshold: .5, tagId: source.id, action: { type: 'none', tagId: null, screenId: null, value: 1 }
    }]
  } } })
  expect(saved.ok(), await saved.text()).toBeTruthy()

  await sign(page, auth)
  await page.goto(`/config/tags?search=${encodeURIComponent(source.name)}`)
  const row = page.getByRole('row').filter({ hasText: source.name })
  await expect(row).toBeVisible()
  await row.locator('[data-test="tag-more"]').click()
  await page.locator('[data-test="tag-usage"]:visible').click()
  await expect(page.locator('[data-test="tag-usage-dialog"]')).toBeVisible()
  await expect(page.locator('[data-test="tag-usage-screenDraft"]')).toContainText(screenName)

  await page.locator('[data-test="tag-usage-target"]').click()
  await page.locator('.el-select-dropdown__item:visible').filter({ hasText: target.name }).click()
  await page.locator('[data-test="tag-usage-replace"]').click()
  await page.locator('.el-message-box__btns .el-button--primary').click()
  await expect(page.locator('[data-test="tag-usage-result"]')).toContainText('已替换 1 处引用')

  const deleted = await request.delete(`/api/tags/${source.id}`, { headers })
  expect(deleted.status()).toBe(204)
})
