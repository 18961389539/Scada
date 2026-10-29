import { test, expect, type APIRequestContext, type Page } from './fixtures'

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

test('calc-job snapshot failure stays in the dialog', async ({ page, request }) => {
  const session = await login(request)
  const suffix = Date.now()
  const device = await request.post('/api/devices', {
    headers: session.headers,
    data: {
      name: `SNAP-D-${suffix}`, protocol: 'ModbusTcp', host: '127.0.0.1', port: 34000 + (suffix % 20000), stationNumber: 1,
      byteOrder: 'ABCD', pollIntervalMs: 1000, timeoutMs: 1000, enabled: false, areaId: 1
    }
  })
  expect(device.ok(), await device.text()).toBeTruthy()
  const tag = await request.post('/api/tags', {
    headers: session.headers,
    data: {
      deviceId: (await device.json()).id, name: `SNAP-T-${suffix}`, area: 'HoldingRegister', address: 0, dataType: 'Float',
      scale: 1, offset: 0, writable: false, archiveMode: 'None', archivePeriodSec: 60, deadband: 0
    }
  })
  expect(tag.ok(), await tag.text()).toBeTruthy()
  const jobName = `SNAP-J-${suffix}`
  const job = await request.post('/api/calc-jobs', {
    headers: session.headers,
    data: { name: jobName, kind: 'Accumulator', areaId: 1, inputTagId: (await tag.json()).id, mode: 'Flow', factor: 1, resetPeriod: 'Day' }
  })
  expect(job.status(), await job.text()).toBe(201)
  const created = await job.json()
  await sign(page, session)
  await page.route((url) => url.pathname === `/api/calc-jobs/${created.id}/snapshots`, (route) =>
    route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message: '快照不可用' }) }))
  await page.goto('/calc-jobs')
  await page.locator('[data-test="calc-job-table"] .el-table__row', { hasText: jobName }).getByRole('button', { name: '快照', exact: true }).click()
  const dialog = page.getByRole('dialog', { name: `复位快照 · ${jobName}` })
  await expect(dialog.locator('[data-test="calc-job-snapshots-error"]')).toContainText('快照不可用')
  await expect(dialog.getByText('暂无快照')).toHaveCount(0)
})

test('project compare, rollback preview, and import preview failures stay on the page', async ({ page, request }) => {
  const session = await login(request)
  const note = `PREV-${Date.now()}`
  const version = await request.post('/api/project/versions', { headers: session.headers, data: { note } })
  expect(version.ok(), await version.text()).toBeTruthy()
  const id = (await version.json()).id as number
  await sign(page, session)
  await page.route((url) => url.pathname === '/api/project/diff', (route) =>
    route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message: '比较不可用' }) }))
  await page.route((url) => /\/api\/project\/versions\/\d+\/rollback\/preview$/.test(url.pathname), (route) =>
    route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message: '回退预览不可用' }) }))
  await page.route((url) => url.pathname === '/api/project/import/preview', (route) =>
    route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message: '导入预览不可用' }) }))
  await page.goto('/config/project')
  await expect(page.locator('[data-test="project-versions"]')).toContainText(note)
  await page.locator('[data-test="project-compare"]').click()
  await expect(page.locator('[data-test="project-compare-error"]')).toContainText('比较不可用')
  await expect(page.locator('[data-test="plan-table"]')).toHaveCount(0)
  await page.locator(`[data-test="project-rollback-${id}"]`).click()
  const dialog = page.getByRole('dialog', { name: `回滚到配置版本 #${id}` })
  await expect(dialog.locator('[data-test="project-rollback-error"]')).toContainText('回退预览不可用')
  await expect(dialog.locator('[data-test="project-rollback-confirm"]')).toBeDisabled()
  await dialog.getByRole('button', { name: '取消', exact: true }).click()
  await expect(dialog).not.toBeVisible()
  await page.getByRole('tab', { name: '导入发布', exact: true }).click()
  await page.locator('[data-test="project-file"]').setInputFiles({ name: 'pkg.json', mimeType: 'application/json', buffer: Buffer.from('{"schema":1}') })
  await expect(page.locator('[data-test="project-import-error"]')).toContainText('导入预览不可用')
  await expect(page.locator('[data-test="project-apply"]')).toBeDisabled()
  await expect(page.locator('[data-test="plan-table"]')).toHaveCount(0)
})
