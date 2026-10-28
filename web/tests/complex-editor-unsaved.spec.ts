import { test, expect, type APIRequestContext, type Page } from '@playwright/test'

const sequence = { id: 801, name: '安全停机', areaId: 1, description: null, dualApproval: false, revision: 1, updatedTs: 1, updatedBy: 'admin', steps: [{ id: 's1', type: 'end', name: '结束', tagId: null, value: 0, confirmExpression: null, timeoutMs: 10000, delayMs: 0 }] }
const point = { tagId: 821, requireConfirm: true, dualApproval: false, approvalTimeoutSec: 300, permitExpression: null, permitMessage: null, limitMin: null, limitMax: null, maxStep: null, feedbackExpression: null, feedbackTimeoutMs: 10000 }
const template = { id: 811, name: '泵站模板', areaId: 1, description: null, dualApproval: false, revision: 1, updatedTs: 1, updatedBy: 'admin', parameters: [{ key: 'pressure', name: '出口压力', tagId: 821, unit: 'MPa', min: 0, max: 50, defaultValue: 10 }] }
const recipe = { id: 812, templateId: 811, templateName: '泵站模板', areaId: 1, name: '夏季配方', description: null, version: 1, updatedTs: 1, updatedBy: 'admin', values: [{ key: 'pressure', value: 12 }], note: null, source: 'manual' }
const cases = [
  { kind: 'point', path: '/control', tab: '控制策略', table: 'points', dialog: 'point-dialog', field: 'point-permit', value: '#821 > 0', original: '', save: 'point-save', write: '/api/control/points/821', close: 'cancel' },
  { kind: 'sequence', path: '/control', tab: '顺控', table: 'sequences', dialog: 'sequence-dialog', field: 'sequence-name', value: '修改后的顺控', original: '安全停机', save: 'sequence-save', write: '/api/control/sequences/801', close: 'button' },
  { kind: 'template', path: '/recipes', tab: '模板', table: 'recipe-templates', dialog: 'recipe-template-dialog', field: 'template-name', value: '修改后的模板', original: '泵站模板', save: 'template-save', write: '/api/recipes/templates/811', close: 'escape' },
  { kind: 'recipe', path: '/recipes', tab: '配方', table: 'recipes', dialog: 'recipe-edit-dialog', field: 'recipe-name', value: '修改后的配方', original: '夏季配方', save: 'recipe-save', write: '/api/recipes/812', close: 'overlay' }
] as const
type EditorCase = typeof cases[number]

async function prepare(page: Page, request: APIRequestContext) {
  const response = await request.post('/api/auth/login', { data: { userName: 'admin', password: 'admin123' } })
  expect(response.ok()).toBeTruthy()
  const session = await response.json()
  await page.addInitScript(({ token, user }) => {
    localStorage.setItem('scada_token', token); localStorage.setItem('scada_user', JSON.stringify(user)); localStorage.setItem('scada_sound', 'off')
  }, session)
  const data: Record<string, unknown> = {
    '/api/areas': [{ id: 1, name: '一车间' }],
    '/api/devices': [{ id: 831, name: '泵站', areaId: 1 }],
    '/api/tags': [{ id: 821, name: '出口压力', deviceId: 831, writable: true }],
    '/api/control/points': [point], '/api/control/sequences': [sequence], '/api/control/requests': [], '/api/control/runs': [], '/api/control/lockouts': [], '/api/control/modes': [],
    '/api/recipes/templates': [template], '/api/recipes': [recipe], '/api/recipes/downloads': []
  }
  await page.route(url => Object.hasOwn(data, url.pathname), route => route.fulfill({ json: data[new URL(route.request().url()).pathname] }))
  await page.route('**/api/control/sequences/validate', route => route.fulfill({ json: { ok: true, message: null, stepIds: [] } }))
}

async function open(page: Page, item: EditorCase) {
  await page.goto(item.path)
  await page.getByRole('tab', { name: item.tab, exact: true }).click()
  const row = page.locator(`[data-test="${item.table}"] .el-table__row`).first()
  await row.getByRole('button', { name: '编辑', exact: true }).click()
  const dialog = page.locator(`[data-test="${item.dialog}"]`)
  await expect(dialog).toBeVisible()
  return { dialog, field: dialog.locator(`[data-test="${item.field}"]`) }
}

for (const item of cases) {
  test(`${item.kind}: unchanged drafts close directly; cancel, close, Esc and overlay protect edits`, async ({ page, request }) => {
    await prepare(page, request)
    const { dialog, field } = await open(page, item)
    await dialog.getByRole('button', { name: '取消', exact: true }).click()
    await expect(dialog).toBeHidden()
    await expect(page.locator('.el-message-box:visible')).toHaveCount(0)
    await page.locator(`[data-test="${item.table}"] .el-table__row`).first().getByRole('button', { name: '编辑', exact: true }).click()
    await field.fill(item.value)
    if (item.close === 'cancel') await dialog.getByRole('button', { name: '取消', exact: true }).click()
    else if (item.close === 'button') await dialog.locator('.el-dialog__headerbtn').click()
    else if (item.close === 'escape') await page.keyboard.press('Escape')
    else await page.locator('.el-overlay-dialog').filter({ has: dialog }).click({ position: { x: 4, y: 4 } })
    const warning = page.locator('.el-message-box')
    await expect(warning).toContainText('尚有未保存的修改')
    await warning.getByRole('button', { name: '继续编辑' }).click()
    await expect(dialog).toBeVisible()
    await expect(field).toHaveValue(item.value)
    await field.fill(item.original)
    await dialog.getByRole('button', { name: '取消', exact: true }).click()
    await expect(dialog).toBeHidden()
    await expect(warning).toBeHidden()
    await page.locator(`[data-test="${item.table}"] .el-table__row`).first().getByRole('button', { name: '编辑', exact: true }).click()
    await field.fill(item.value)
    await dialog.getByRole('button', { name: '取消', exact: true }).click()
    await warning.getByRole('button', { name: '放弃修改' }).click()
    await expect(dialog).toBeHidden()
    await page.locator(`[data-test="${item.table}"] .el-table__row`).first().getByRole('button', { name: '编辑', exact: true }).click()
    await expect(field).toHaveValue(item.original)
    if (item.kind !== 'point') {
      const description = dialog.getByRole('textbox', { name: '说明', exact: true })
      await description.fill('临时说明')
      await description.fill('')
      if (item.kind === 'recipe') {
        await dialog.locator('[data-test="recipe-note"]').fill('临时版本说明')
        await dialog.locator('[data-test="recipe-note"]').fill('')
      }
      await dialog.getByRole('button', { name: '取消', exact: true }).click()
      await expect(dialog).toBeHidden()
      await expect(warning).toBeHidden()
    }
  })
}

test('empty optional text is unchanged while an unset numeric limit changed to zero remains dirty', async ({ page, request }) => {
  await prepare(page, request)
  const { dialog } = await open(page, cases[0])
  await dialog.locator('[data-test="point-max"] input').fill('0')
  await dialog.getByRole('button', { name: '取消', exact: true }).click()
  const warning = page.locator('.el-message-box')
  await expect(warning).toContainText('尚有未保存的修改')
  await warning.getByRole('button', { name: '继续编辑' }).click()
  await expect(dialog.locator('[data-test="point-max"] input')).toHaveValue('0')
})

test('nested sequence steps and recipe parameters count as unsaved changes', async ({ page, request }) => {
  await prepare(page, request)
  const { dialog } = await open(page, cases[1])
  await dialog.locator('[data-test="sequence-editor-mode"]').getByText('表格', { exact: true }).click()
  await dialog.locator('[data-test="sequence-steps"] .el-table__row').first().getByRole('textbox').nth(1).fill('未保存的步骤名称')
  await dialog.getByRole('button', { name: '取消', exact: true }).click()
  await page.locator('.el-message-box').getByRole('button', { name: '放弃修改' }).click()
  await page.locator('[data-test="sequences"] .el-table__row').first().getByRole('button', { name: '编辑', exact: true }).click()
  await expect(dialog.locator('[data-test="sequence-steps"] .el-table__row').first().getByRole('textbox').nth(1)).toHaveValue('结束')
  await dialog.getByRole('button', { name: '取消', exact: true }).click()
  const editor = await open(page, cases[3])
  await editor.dialog.locator('[data-test="recipe-value-pressure"] input').fill('18')
  await editor.dialog.getByRole('button', { name: '取消', exact: true }).click()
  await page.locator('.el-message-box').getByRole('button', { name: '继续编辑' }).click()
  await expect(editor.dialog.locator('[data-test="recipe-value-pressure"] input')).toHaveValue('18')
})

test('leaving a dirty recipe through page search keeps or discards the draft', async ({ page, request }) => {
  await prepare(page, request)
  const { dialog, field } = await open(page, cases[3])
  await field.fill('尚未保存的配方')
  const navigate = async () => {
    await page.keyboard.press('Control+KeyK')
    const palette = page.locator('[data-test="command-palette"]')
    await palette.locator('[data-test="command-palette-input"]').fill('设备')
    await palette.locator('[data-test="command-palette-item-devices"] .cmd-open').click()
  }
  await navigate()
  await page.locator('.el-message-box').getByRole('button', { name: '继续编辑' }).click()
  await expect(page).toHaveURL('/recipes')
  await expect(field).toHaveValue('尚未保存的配方')
  await navigate()
  await page.locator('.el-message-box').getByRole('button', { name: '放弃修改' }).click()
  await expect(page).toHaveURL('/config/devices')
  await expect(dialog).toHaveCount(0)
})

test('refreshing a dirty template asks before losing edits', async ({ page, request }) => {
  await prepare(page, request)
  const { dialog, field } = await open(page, cases[2])
  await field.fill('刷新前保留的模板')
  const warning = new Promise<string>(resolve => {
    page.once('dialog', async browserDialog => {
      const type = browserDialog.type()
      await browserDialog.dismiss()
      resolve(type)
    })
  })
  await page.evaluate(() => { window.setTimeout(() => window.location.reload(), 0) })
  expect(await warning).toBe('beforeunload')
  await expect(dialog).toBeVisible()
  await expect(field).toHaveValue('刷新前保留的模板')
})

for (const item of cases.slice(0, 2)) {
  test(`${item.kind}: pending saves lock drafts and reject repeated submits, failure retains draft for retry`, async ({ page, request }) => {
    await prepare(page, request)
    const { dialog, field } = await open(page, item)
    await field.fill(item.value)
    let writes = 0, release!: () => void
    const pending = new Promise<void>(resolve => { release = resolve })
    await page.route(url => url.pathname === item.write, async route => {
      writes++
      if (writes === 1) { await pending; return route.fulfill({ status: 500, json: { message: '保存暂时不可用' } }) }
      return route.fulfill({ json: {} })
    })
    const save = dialog.locator(`[data-test="${item.save}"]`)
    await save.click()
    try {
      await expect.poll(() => writes).toBe(1)
      await expect(field).toBeDisabled()
      await expect(save).toHaveClass(/is-loading/)
      await expect(dialog.getByRole('button', { name: '取消', exact: true })).toBeDisabled()
      await save.dispatchEvent('click')
      await page.keyboard.press('Escape')
      expect(writes).toBe(1)
      await expect(dialog).toBeVisible()
      release()
      await expect(dialog.getByRole('alert')).toContainText('保存暂时不可用')
      await expect(field).toBeEnabled()
      await expect(field).toHaveValue(item.value)
      await save.click()
      await expect(dialog).toBeHidden()
      await expect(page.locator('.el-message-box:visible')).toHaveCount(0)
      expect(writes).toBe(2)
    } finally { release() }
  })
}
