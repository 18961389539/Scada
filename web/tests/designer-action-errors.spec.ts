import { test, expect, type APIRequestContext, type Page } from '@playwright/test'

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

test('designer save, validation, publish and template failures stay on the page', async ({ page, request }) => {
  const session = await login(request)
  const created = await request.post('/api/screens', { headers: session.headers, data: { name: `设计器失败-${Date.now()}` } })
  expect(created.status()).toBe(201)
  const screen = await created.json()
  await sign(page, session)
  let saves = 0
  await page.route((url) => url.pathname === `/api/screens/${screen.id}`, (route) => {
    if (route.request().method() === 'PUT' && saves++ === 0) {
      return route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message: '保存不可用' }) })
    }
    return route.continue()
  })
  await page.route((url) => url.pathname === '/api/screens/validate', (route) =>
    route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message: '校验不可用' }) }))
  await page.route((url) => url.pathname === `/api/screens/${screen.id}/publish`, (route) =>
    route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message: '发布不可用' }) }))
  await page.route((url) => url.pathname === '/api/hmi/library' && url.search === '', (route) => {
    if (route.request().method() !== 'POST') return route.continue()
    return route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message: '模板不可用' }) })
  })
  await page.goto(`/config/screens/${screen.id}`)
  await page.getByRole('button', { name: '保存草稿', exact: true }).click()
  const action = page.locator('[data-test="designer-action-error"]')
  await expect(action).toContainText('保存不可用')
  await page.getByRole('button', { name: '校验', exact: true }).click()
  const issues = page.locator('[data-test="designer-issues"]')
  await expect(issues.locator('[data-test="designer-issues-error"]')).toContainText('校验不可用')
  await expect(issues.getByText('校验通过')).toHaveCount(0)
  await page.keyboard.press('Escape')
  await expect(issues).not.toBeVisible()
  await page.locator('[data-palette="pump"]').click()
  await page.getByRole('button', { name: '存为模板', exact: true }).click()
  const template = page.locator('[data-test="template-dialog"]')
  await template.getByRole('textbox', { name: '键' }).fill('pump-face')
  await template.getByRole('textbox', { name: '名称' }).fill('泵面板')
  await template.locator('[data-test="template-create"]').click()
  await expect(template.locator('[data-test="template-create-error"]')).toContainText('模板不可用')
  await expect(template).toBeVisible()
  await template.getByRole('button', { name: '取消', exact: true }).click()
  await expect(template).not.toBeVisible()
  await page.getByRole('button', { name: '保存草稿', exact: true }).click()
  await expect(page.getByText('草稿已保存', { exact: true })).toBeVisible()
  await expect(action).toHaveCount(0)
  await page.getByRole('button', { name: '发布', exact: true }).click()
  await expect(action).toContainText('发布不可用')
  await expect(page.locator('[data-test="designer-issues"]')).not.toBeVisible()
})
