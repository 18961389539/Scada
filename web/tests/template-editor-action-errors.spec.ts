import { test, expect, type APIRequestContext, type Page } from '@playwright/test'
import type { ObjectTemplateDocument } from '../src/api/objectTemplates'

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

test('template editor save, validation, publish and version failures stay on the page', async ({ page, request }) => {
  const session = await login(request)
  const document = await (await request.get('/api/object-templates/examples/pump', { headers: session.headers })).json() as ObjectTemplateDocument
  const created = await request.post('/api/object-templates', { headers: session.headers, data: { name: `编辑器失败-${Date.now()}`, revision: 0, document } })
  expect(created.status()).toBe(201)
  const template = await created.json()
  expect((await request.post(`/api/object-templates/${template.id}/publish`, { headers: session.headers, data: { revision: template.revision } })).ok()).toBeTruthy()
  await sign(page, session)
  let saves = 0
  let versions = 0
  await page.route((url) => url.pathname === `/api/object-templates/${template.id}`, (route) => {
    if (route.request().method() === 'PUT' && saves++ === 0) {
      return route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message: '保存不可用' }) })
    }
    return route.continue()
  })
  await page.route((url) => url.pathname === '/api/object-templates/validate', (route) =>
    route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message: '校验不可用' }) }))
  await page.route((url) => url.pathname === `/api/object-templates/${template.id}/publish`, (route) =>
    route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message: '发布不可用' }) }))
  await page.route((url) => url.pathname === `/api/object-templates/${template.id}/versions/1`, (route) => {
    if (versions++ === 0) {
      return route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message: '版本不可用' }) })
    }
    return route.continue()
  })
  await page.goto(`/config/object-templates/${template.id}`)
  await page.getByRole('button', { name: '保存模板', exact: true }).click()
  const action = page.locator('[data-test="template-editor-error"]')
  await expect(action).toContainText('保存不可用')
  await page.getByRole('button', { name: '校验模板', exact: true }).click()
  const issues = page.locator('[data-test="template-editor-issues"]')
  await expect(issues.locator('[data-test="template-editor-issues-error"]')).toContainText('校验不可用')
  await expect(issues.getByText('校验通过')).toHaveCount(0)
  await page.keyboard.press('Escape')
  await expect(issues).not.toBeVisible()
  await page.getByRole('tab', { name: '发布历史', exact: true }).click()
  await page.getByRole('button', { name: '查看版本', exact: true }).click()
  const history = page.locator('[data-test="template-version-dialog"]')
  await expect(history.locator('[data-test="template-version-error"]')).toContainText('版本不可用')
  await expect(history.locator('[data-test="template-version-tags"]')).toHaveCount(0)
  await page.keyboard.press('Escape')
  await expect(history).not.toBeVisible()
  await page.getByRole('button', { name: '查看版本', exact: true }).click()
  await expect(history.locator('[data-test="template-version-tags"]')).toBeVisible()
  await expect(history.locator('[data-test="template-version-error"]')).toHaveCount(0)
  await page.keyboard.press('Escape')
  await page.getByRole('button', { name: '保存模板', exact: true }).click()
  await expect(page.getByText('模板草稿已保存', { exact: true })).toBeVisible()
  await expect(action).toHaveCount(0)
  await page.getByRole('button', { name: '发布新版本', exact: true }).click()
  await expect(action).toContainText('发布不可用')
  await expect(page.locator('[data-test="template-editor-issues"]')).not.toBeVisible()
})
