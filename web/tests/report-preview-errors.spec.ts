import { test, expect, type APIRequestContext, type Page } from '@playwright/test'

async function login(request: APIRequestContext) {
  const response = await request.post('/api/auth/login', { data: { userName: 'admin', password: 'admin123' } })
  expect(response.ok()).toBeTruthy()
  const body = await response.json()
  return { token: body.token as string, user: body.user }
}

async function sign(page: Page, session: { token: string; user: unknown }): Promise<void> {
  await page.addInitScript((data) => {
    localStorage.setItem('scada_token', data.token)
    localStorage.setItem('scada_user', JSON.stringify(data.user))
  }, session)
}

test('report preview and verification failures stay in their dialogs', async ({ page, request }) => {
  const session = await login(request)
  await sign(page, session)
  const run = {
    id: 7, templateId: 1, templateName: '日报', templateRevision: 1, areaId: 1, periodKey: '2026-10-26',
    fromTs: 0, toTs: 1, version: 2, trigger: 'manual', generatedBy: 'admin', generatedTs: Date.now(),
    xlsxSha256: 'a'.repeat(16), pdfSha256: 'b'.repeat(16), purgedTs: null, signed: false,
    signerThumbprint: null, signerSubject: null, signedTs: null, signatureError: null, approvalStatus: 'none'
  }
  await page.route((url) => url.pathname === '/api/report-runs', (route) => {
    if (route.request().method() !== 'GET') return route.continue()
    return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify([run]) })
  })
  await page.route((url) => url.pathname === '/api/report-runs/7', (route) =>
    route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message: '报表不可用' }) }))
  await page.route((url) => url.pathname === '/api/report-runs/7/verify', (route) =>
    route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message: '验证不可用' }) }))
  await page.goto('/report-center')
  await page.locator('[data-test="report-view-7"]').click()
  const preview = page.getByRole('dialog', { name: '报表' })
  await expect(preview.locator('[data-test="report-preview-error"]')).toContainText('报表不可用')
  await expect(preview.locator('[data-test="report-preview"]')).toHaveCount(0)
  await page.keyboard.press('Escape')
  await expect(preview).not.toBeVisible()
  await page.locator('[data-test="report-verify-7"]').click()
  const verify = page.locator('[data-test="report-verify-dialog"]')
  await expect(verify.locator('[data-test="report-verify-error"]')).toContainText('验证不可用')
  await expect(verify.locator('[data-test="report-verify-result"]')).toHaveCount(0)
})
