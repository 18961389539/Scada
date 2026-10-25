import { test, expect, type APIRequestContext, type Page } from '@playwright/test'

async function auth(request: APIRequestContext) {
  const response = await request.post('/api/auth/login', { data: { userName: 'admin', password: 'admin123' } }); expect(response.ok()).toBeTruthy()
  const result = await response.json(); return { ...result, headers: { Authorization: `Bearer ${result.token}` } }
}
async function signIn(page: Page, session: { token: string; user: unknown }) {
  await page.goto('/login'); await page.evaluate(s => { localStorage.setItem('scada_token', s.token); localStorage.setItem('scada_user', JSON.stringify(s.user)) }, session)
}
async function dragTo(page: Page, from: string, to: string) {
  const a = (await page.locator(from).boundingBox())!, b = (await page.locator(to).boundingBox())!
  await page.mouse.move(a.x + a.width / 2, a.y + a.height / 2); await page.mouse.down()
  await page.mouse.move(b.x + b.width / 2, b.y + b.height / 2, { steps: 8 }); await page.mouse.up()
}

test('graphical sequence editor: add nodes, connect, validation on nodes, lossless save, live run chart', async ({ page, request }) => {
  const admin = await auth(request), u = `${Date.now()}`.slice(-6)
  await signIn(page, admin); await page.goto('/control')
  await page.getByRole('tab', { name: '顺控' }).click()
  await page.locator('[data-test="sequence-new"]').click()
  const dialog = page
  await dialog.locator('[data-test="sequence-name"]').fill(`图形-${u}`)
  const add = async (type: string) => { await dialog.locator('[data-test="flow-add"]').click(); await page.locator(`[data-test="flow-add-${type}"]:visible`).click() }
  await add('wait'); await dialog.locator('[data-test="prop-expression"]').fill('1 == 1'); await dialog.locator('[data-test="prop-expression"]').blur()
  await add('end'); await add('end')
  // s3 不可达：节点标红
  await expect(dialog.locator('[data-test="flow-node-s3"]')).toHaveAttribute('data-error', 'true')
  await expect(dialog.locator('[data-test="sequence-validation"]')).toContainText('不可达')
  await dialog.locator('[data-test="flow-node-s3"]').click(); await dialog.locator('[data-test="prop-delete"]').click()
  await expect(dialog.locator('[data-test="flow-node-s3"]')).toHaveCount(0)
  // 拖线：s1 的“下一步”端口连到 s2（显式 next）
  await dragTo(page, '[data-test="flow-port-s1-next"]', '[data-test="flow-node-s2"]')
  await expect(dialog.locator('[data-test="flow-edge-s1-next-s2"]')).toHaveCount(1)
  await expect(dialog.locator('[data-test="sequence-validation"]')).toContainText('校验通过')
  // 拖动节点保存位置
  const node = (await dialog.locator('[data-test="flow-node-s2"]').boundingBox())!
  await page.mouse.move(node.x + 20, node.y + 10); await page.mouse.down(); await page.mouse.move(node.x + 120, node.y + 60, { steps: 5 }); await page.mouse.up()
  await dialog.locator('[data-test="flow-zoom-in"]').click(); await dialog.locator('[data-test="flow-layout"]').click()
  await page.screenshot({ path: test.info().outputPath('flow-editor.png') })
  await dialog.locator('[data-test="sequence-save"]').click()
  await expect(page.locator('[data-test="sequence-name"]')).toBeHidden()
  const seq = (await (await request.get('/api/control/sequences', { headers: admin.headers })).json()).find((x: { name: string }) => x.name === `图形-${u}`)
  expect(seq.steps.map((s: { id: string; type: string; next: string | null }) => [s.id, s.type, s.next])).toEqual([['s1', 'wait', 's2'], ['s2', 'end', null]])
  expect(typeof seq.steps[1].x).toBe('number')

  // 表格视图仍可用（后备）
  await page.locator('[data-test="sequences"]').getByRole('row', { name: new RegExp(`图形-${u}`) }).getByRole('button', { name: '编辑' }).click()
  await dialog.locator('[data-test="sequence-editor-mode"]').getByText('表格').click()
  await expect(dialog.locator('[data-test="sequence-steps"]')).toBeVisible()
  await dialog.getByRole('button', { name: '取消' }).click()

  const start = await request.post(`/api/control/sequences/${seq.id}/start`, { headers: admin.headers, data: { confirmed: true } }); expect(start.ok()).toBeTruthy()
  const runId = (await start.json()).runId
  await page.getByRole('button', { name: '刷新' }).click()
  await page.locator(`[data-test="run-flow-${runId}"]`).click()
  const runDialog = page.locator('.el-overlay:visible')
  await expect(runDialog.locator('[data-test="flow-node-s2"]')).toHaveAttribute('data-status', 'st-done', { timeout: 15000 })
  await expect(runDialog.locator('[data-test="flow-node-s1"]')).toHaveAttribute('data-status', 'st-done')
  await page.screenshot({ path: test.info().outputPath('flow-run.png') })
  await request.delete(`/api/control/sequences/${seq.id}`, { headers: admin.headers })
})
