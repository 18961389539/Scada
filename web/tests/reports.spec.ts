import { test, expect, type APIRequestContext } from '@playwright/test'

async function auth(request: APIRequestContext, userName = 'admin', password = 'admin123') {
  const response = await request.post('/api/auth/login', { data: { userName, password } }); expect(response.ok()).toBeTruthy()
  const result = await response.json(); return { ...result, headers: { Authorization: `Bearer ${result.token}` } }
}

test('report center: UI builds a day template, archives versioned Excel/PDF, preview and downloads work, runs cannot be modified', async ({ page, request }) => {
  test.setTimeout(90000)
  const session = await auth(request), headers = session.headers, name = `日报-${Date.now()}`
  await page.addInitScript(({ token, user }) => { localStorage.setItem('scada_token', token); localStorage.setItem('scada_user', JSON.stringify(user)) }, session)
  await page.goto('/report-center')
  await page.locator('[data-test="report-center-templates"]').click()
  await page.locator('[data-test="report-template-new"]').click()
  await page.locator('[data-test="report-template-name"]').fill(name)
  await page.locator('[data-test="report-add-column"]').click()
  await page.locator('[data-test="report-add-column"]').click()
  // 第一列为报警次数，第二列改为公式引用第一列。
  await page.locator('[data-test="report-column-0"] .el-select').first().click()
  await page.getByRole('option', { name: '报警次数', exact: true }).click()
  const second = page.locator('[data-test="report-column-1"]')
  await second.locator('.el-select').first().click()
  await page.getByRole('option', { name: '公式', exact: true }).last().click()
  await second.getByPlaceholder('如 $a - $b').fill('$c1 * 2')
  await page.locator('[data-test="report-template-save"]').click()
  const row = page.locator('[data-test="report-template-table"] .el-table__row', { hasText: name })
  await expect(row).toContainText('日报')

  const template = (await (await request.get('/api/report-templates', { headers })).json()).find((x: any) => x.name === name)
  for (let i = 0; i < 2; i++) {
    await page.locator('[data-test="report-center-templates"]').click()
    await page.locator(`[data-test="report-generate-${template.id}"]`).click()
    await page.locator('.el-message-box').getByRole('button', { name: '生成' }).click()
    await expect(page.locator('.el-message-box')).toBeHidden()
    await expect(page.locator('[data-test="report-center-archives"] input')).toBeChecked()
  }
  await expect.poll(async () => (await (await request.get(`/api/report-runs?templateId=${template.id}`, { headers })).json()).length, { timeout: 20000 }).toBe(2)
  const runs = await (await request.get(`/api/report-runs?templateId=${template.id}`, { headers })).json()
  expect(runs.map((x: any) => x.version).sort()).toEqual([1, 2])
  await page.reload()
  await page.locator(`[data-test="report-view-${runs[0].id}"]`).click()
  await expect(page.locator('[data-test="report-preview"]')).toContainText('合计')
  await page.screenshot({ path: test.info().outputPath('report-center.png'), animations: 'disabled' })

  const pdf = await request.get(`/api/report-runs/${runs[0].id}/download?format=pdf`, { headers })
  expect(pdf.status()).toBe(200); expect((await pdf.body()).subarray(0, 4).toString()).toBe('%PDF')
  expect((await request.get(`/api/report-runs/${runs[0].id}/download?format=xlsx`, { headers })).status()).toBe(200)
  expect([404, 405]).toContain((await request.delete(`/api/report-runs/${runs[0].id}`, { headers })).status())
  expect((await request.delete(`/api/report-templates/${template.id}?revision=${template.revision}`, { headers })).status()).toBe(204)
  expect((await (await request.get(`/api/report-runs?templateId=${template.id}`, { headers })).json()).length).toBe(2)
})
