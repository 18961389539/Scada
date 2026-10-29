import { test, expect, type APIRequestContext, type Page } from './fixtures'

async function login(request: APIRequestContext) {
  const r = await request.post('/api/auth/login', { data: { userName: 'admin', password: 'admin123' } })
  expect(r.ok()).toBeTruthy()
  const result = await r.json()
  return { ...result, headers: { Authorization: `Bearer ${result.token}` } }
}

async function signIn(page: Page, auth: { token: string; user: unknown }) {
  await page.addInitScript(({ token, user }) => {
    localStorage.setItem('scada_token', token)
    localStorage.setItem('scada_user', JSON.stringify(user))
  }, auth)
}

test('forms UX: tag defaults + validation; alarm rule batch disable/enable/delete; tag batch delete', async ({ page, request }) => {
  test.setTimeout(90000)
  const auth = await login(request)
  const h = auth.headers
  const suffix = `${Date.now()}`

  const deviceRes = await request.post('/api/devices', {
    headers: h,
    data: {
      name: `UX-Dev-${suffix}`, protocol: 'ModbusTcp', host: '127.0.0.1', port: 1502,
      stationNumber: 1, byteOrder: 'ABCD', pollIntervalMs: 1000, timeoutMs: 3000, enabled: false, areaId: 1
    }
  })
  expect(deviceRes.ok(), await deviceRes.text()).toBeTruthy()
  const device = await deviceRes.json()

  const mkTag = async (name: string, address: number) => {
    const r = await request.post('/api/tags', {
      headers: h,
      data: {
        deviceId: device.id, name, area: 'HoldingRegister', address, dataType: 'Float',
        scale: 1, offset: 0, writable: false, archiveMode: 'OnChange', deadband: 0, archivePeriodSec: 600
      }
    })
    expect(r.ok(), await r.text()).toBeTruthy()
    return r.json()
  }
  const tagA = await mkTag(`UX-A-${suffix}`, 10)
  const tagB = await mkTag(`UX-B-${suffix}`, 12)

  const mkRule = async (tagId: number, message: string) => {
    const r = await request.post('/api/alarm-rules', {
      headers: h,
      data: {
        tagId, type: 'High', threshold: 99, level: 'Warning', delaySec: 3, recoverDeadband: 0,
        enabled: true, message, offDelaySec: 0, suppressMode: 'None'
      }
    })
    expect(r.ok(), await r.text()).toBeTruthy()
    return r.json()
  }
  const ruleA = await mkRule(tagA.id, `UX-RA-${suffix}`)
  const ruleB = await mkRule(tagB.id, `UX-RB-${suffix}`)

  await signIn(page, auth)

  // Tag create: defaults tip + save disabled when name empty
  await page.goto('/config/tags')
  await page.locator('[data-test="tag-create"]').click()
  await expect(page.locator('[data-test="tag-defaults-tip"]')).toBeVisible()
  await expect(page.locator('[data-test="tag-archive-period"] input')).toHaveValue('600')
  await page.locator('[data-test="tag-name"]').fill('')
  await expect(page.locator('[data-test="tag-save"]')).toBeDisabled()
  await page.keyboard.press('Escape')

  // Alarm rules page: defaults tip
  await page.goto('/config/rules')
  await page.locator('[data-test="rule-create"]').click()
  await expect(page.locator('[data-test="rule-defaults-tip"]')).toBeVisible()
  await expect(page.locator('[data-test="rule-delay"] input')).toHaveValue('3')
  await page.keyboard.press('Escape')

  const rowA = page.getByRole('row').filter({ hasText: `UX-RA-${suffix}` })
  const rowB = page.getByRole('row').filter({ hasText: `UX-RB-${suffix}` })
  await expect(rowA).toBeVisible()
  await expect(rowB).toBeVisible()

  await rowA.locator('.el-checkbox__inner').click()
  await rowB.locator('.el-checkbox__inner').click()
  await expect(page.locator('[data-test="rule-batch-count"]')).toContainText('2')

  await page.locator('[data-test="rule-batch-disable"]').click()
  await expect.poll(async () => {
    const list = await (await request.get('/api/alarm-rules', { headers: h })).json()
    return list.find((x: { id: number }) => x.id === ruleA.id)?.enabled === false
      && list.find((x: { id: number }) => x.id === ruleB.id)?.enabled === false
  }, { timeout: 15000 }).toBe(true)

  await rowA.locator('.el-checkbox__inner').click()
  await rowB.locator('.el-checkbox__inner').click()
  await page.locator('[data-test="rule-batch-enable"]').click()
  await expect.poll(async () => {
    const list = await (await request.get('/api/alarm-rules', { headers: h })).json()
    return list.find((x: { id: number }) => x.id === ruleA.id)?.enabled === true
      && list.find((x: { id: number }) => x.id === ruleB.id)?.enabled === true
  }, { timeout: 15000 }).toBe(true)

  await rowA.locator('.el-checkbox__inner').click()
  await rowB.locator('.el-checkbox__inner').click()
  await page.locator('[data-test="rule-batch-delete"]').click()
  await page.locator('.el-message-box').getByRole('button', { name: /删除|Delete/ }).click()
  await expect.poll(async () => {
    const list = await (await request.get('/api/alarm-rules', { headers: h })).json()
    return list.every((x: { id: number }) => x.id !== ruleA.id && x.id !== ruleB.id)
  }, { timeout: 15000 }).toBe(true)

  // Tag batch delete
  await page.goto('/config/tags')
  await page.getByPlaceholder(/搜索测点|Search tags/i).fill(`UX-A-${suffix}`)
  await page.getByRole('button', { name: /查询|Search/ }).first().click()
  const tRowA = page.getByRole('row').filter({ hasText: `UX-A-${suffix}` })
  await expect(tRowA).toBeVisible()
  await tRowA.locator('.el-checkbox__inner').click()

  await page.getByPlaceholder(/搜索测点|Search tags/i).fill(`UX-B-${suffix}`)
  await page.getByRole('button', { name: /查询|Search/ }).first().click()
  const tRowB = page.getByRole('row').filter({ hasText: `UX-B-${suffix}` })
  await expect(tRowB).toBeVisible()
  // Selection clears on reload — select both via search for UX-
  await page.getByPlaceholder(/搜索测点|Search tags/i).fill(`UX-`)
  await page.getByRole('button', { name: /查询|Search/ }).first().click()
  await expect(page.getByRole('row').filter({ hasText: `UX-A-${suffix}` })).toBeVisible()
  await expect(page.getByRole('row').filter({ hasText: `UX-B-${suffix}` })).toBeVisible()
  await page.getByRole('row').filter({ hasText: `UX-A-${suffix}` }).locator('.el-checkbox__inner').click()
  await page.getByRole('row').filter({ hasText: `UX-B-${suffix}` }).locator('.el-checkbox__inner').click()
  await expect(page.locator('[data-test="tag-batch-count"]')).toContainText('2')
  await page.locator('[data-test="tag-batch-delete"]').click()
  await page.locator('.el-message-box').getByRole('button', { name: /删除|Delete/ }).click()
  await expect.poll(async () => {
    const list = await (await request.get(`/api/tags?deviceId=${device.id}`, { headers: h })).json()
    return list.every((x: { id: number }) => x.id !== tagA.id && x.id !== tagB.id)
  }, { timeout: 15000 }).toBe(true)

  await request.delete(`/api/devices/${device.id}`, { headers: h })
})
