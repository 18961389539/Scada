import { test, expect, type APIRequestContext, type Page } from './fixtures'

async function login(request: APIRequestContext) {
  const response = await request.post('/api/auth/login', { data: { userName: 'admin', password: 'admin123' } })
  expect(response.ok()).toBeTruthy()
  const body = await response.json()
  return { token: body.token as string, user: body.user, headers: { Authorization: `Bearer ${body.token}` } }
}

async function sign(page: Page, session: { token: string; user: unknown }): Promise<void> {
  await page.addInitScript((data) => {
    localStorage.setItem('scada_token', data.token)
    localStorage.setItem('scada_user', JSON.stringify(data.user))
  }, session)
}

test('library import, template save and delete failures stay on the page', async ({ page, request }) => {
  const session = await login(request)
  const suffix = `${Date.now()}`
  const symbol = await (await request.post('/api/hmi/library', { headers: session.headers, data: {
    key: `sym${suffix}`, name: `符号${suffix}`, category: '自定义', kind: 'symbol',
    svg: '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 40 40"><rect width="40" height="40"/></svg>',
    width: 40, height: 40, placeholders: []
  } })).json()
  const template = await (await request.post('/api/hmi/library', { headers: session.headers, data: {
    key: `tpl${suffix}`, name: `面板${suffix}`, category: '自定义', kind: 'template', width: 320, height: 200, placeholders: ['speed'],
    document: { schemaVersion: 1, width: 320, height: 200, background: '#101b2d', nodes: [] }
  } })).json()
  expect(symbol.item.id).toBeTruthy()
  expect(template.item.id).toBeTruthy()
  await sign(page, session)
  await page.route((url) => url.pathname === '/api/hmi/library/import', (route) =>
    route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message: '导入不可用' }) }))
  await page.route((url) => url.pathname === `/api/hmi/library/${template.item.id}`, (route) => {
    if (route.request().method() !== 'PUT') return route.continue()
    return route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message: '保存不可用' }) })
  })
  await page.route((url) => url.pathname === `/api/hmi/library/${symbol.item.id}`, (route) => {
    if (route.request().method() !== 'DELETE') return route.continue()
    return route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message: '删除不可用' }) })
  })
  await page.goto('/config/hmi-library')
  await page.locator('[data-test="library-svg-file"]').setInputFiles({
    name: `new${suffix}.svg`, mimeType: 'image/svg+xml',
    buffer: Buffer.from('<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 40 40"><rect width="40" height="40"/></svg>')
  })
  await expect(page.locator('[data-test="library-action-error"]')).toContainText('导入不可用')
  await page.getByRole('tab', { name: /用户图库/ }).click()
  const items = page.locator('[data-test="library-items"]')
  await expect(items).toContainText(`面板${suffix}`)
  await expect(items).not.toContainText(`new${suffix}`)
  await page.locator(`[data-test="library-edit-tpl${suffix}"]`).click()
  const dialog = page.getByRole('dialog', { name: /编辑模板/ })
  await dialog.getByRole('textbox', { name: '新占位符' }).fill('1bad')
  await dialog.getByRole('button', { name: '添加', exact: true }).click()
  await expect(dialog.locator('[data-test="library-template-error"]')).toContainText('占位符须为字母开头的唯一键')
  await dialog.locator('[data-test="library-template-save"]').click()
  await expect(dialog.locator('[data-test="library-template-error"]')).toContainText('保存不可用')
  await expect(dialog).toBeVisible()
  await dialog.getByRole('button', { name: '取消', exact: true }).click()
  await expect(dialog).not.toBeVisible()
  await page.locator(`[data-test="library-delete-sym${suffix}"]`).click()
  await page.locator('.el-message-box').getByRole('button', { name: '确定' }).click()
  await expect(page.locator('[data-test="library-action-error"]')).toContainText('删除不可用')
  await expect(items).toContainText(`符号${suffix}`)
})
