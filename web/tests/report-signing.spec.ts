import { test, expect, type APIRequestContext } from './fixtures'

async function auth(request: APIRequestContext, userName = 'admin', password = 'admin123') {
  const response = await request.post('/api/auth/login', { data: { userName, password } }); expect(response.ok()).toBeTruthy()
  const result = await response.json(); return { ...result, headers: { Authorization: `Bearer ${result.token}` } }
}

test('report signing: archived report shows valid signature, copy check, review e-signature with password re-auth', async ({ page, request }) => {
  test.setTimeout(90000)
  const session = await auth(request), headers = session.headers, name = `签名日报-${Date.now()}`
  const areas = await (await request.get('/api/areas', { headers })).json()
  const created = await request.post('/api/report-templates', { headers, data: { name, period: 'Day', areaId: areas[0].id, scheduleEnabled: false, columns: [{ key: 'a', title: '报警', kind: 'AlarmCount', decimals: 0 }] } })
  expect(created.ok()).toBeTruthy()
  const run = await (await request.post(`/api/report-templates/${(await created.json()).id}/generate`, { headers, data: {} })).json()
  expect(run.signed).toBe(true)

  await page.addInitScript(({ token, user }) => { localStorage.setItem('scada_token', token); localStorage.setItem('scada_user', JSON.stringify(user)) }, session)
  await page.goto('/report-center')
  await expect(page.locator(`[data-test="report-signed-${run.id}"]`)).toBeVisible()
  await expect(page.locator(`[data-test="report-approval-${run.id}"]`)).toHaveText('待审核')
  await page.locator(`[data-test="report-verify-${run.id}"]`).click()
  await expect(page.locator('[data-test="report-verify-result"]')).toContainText('验证通过')

  const pdf = await request.get(`/api/report-runs/${run.id}/download?format=pdf`, { headers })
  expect(pdf.headers()['x-report-signature']).toBe('valid')
  await page.locator('[data-test="report-verify-file"]').setInputFiles({ name: 'copy.pdf', mimeType: 'application/pdf', buffer: await pdf.body() })
  await expect(page.locator('[data-test="report-verify-file-result"]')).toContainText('签名有效')

  await page.locator('[data-test="report-sign-reason"]').fill('数据核对无误')
  await page.locator('[data-test="report-sign-password"]').fill('wrong-password')
  await page.locator('[data-test="report-sign-submit"]').click()
  await expect(page.locator('[data-test="report-sign-form"] .el-alert')).toContainText('密码不正确')
  await page.locator('[data-test="report-sign-password"]').fill('admin123')
  await page.locator('[data-test="report-sign-submit"]').click()
  await expect(page.locator('[data-test="report-approval-table"]')).toContainText('数据核对无误')
  await page.screenshot({ path: test.info().outputPath('report-signing.png'), animations: 'disabled' })
  await expect(page.locator(`[data-test="report-approval-${run.id}"]`)).toHaveText('已审核，待批准')
  // 同一人不能批准。
  const self = await request.post(`/api/report-runs/${run.id}/approvals`, { headers, data: { step: 'approve', decision: 'approved', reason: 'x', password: 'admin123' } })
  expect(self.status()).toBe(409)
})
