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

test('report generate, delete, download and file check failures stay on the page', async ({ page, request }) => {
  const session = await login(request)
  await sign(page, session)
  const template = {
    id: 9, name: '失败模板', period: 'Day', areaId: 1, shiftStarts: '08:00,20:00', columns: [],
    scheduleEnabled: false, scheduleDelayMin: 10, emailChannelId: null, emailRecipients: '', revision: 1, updatedTs: Date.now()
  }
  const run = {
    id: 7, templateId: 9, templateName: '失败模板', templateRevision: 1, areaId: 1, periodKey: '2026-10-26',
    fromTs: 0, toTs: 1, version: 1, trigger: 'manual', generatedBy: 'admin', generatedTs: Date.now(),
    xlsxSha256: 'a'.repeat(16), pdfSha256: 'b'.repeat(16), purgedTs: null, signed: false,
    signerThumbprint: null, signerSubject: null, signedTs: null, signatureError: null, approvalStatus: 'none'
  }
  await page.route((url) => url.pathname === '/api/report-templates', (route) => {
    if (route.request().method() !== 'GET') return route.continue()
    return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify([template]) })
  })
  await page.route((url) => url.pathname === '/api/report-templates/9/generate', (route) =>
    route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message: '生成不可用' }) }))
  await page.route((url) => url.pathname === '/api/report-templates/9', (route) => {
    if (route.request().method() !== 'DELETE') return route.continue()
    return route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message: '删除不可用' }) })
  })
  await page.route((url) => url.pathname === '/api/report-runs', (route) => {
    if (route.request().method() !== 'GET') return route.continue()
    return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify([run]) })
  })
  await page.route((url) => url.pathname === '/api/report-runs/7/download', (route) =>
    route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message: '下载不可用' }) }))
  await page.route((url) => url.pathname === '/api/report-runs/7/verify', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({
      runId: 7, purged: false, signed: false, signerThumbprint: null, signerSubject: null, signedTs: null, signatureError: null,
      files: [], approvals: [], status: 'none', ok: false, currentThumbprint: null
    }) }))
  await page.route((url) => url.pathname === '/api/report-runs/7/verify-file', (route) =>
    route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message: '核对不可用' }) }))
  await page.goto('/report-center')
  await page.locator('[data-test="report-generate-9"]').click()
  await page.locator('.el-message-box').getByRole('button', { name: '生成' }).click()
  const action = page.locator('[data-test="report-action-error"]')
  await expect(action).toContainText('生成不可用')
  await page.locator('[data-test="report-template-table"]').getByRole('button', { name: '删除' }).click()
  await page.locator('.el-message-box').getByRole('button', { name: '确定' }).click()
  await expect(action).toContainText('删除不可用')
  await expect(page.locator('[data-test="report-template-table"]')).toContainText('失败模板')
  await page.locator('[data-test="report-run-table"]').getByRole('button', { name: 'Excel' }).click()
  await expect(action).toContainText('下载不可用')
  await page.locator('[data-test="report-verify-7"]').click()
  const verify = page.locator('[data-test="report-verify-dialog"]')
  await verify.locator('[data-test="report-verify-file"]').setInputFiles({ name: 'copy.pdf', mimeType: 'application/pdf', buffer: Buffer.from('%PDF') })
  await expect(verify.locator('[data-test="report-verify-file-error"]')).toContainText('核对不可用')
  await expect(verify.locator('[data-test="report-verify-file-result"]')).toHaveCount(0)
})
