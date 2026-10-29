import { test, expect, type APIRequestContext, type Page } from './fixtures'

const cases = [
  { kind: 'device', page: '/config/devices', api: '/api/devices', field: 'device-name', initial: '[data-test="device-name"]', error: 'device-form-error' },
  { kind: 'tag', page: '/config/tags', api: '/api/tags', field: 'tag-name', initial: '[data-test="tag-name"]', error: 'tag-form-error' },
  { kind: 'rule', page: '/config/rules', api: '/api/alarm-rules', field: 'rule-message', initial: '[data-test="rule-threshold"] input', error: 'rule-save-error' }
] as const
type Case = typeof cases[number]

async function openEditor(page: Page, request: APIRequestContext, config: Case) {
  const login = await request.post('/api/auth/login', { data: { userName: 'admin', password: 'admin123' } })
  expect(login.ok()).toBeTruthy()
  const session = await login.json()
  const headers = { Authorization: `Bearer ${session.token}` }
  const post = async (path: string, data: unknown) => {
    const response = await request.post(path, { headers, data })
    expect(response.ok(), await response.text()).toBeTruthy()
    return response.json()
  }
  const suffix = Date.now()
  const device = await post('/api/devices', {
    name: `Editor-Device-${suffix}`, protocol: 'ModbusTcp', host: '127.0.0.1', port: 1502,
    stationNumber: 1, byteOrder: 'ABCD', pollIntervalMs: 1000, timeoutMs: 1000, enabled: false, areaId: 1
  })
  const tag = await post('/api/tags', {
    deviceId: device.id, name: `Editor-Tag-${suffix}`, area: 'HoldingRegister', address: 0,
    dataType: 'UInt16', scale: 1, offset: 0, writable: false, archiveMode: 'None', archivePeriodSec: 600, deadband: 0
  })
  const rule = await post('/api/alarm-rules', {
    tagId: tag.id, type: 'High', threshold: 90, level: 'Warning', delaySec: 3, recoverDeadband: 0,
    enabled: false, message: `Editor-Rule-${suffix}`, offDelaySec: 0, suppressMode: 'None'
  })
  const entity = { device, tag, rule }[config.kind]
  const label = config.kind === 'rule' ? entity.message : entity.name
  await page.addInitScript(({ token, user }) => {
    localStorage.setItem('scada_token', token)
    localStorage.setItem('scada_user', JSON.stringify(user))
  }, session)
  await page.goto(config.page)
  const row = page.getByRole('row').filter({ hasText: label })
  await row.getByRole('button', { name: '编辑', exact: true }).click()
  const dialog = page.locator(`[data-test="${config.kind}-dialog"]`)
  await expect(dialog).toBeVisible()
  await expect(dialog.locator(config.initial)).toBeFocused()
  return { dialog, input: dialog.locator(`[data-test="${config.field}"]`), row, entity, headers }
}

for (const config of cases) {
  test(`${config.kind}: unsaved changes survive cancel, unchanged and reverted forms close without a warning`, async ({ page, request }) => {
    const { dialog, input, row } = await openEditor(page, request, config)
    const original = await input.inputValue()
    await input.fill(`${original}-edited`)
    await dialog.getByRole('button', { name: '取消', exact: true }).click()
    const warning = page.locator('.el-message-box')
    await expect(warning).toContainText('尚有未保存的修改')
    await warning.getByRole('button', { name: '继续编辑' }).click()
    await expect(dialog).toBeVisible()
    await expect(input).toHaveValue(`${original}-edited`)
    await input.fill(original)
    await dialog.getByRole('button', { name: '取消', exact: true }).click()
    await expect(dialog).toBeHidden()
    await expect(warning).toBeHidden()

    await row.getByRole('button', { name: '编辑', exact: true }).click()
    await input.fill(`${original}-discarded`)
    await page.keyboard.press('Escape')
    await expect(warning).toBeVisible()
    await warning.getByRole('button', { name: '放弃修改' }).click()
    await expect(dialog).toBeHidden()

    await page.locator(`[data-test="${config.kind}-create"]`).click()
    await expect(dialog).toBeVisible()
    await expect(dialog.locator(config.initial)).toBeFocused()
    await page.keyboard.press('Escape')
    await expect(dialog).toBeHidden()
    await expect(warning).toBeHidden()

    if (config.kind === 'rule') await row.locator('[data-test="rule-copy"]').click()
    else {
      await row.locator(`[data-test="${config.kind}-more"]`).click()
      await page.locator(`[data-test="${config.kind}-copy"]:visible`).click()
    }
    await expect(dialog).toBeVisible()
    await expect(dialog.locator(config.initial)).toBeFocused()
    await page.keyboard.press('Escape')
    await expect(dialog).toBeHidden()
    await expect(warning).toBeHidden()
  })

  test(`${config.kind}: rapid shortcut saves send one write, pending writes lock the form, failures allow retry`, async ({ page, request }) => {
    const { dialog, input, entity, headers } = await openEditor(page, request, config)
    const value = `${await input.inputValue()}-saved`
    await input.fill(value)
    const path = `${config.api}/${entity.id}`
    let release!: () => void, fail = true, writes = 0
    const gate = new Promise<void>(resolve => { release = resolve })
    await page.route(url => url.pathname === path, async route => {
      if (route.request().method() !== 'PUT') return route.continue()
      writes++
      if (!fail) return route.continue()
      await gate
      await route.fulfill({ status: 500, json: { message: '保存暂时不可用' } })
    })
    const pending = page.waitForRequest(request => new URL(request.url()).pathname === path && request.method() === 'PUT')
    await input.evaluate(el => {
      for (let i = 0; i < 5; i++) el.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', ctrlKey: true, bubbles: true, cancelable: true }))
    })
    await pending
    try {
      await expect.poll(() => writes).toBe(1)
      await expect(input).toBeDisabled()
      await expect(dialog.getByRole('button', { name: '取消', exact: true })).toBeDisabled()
      await expect(dialog.locator(`[data-test="${config.kind}-save"]`)).toHaveClass(/is-loading/)
      await page.keyboard.press('Escape')
      await expect(dialog).toBeVisible()
      await expect(page.locator('.el-message-box:visible')).toHaveCount(0)
      release()
      const error = dialog.locator(`[data-test="${config.error}"]`)
      await expect(error).toContainText('保存暂时不可用')
      await expect(error).toBeFocused()
      await expect(input).toBeEnabled()
      await expect(input).toHaveValue(value)
      expect(writes).toBe(1)

      fail = false
      await error.press('Control+Enter')
      await expect(dialog).toBeHidden()
      const response = await request.get(config.api, { headers })
      expect(response.ok()).toBeTruthy()
      const saved = (await response.json()).find((item: { id: number }) => item.id === entity.id)
      expect(config.kind === 'rule' ? saved.message : saved.name).toBe(value)
      expect(writes).toBe(2)
    } finally { release() }
  })
}

test('leaving a dirty tag editor through page search keeps edits or discards them as chosen', async ({ page, request }) => {
  const config = cases[1]
  const { dialog, input } = await openEditor(page, request, config)
  await input.fill('尚未保存的测点')
  const navigate = async () => {
    await page.keyboard.press('Control+KeyK')
    const palette = page.locator('[data-test="command-palette"]')
    await expect(palette.locator('[data-test="command-palette-input"]')).toBeFocused()
    await palette.locator('[data-test="command-palette-input"]').fill('设备')
    await palette.locator('[data-test="command-palette-item-devices"] .cmd-open').click()
  }
  await navigate()
  const warning = page.locator('.el-message-box')
  await expect(warning).toBeVisible()
  await warning.getByRole('button', { name: '继续编辑' }).click()
  await expect(page).toHaveURL('/config/tags')
  await expect(input).toHaveValue('尚未保存的测点')
  await expect(dialog).toBeVisible()
  await navigate()
  await warning.getByRole('button', { name: '放弃修改' }).click()
  await expect(page).toHaveURL('/config/devices')
  await expect(page.locator('[data-test="tag-dialog"]')).toHaveCount(0)
})

test('refreshing a dirty editor asks the browser before losing edits', async ({ page, request }) => {
  const { dialog, input } = await openEditor(page, request, cases[1])
  await input.fill('刷新前保留的内容')
  const warning = new Promise<string>(resolve => {
    page.on('dialog', async browserDialog => {
      const type = browserDialog.type()
      await browserDialog.dismiss()
      resolve(type)
    })
  })
  await page.evaluate(() => { window.setTimeout(() => window.location.reload(), 0) })
  expect(await warning).toBe('beforeunload')
  await expect(dialog).toBeVisible()
  await expect(input).toHaveValue('刷新前保留的内容')
  await dialog.getByRole('button', { name: '取消', exact: true }).click()
  await page.locator('.el-message-box').getByRole('button', { name: '放弃修改' }).click()
})

test('invalid values focus the field and server validation errors remain visible', async ({ page, request }) => {
  const { dialog, input, entity } = await openEditor(page, request, cases[1])
  await input.fill('')
  await input.press('Control+Enter')
  await expect(dialog.locator('[data-test="tag-form-error"]')).toContainText('请先修正')
  await expect(input).toBeFocused()
  await expect(input).toBeEnabled()
  await input.fill(`${entity.name}-conflict`)
  await page.route(url => url.pathname === `/api/tags/${entity.id}`, route => route.fulfill({ status: 400, json: { message: '测点名称已存在' } }))
  await input.press('Control+Enter')
  await expect(dialog.locator('[data-test="tag-form-error"]')).toContainText('测点名称已存在')
  await expect(dialog.locator('.el-form-item.is-error')).toContainText('测点名称已存在')
  await expect(input).toBeFocused()
  await page.screenshot({ path: test.info().outputPath('tag-validation-focus.png'), animations: 'disabled' })
})
