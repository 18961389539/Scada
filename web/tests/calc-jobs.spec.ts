import { test, expect, type APIRequestContext, type Page } from '@playwright/test'

async function auth(request: APIRequestContext, userName = 'admin', password = 'admin123') {
  const response = await request.post('/api/auth/login', { data: { userName, password } }); expect(response.ok()).toBeTruthy()
  const result = await response.json(); return { ...result, headers: { Authorization: `Bearer ${result.token}` } }
}

async function source(page: Page, text: string) {
  await page.locator('[data-test="calc-job-csharp-editor"] .monaco-editor').click({ position: { x: 100, y: 30 } })
  await page.keyboard.press('Control+A')
  await page.keyboard.insertText(text)
}

test('calc jobs: C# compiles and previews before save, outputs become derived tags and runs/snapshots are visible', async ({ page, request }) => {
  test.setTimeout(60000)
  const session = await auth(request), headers = session.headers, name = `脚本任务-${Date.now()}`
  await page.addInitScript(({ token, user }) => { localStorage.setItem('scada_token', token); localStorage.setItem('scada_user', JSON.stringify(user)) }, session)
  await page.goto('/calc-jobs')
  await page.locator('[data-test="calc-job-new"]').click()
  await page.locator('[data-test="calc-job-name"]').fill(name)
  await page.locator('[data-test="calc-job-kind"]').click()
  await expect(page.locator('.el-select-dropdown__item:visible', { hasText: '受限脚本' })).toHaveCount(0)
  await page.locator('.el-select-dropdown__item:visible', { hasText: 'C# 高级脚本' }).click()
  await source(page, 'context.Output("broken", );')
  await page.locator('[data-test="csharp-compile"]').click()
  await expect(page.locator('[data-test="csharp-diagnostics"]')).toContainText('CS', { timeout: 20000 })
  await source(page, 'var answer = 2 * 21;\ncontext.Output("answer", answer);\ncontext.Output("hourNow", context.Now.Hour);\ncontext.Log("preview-ready");')
  await expect(page.locator('[data-test="csharp-diagnostics"]')).toHaveCount(0)
  await page.locator('[data-test="csharp-compile"]').click()
  await expect(page.locator('[data-test="csharp-result"]')).toContainText('检查通过', { timeout: 20000 })
  await page.locator('[data-test="csharp-preview"]').click()
  await expect(page.locator('[data-test="csharp-result"]')).toContainText('preview-ready', { timeout: 20000 })
  await expect(page.locator('[data-test="csharp-result"]')).toContainText('42')
  expect((await (await request.get('/api/calc-jobs', { headers })).json()).some((item: any) => item.job.name === name)).toBe(false)
  await page.screenshot({ path: test.info().outputPath('csharp-editor-desktop.png'), animations: 'disabled' })
  await page.locator('[data-test="calc-job-save"]').click()
  const row = page.locator('[data-test="calc-job-table"] .el-table__row', { hasText: name })
  await expect(row).toContainText('C# 高级脚本')
  await expect(row).toContainText('42', { timeout: 15000 })
  await page.screenshot({ path: test.info().outputPath('calc-jobs.png'), animations: 'disabled' })

  const jobs = await (await request.get('/api/calc-jobs', { headers })).json()
  const info = jobs.find((x: any) => x.job.name === name); expect(info.outputs.map((x: any) => x.name).sort()).toEqual(['answer', 'hourNow'])
  const answer = info.outputs.find((x: any) => x.name === 'answer').tagId
  await expect.poll(async () => (await (await request.get(`/api/realtime?tagIds=${answer}`, { headers })).json())[0]?.value, { timeout: 10000 }).toBe(42)
  const tag = (await (await request.get('/api/tags', { headers })).json()).find((x: any) => x.id === answer)
  expect(tag.kind).toBe('Derived'); expect(tag.name).toBe(`${name}.answer`)
  expect((await request.delete(`/api/tags/${answer}`, { headers })).status()).toBe(400)
  // 派生测点只读：不能下发控制。
  const command = await (await request.post('/api/commands', { headers, data: { tagId: answer, value: 1 } })).json(); expect(command.success).toBe(false)

  // 累计器：手动复位留快照，UI 可查看。
  const created = await request.post('/api/calc-jobs', { headers, data: { name: `${name}-累计`, kind: 'Accumulator', areaId: info.job.areaId, inputTagId: answer, mode: 'Flow', factor: 1, resetPeriod: 'Day' } })
  expect(created.status(), await created.text()).toBe(201); const accumulator = await created.json()
  expect((await request.post(`/api/calc-jobs/${accumulator.id}/reset`, { headers })).status()).toBe(204)
  await page.reload()
  const accRow = page.locator('[data-test="calc-job-table"] .el-table__row', { hasText: `${name}-累计` })
  await accRow.getByRole('button', { name: '快照' }).click()
  await expect(page.locator('.el-dialog:visible')).toContainText('手动复位')
  expect((await request.delete(`/api/calc-jobs/${accumulator.id}?revision=1`, { headers })).status()).toBe(204)
  expect((await request.delete(`/api/calc-jobs/${info.job.id}?revision=1`, { headers })).status()).toBe(204)
})

test('calc jobs: legacy scripts remain editable without becoming C#', async ({ page, request }) => {
  const session = await auth(request), name = `Legacy-${Date.now()}`
  const response = await request.post('/api/calc-jobs', { headers: session.headers, data: { name, kind: 'Script', areaId: 1, script: '@answer = 42' } })
  expect(response.status(), await response.text()).toBe(201)
  const job = await response.json()
  await page.addInitScript(({ token, user }) => { localStorage.setItem('scada_token', token); localStorage.setItem('scada_user', JSON.stringify(user)) }, session)
  await page.goto('/calc-jobs')
  await page.locator('.el-table__row', { hasText: name }).getByRole('button', { name: '编辑', exact: true }).click()
  await expect(page.locator('[data-test="calc-job-script"]')).toHaveValue('@answer = 42')
  await expect(page.locator('[data-test="calc-job-csharp-editor"]')).toHaveCount(0)
  await page.getByRole('button', { name: '取消', exact: true }).click()
  expect((await request.delete(`/api/calc-jobs/${job.id}?revision=1`, { headers: session.headers })).status()).toBe(204)
})

test('calc jobs: Worker timeout recovers and editor controls fit a narrow viewport', async ({ page, request }) => {
  test.setTimeout(60000)
  const session = await auth(request)
  await page.addInitScript(({ token, user }) => { localStorage.setItem('scada_token', token); localStorage.setItem('scada_user', JSON.stringify(user)) }, session)
  await page.goto('/calc-jobs')
  await page.locator('[data-test="calc-job-new"]').click()
  await page.locator('[data-test="calc-job-kind"]').click()
  await page.locator('.el-select-dropdown__item:visible', { hasText: 'C# 高级脚本' }).click()
  await source(page, 'while (true) { }')
  await page.locator('[data-test="csharp-preview"]').click()
  await expect(page.locator('[data-test="csharp-diagnostics"]')).toContainText('deadline', { timeout: 20000 })
  await source(page, 'context.Output("answer", 42);')
  await page.locator('[data-test="csharp-preview"]').click()
  await expect(page.locator('[data-test="csharp-result"]')).toContainText('42', { timeout: 20000 })
  await page.setViewportSize({ width: 390, height: 844 })
  const nameBox = await page.locator('[data-test="calc-job-name"]').boundingBox()
  const kindBox = await page.locator('[data-test="calc-job-kind"]').boundingBox()
  expect(nameBox!.width).toBeGreaterThan(200)
  expect(kindBox!.y).toBeGreaterThanOrEqual(nameBox!.y + nameBox!.height)
  await page.locator('[data-test="calc-job-name"]').scrollIntoViewIfNeeded()
  await page.screenshot({ path: test.info().outputPath('csharp-form-mobile.png'), animations: 'disabled', fullPage: true })
  await page.locator('[data-test="csharp-preview"]').scrollIntoViewIfNeeded()
  for (const selector of ['[data-test="calc-job-csharp-editor"]', '[data-test="csharp-compile"]', '[data-test="csharp-preview"]', '[data-test="calc-job-save"]']) {
    const box = await page.locator(selector).boundingBox()
    expect(box).not.toBeNull(); expect(box!.x).toBeGreaterThanOrEqual(0); expect(box!.x + box!.width).toBeLessThanOrEqual(390)
  }
  await page.screenshot({ path: test.info().outputPath('csharp-editor-mobile.png'), animations: 'disabled', fullPage: true })
  await page.getByRole('button', { name: '取消', exact: true }).click()
  await expect(page.locator('.el-message-box')).toContainText('未保存')
  await page.locator('.el-message-box').getByRole('button', { name: /确定|确认/ }).click()
})
