import { test, expect, type APIRequestContext } from '@playwright/test'

async function auth(request: APIRequestContext, userName = 'admin', password = 'admin123') {
  const response = await request.post('/api/auth/login', { data: { userName, password } }); expect(response.ok()).toBeTruthy()
  const result = await response.json(); return { ...result, headers: { Authorization: `Bearer ${result.token}` } }
}

test('calc jobs: UI creates a sandboxed script, outputs become derived tags, bad scripts are rejected and runs/snapshots are visible', async ({ page, request }) => {
  test.setTimeout(60000)
  const session = await auth(request), headers = session.headers, name = `脚本任务-${Date.now()}`
  await page.addInitScript(({ token, user }) => { localStorage.setItem('scada_token', token); localStorage.setItem('scada_user', JSON.stringify(user)) }, session)
  await page.goto('/calc-jobs')
  await page.locator('[data-test="calc-job-new"]').click()
  await page.locator('[data-test="calc-job-name"]').fill(name)
  await page.locator('[data-test="calc-job-kind"]').click()
  await page.locator('.el-select-dropdown__item:visible', { hasText: '受限脚本' }).click()
  // 先提交一个越过白名单的脚本：服务端拒绝并在对话框中提示。
  await page.locator('[data-test="calc-job-script"]').fill('@x = #999999')
  await page.locator('[data-test="calc-job-save"]').click()
  await expect(page.locator('[data-test="calc-job-error"]')).toContainText('白名单')
  await page.locator('[data-test="calc-job-script"]').fill('$a = 2 * 21\n@answer = $a; @hourNow = $hour')
  await page.locator('[data-test="calc-job-save"]').click()
  const row = page.locator('[data-test="calc-job-table"] .el-table__row', { hasText: name })
  await expect(row).toContainText('受限脚本')
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
