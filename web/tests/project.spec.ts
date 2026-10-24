import { test, expect, type APIRequestContext } from '@playwright/test'

async function auth(request: APIRequestContext, userName = 'admin', password = 'admin123') {
  const response = await request.post('/api/auth/login', { data: { userName, password } }); expect(response.ok()).toBeTruthy()
  const result = await response.json(); return { ...result, headers: { Authorization: `Bearer ${result.token}` } }
}
const device = (name: string) => ({ name, protocol: 'ModbusTcp', host: '127.0.0.1', port: 1502, stationNumber: 1, byteOrder: 'ABCD', pollIntervalMs: 1000, timeoutMs: 500, enabled: false, areaId: 1 })

test('project management: snapshot, compare, rollback with impact preview, and offline-edited package import with conflict choice', async ({ page, request }) => {
  test.setTimeout(90000)
  const session = await auth(request), headers = session.headers, u = `${Date.now()}`.slice(-6)
  await page.addInitScript(({ token, user }) => { localStorage.setItem('scada_token', token); localStorage.setItem('scada_user', JSON.stringify(user)) }, session)
  await page.goto('/config/project')
  await page.locator('[data-test="project-note"]').fill(`基线-${u}`)
  await page.locator('[data-test="project-snapshot"]').click()
  await expect(page.locator('[data-test="project-versions"]')).toContainText(`基线-${u}`)
  const baseline = (await (await request.get('/api/project/versions', { headers })).json())[0]

  // 快照之后新增设备：比较与回滚预览都能看到，确认后设备被删除。
  const created = await request.post('/api/devices', { headers, data: device(`临时-${u}`) }); expect(created.status(), await created.text()).toBe(201)
  await page.locator('[data-test="project-compare"]').click()
  await expect(page.locator('[data-test="plan-table"]').first()).toContainText(`临时-${u}`)
  await page.locator(`[data-test="project-rollback-${baseline.id}"]`).click()
  const dialog = page.locator('.el-dialog:visible')
  await expect(dialog).toContainText(`临时-${u}`); await expect(dialog).toContainText('删除')
  await page.screenshot({ path: test.info().outputPath('project-rollback.png'), animations: 'disabled' })
  await page.locator('[data-test="project-rollback-confirm"]').click()
  await page.locator('.el-message-box').getByRole('button', { name: '回滚' }).click()
  await expect(dialog).toBeHidden({ timeout: 20000 })
  const devices = await (await request.get('/api/devices', { headers })).json()
  expect(devices.some((d: any) => d.name === `临时-${u}`)).toBe(false)

  // 离线编辑：导出 → 改设备参数并新增设备 → 上传预览（冲突默认跳过，单项改为覆盖）→ 发布。
  const keep = await request.post('/api/devices', { headers, data: device(`编辑-${u}`) }); expect(keep.status()).toBe(201)
  const pkg = await (await request.get('/api/project/export', { headers })).json()
  const item = pkg.sections.devices.find((x: any) => x.key === `编辑-${u}`); item.data.pollIntervalMs = 2500
  pkg.sections.devices.push({ key: `离线-${u}`, areas: item.areas, data: { ...item.data, name: `离线-${u}` } })
  await page.reload()
  await page.getByRole('tab', { name: '导入发布' }).click()
  await page.locator('[data-test="project-file"]').setInputFiles({ name: 'project.json', mimeType: 'application/json', buffer: Buffer.from(JSON.stringify(pkg)) })
  const plan = page.locator('[data-test="plan-table"]').last()
  await expect(plan).toContainText(`离线-${u}`); await expect(plan).toContainText('跳过')
  const row = plan.locator('.el-table__row', { hasText: `编辑-${u}` })
  await row.locator('.el-select').click()
  await page.locator('.el-select-dropdown:visible').getByRole('option', { name: '覆盖', exact: true }).click()
  await expect(row).toContainText('pollIntervalMs')
  await page.locator('[data-test="project-apply"]').click()
  await page.locator('.el-message-box').getByRole('button', { name: '发布' }).click()
  await expect(page.locator('.el-message--success')).toContainText('已发布', { timeout: 20000 })
  const after = await (await request.get('/api/devices', { headers })).json()
  expect(after.find((d: any) => d.name === `编辑-${u}`).pollIntervalMs).toBe(2500)
  expect(after.some((d: any) => d.name === `离线-${u}`)).toBe(true)
  const versions = await (await request.get('/api/project/versions', { headers })).json()
  expect(versions[0].kind).toBe('import')
})
