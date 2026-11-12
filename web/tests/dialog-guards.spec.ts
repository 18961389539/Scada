import { test, expect, type APIRequestContext, type Page, loginApi } from './fixtures'

// 填了一半的新建表单，误点遮罩、按 Esc 或点取消就整段丢掉——建 Key 要勾权限选区域，
// 批量实例化要算地址区间，重来一次成本不低。这里固化：改过才问、没改不问、取消能留住输入、
// 真正放弃后重开是干净表单（以前不重置，上次没保存的名字会留在里面，看着像已经建好了）。

async function prepare(page: Page, request: APIRequestContext) {
  const auth = await loginApi(request)
  await page.addInitScript(({ token, user }) => {
    localStorage.setItem('scada_token', token)
    localStorage.setItem('scada_user', JSON.stringify(user))
  }, auth)
}

test('建 External API Key 填了内容再取消，先确认；放弃后重开是空表单', async ({ page, request }) => {
  await prepare(page, request)
  await page.route('**/api/**', async route => {
    const path = new URL(route.request().url()).pathname
    if (path === '/api/external/keys') return route.fulfill({ json: [] })
    if (path === '/api/external/status') return route.fulfill({ json: { rest: { enabled: true }, opcUa: { enabled: false } } })
    if (path === '/api/areas') return route.fulfill({ json: [{ id: 1, name: '一车间' }] })
    return route.fulfill({ json: [] })
  })
  await page.goto('/config/external-api')
  const open = page.locator('[data-test="ext-add"]')
  await open.click()
  const dialog = page.locator('.el-dialog')
  await expect(dialog).toBeVisible()

  // 一个字都没填就关，不该问
  await dialog.locator('.el-dialog__headerbtn').click()
  await expect(dialog).toBeHidden()
  await expect(page.locator('.el-message-box')).toHaveCount(0)

  // 填了名字再关，必须问
  await open.click()
  await page.locator('[data-test="ext-name"]').fill('MES 采集账号')
  await dialog.locator('.el-dialog__headerbtn').click()
  const box = page.locator('.el-message-box')
  await expect(box).toBeVisible()
  await expect(box).toContainText('尚有未保存的修改')

  // 选择继续编辑：弹窗留着，输入还在
  await box.locator('button', { hasText: '继续编辑' }).click()
  await expect(box).toBeHidden()
  await expect(dialog).toBeVisible()
  await expect(page.locator('[data-test="ext-name"]')).toHaveValue('MES 采集账号')

  // 真的放弃：关掉，重开必须是干净的
  await dialog.locator('.el-dialog__headerbtn').click()
  await page.locator('.el-message-box button', { hasText: '放弃修改' }).click()
  await expect(dialog).toBeHidden()
  await open.click()
  await expect(dialog).toBeVisible()
  await expect(page.locator('[data-test="ext-name"]')).toHaveValue('')
})

test('批量实例化改过参数后取消会先确认，继续编辑保住参数', async ({ page, request }) => {
  await prepare(page, request)
  await page.route('**/api/**', async route => {
    const path = new URL(route.request().url()).pathname
    if (path === '/api/object-templates') return route.fulfill({ json: [{ id: 1, name: '泵模板', latestVersion: 1, revision: 3, instanceCount: 0 }] })
    if (path === '/api/object-templates/1') return route.fulfill({ json: { id: 1, name: '泵模板', revision: 3, versions: [{ version: 1, publishedTs: Date.now(), note: '' }] } })
    if (path === '/api/object-templates/1/versions/1') return route.fulfill({ json: { version: 1, document: { tags: [], alarms: [], panelNodes: [] } } })
    if (path === '/api/devices') return route.fulfill({ json: [{ id: 11, name: '一号柜', areaId: 1, protocol: 'ModbusTcp', enabled: true }] })
    return route.fulfill({ json: [] })
  })
  await page.goto('/config/object-templates')
  await page.locator('[data-test="tpl-instantiate"]').first().click()
  const dialog = page.locator('.el-dialog').filter({ hasText: '泵模板' })
  await expect(dialog).toBeVisible()

  await page.locator('[data-test="tpl-batch-prefix"]').fill('P-101')
  await dialog.locator('.el-dialog__headerbtn').click()
  const box = page.locator('.el-message-box')
  await expect(box).toBeVisible()
  await box.locator('button', { hasText: '继续编辑' }).click()
  await expect(page.locator('[data-test="tpl-batch-prefix"]')).toHaveValue('P-101')

  // 没改就关：第二次打开后原样关掉，不该再问
  await dialog.locator('.el-dialog__headerbtn').click()
  await page.locator('.el-message-box button', { hasText: '放弃修改' }).click()
  await expect(dialog).toBeHidden()
  await page.locator('[data-test="tpl-instantiate"]').first().click()
  await expect(dialog).toBeVisible()
  await dialog.locator('.el-dialog__headerbtn').click()
  await expect(dialog).toBeHidden()
  await expect(page.locator('.el-message-box')).toHaveCount(0)
})
