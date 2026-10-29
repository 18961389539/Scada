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

function json(status: number, message: string) {
  return { status, contentType: 'application/json', body: JSON.stringify({ message }) }
}

test('report export failure stays on the page', async ({ page, request }) => {
  const session = await login(request)
  const suffix = Date.now()
  const device = await request.post('/api/devices', {
    headers: session.headers,
    data: {
      name: `RPT-D-${suffix}`, protocol: 'ModbusTcp', host: '127.0.0.1', port: 1502, stationNumber: 1,
      byteOrder: 'ABCD', pollIntervalMs: 1000, timeoutMs: 1000, enabled: false, areaId: 1
    }
  })
  expect(device.ok(), await device.text()).toBeTruthy()
  const tag = await request.post('/api/tags', {
    headers: session.headers,
    data: {
      deviceId: (await device.json()).id, name: `RPT-T-${suffix}`, area: 'HoldingRegister', address: 2, dataType: 'Float',
      scale: 1, offset: 0, writable: false, archiveMode: 'None', deadband: 0, archivePeriodSec: 60
    }
  })
  expect(tag.ok(), await tag.text()).toBeTruthy()
  const tagId = (await tag.json()).id as number
  await sign(page, session)
  await page.route((url) => url.pathname === '/api/reports' || url.pathname === '/api/reports/export', async (route) => {
    const path = new URL(route.request().url()).pathname
    if (path === '/api/reports/export') {
      await route.fulfill(json(500, '报表导出不可用'))
      return
    }
    await route.fulfill({
      status: 200, contentType: 'application/json',
      body: JSON.stringify({ type: 'daily', from: 0, to: 1, series: [{ tagId, points: [{ ts: Date.now(), avg: 1, min: 0, max: 2, count: 1 }] }] })
    })
  })
  await page.goto('/reports')
  await page.getByRole('button', { name: '生成报表' }).click()
  await expect(page.getByRole('button', { name: '导出 Excel' })).toBeEnabled()
  await page.getByRole('button', { name: '导出 Excel' }).click()
  await expect(page.locator('[data-test="reports-export-error"]')).toContainText('报表导出不可用')
  await expect(page.locator('[data-test="reports-empty"]')).toHaveCount(0)
})

test('realtime write failure stays in the write dialog', async ({ page, request }) => {
  const session = await login(request)
  const suffix = Date.now()
  const device = await request.post('/api/devices', {
    headers: session.headers,
    data: {
      name: `RT-D-${suffix}`, protocol: 'ModbusTcp', host: '127.0.0.1', port: 1502, stationNumber: 1,
      byteOrder: 'ABCD', pollIntervalMs: 1000, timeoutMs: 1000, enabled: false, areaId: 1
    }
  })
  expect(device.ok(), await device.text()).toBeTruthy()
  const tag = await request.post('/api/tags', {
    headers: session.headers,
    data: {
      deviceId: (await device.json()).id, name: `RT-T-${suffix}`, area: 'HoldingRegister', address: 3, dataType: 'Float',
      scale: 1, offset: 0, writable: true, archiveMode: 'None', deadband: 0, archivePeriodSec: 60
    }
  })
  expect(tag.ok(), await tag.text()).toBeTruthy()
  const tagName = (await tag.json()).name as string
  await sign(page, session)
  await page.route((url) => url.pathname === '/api/control/check', (route) => {
    if (route.request().method() !== 'POST') return route.continue()
    return route.fulfill({
      status: 200, contentType: 'application/json',
      body: JSON.stringify({
        tagId: 1, tagName, current: 0, refusals: [], requireConfirm: true, dualApproval: false,
        limitMin: null, limitMax: null, maxStep: null, feedback: null, mode: 'Remote', lockouts: []
      })
    })
  })
  await page.route((url) => url.pathname === '/api/commands', (route) => {
    if (route.request().method() !== 'POST') return route.continue()
    return route.fulfill(json(500, '写入不可用'))
  })
  await page.goto('/realtime')
  const row = page.getByRole('row').filter({ hasText: tagName })
  await row.getByRole('button', { name: '写值' }).click()
  const dialog = page.getByRole('dialog', { name: tagName })
  await dialog.getByRole('button', { name: '下发' }).click()
  await page.locator('[data-test="control-confirm-ok"]').click()
  await expect(dialog.locator('[data-test="realtime-write-error"]')).toContainText('写入不可用')
  await expect(dialog).toBeVisible()
})

test('screen command failure stays on the runtime page', async ({ page, request }) => {
  test.setTimeout(60000)
  const session = await login(request)
  const suffix = Date.now()
  const device = await request.post('/api/devices', {
    headers: session.headers,
    data: {
      name: `SC-D-${suffix}`, protocol: 'ModbusTcp', host: '127.0.0.1', port: 1502, stationNumber: 1,
      byteOrder: 'ABCD', pollIntervalMs: 1000, timeoutMs: 1000, enabled: false, areaId: 1
    }
  })
  expect(device.ok(), await device.text()).toBeTruthy()
  const tag = await request.post('/api/tags', {
    headers: session.headers,
    data: {
      deviceId: (await device.json()).id, name: `SC-T-${suffix}`, area: 'HoldingRegister', address: 4, dataType: 'Float',
      scale: 1, offset: 0, writable: true, archiveMode: 'None', deadband: 0, archivePeriodSec: 60
    }
  })
  expect(tag.ok(), await tag.text()).toBeTruthy()
  const tagId = (await tag.json()).id as number
  const created = await request.post('/api/screens', { headers: session.headers, data: { name: `画面${suffix}` } })
  expect(created.status()).toBe(201)
  const screen = await created.json()
  const document = {
    schemaVersion: 1, width: 800, height: 480, background: '#101b2d',
    nodes: [{
      id: 'cmd-1', type: 'button', x: 40, y: 40, width: 160, height: 48, rotation: 0,
      text: `开泵${suffix}`, color: '#7394b8', activeColor: '#34d399', unit: '', decimals: 1,
      min: 0, max: 100, activeThreshold: 0.5, tagId: null,
      action: { type: 'command', tagId, screenId: null, value: 1 }
    }]
  }
  const saved = await request.put(`/api/screens/${screen.id}`, {
    headers: session.headers, data: { name: `画面${suffix}`, revision: screen.revision, document }
  })
  expect(saved.ok(), await saved.text()).toBeTruthy()
  const published = await request.post(`/api/screens/${screen.id}/publish`, {
    headers: session.headers, data: { revision: (await saved.json()).revision }
  })
  expect(published.ok(), await published.text()).toBeTruthy()
  await sign(page, session)
  await page.route((url) => url.pathname === '/api/control/check', (route) => {
    if (route.request().method() !== 'POST') return route.continue()
    return route.fulfill({
      status: 200, contentType: 'application/json',
      body: JSON.stringify({
        tagId, tagName: `SC-T-${suffix}`, current: 0, refusals: [], requireConfirm: true, dualApproval: false,
        limitMin: null, limitMax: null, maxStep: null, feedback: null, mode: 'Remote', lockouts: []
      })
    })
  })
  await page.route((url) => /\/api\/screens\/\d+\/actions\//.test(url.pathname), (route) => {
    if (route.request().method() !== 'POST') return route.continue()
    return route.fulfill(json(500, '控制不可用'))
  })
  await page.goto(`/screens/${screen.id}`)
  await page.locator('[data-node-id="cmd-1"]').click()
  await page.locator('[data-test="control-confirm-ok"]').click()
  await expect(page.locator('[data-test="runtime-command-error"]')).toContainText('控制不可用')
  await expect(page.locator('[data-node-id="cmd-1"]')).toBeVisible()
})

test('oee save, delete, export and threshold failures stay on the page', async ({ page, request }) => {
  test.setTimeout(60000)
  const session = await login(request)
  const suffix = `${Date.now()}`.slice(-6)
  const name = `OEE线${suffix}`
  const created = await request.post('/api/oee/lines', {
    headers: session.headers,
    data: {
      name, areaId: 1, description: null, stateTagId: null, stateMapJson: '{}',
      goodCountTagId: null, rejectCountTagId: null, totalCountTagId: null, productId: null,
      idealCycleSec: 1, ratedUnitsPerHour: null, targetUnitsPerHour: null, enabled: true
    }
  })
  expect(created.ok(), await created.text()).toBeTruthy()
  const lineId = (await created.json()).id as number
  await sign(page, session)
  await page.route((url) => url.pathname === '/api/oee/lines', (route) => {
    if (route.request().method() !== 'POST') return route.continue()
    return route.fulfill(json(500, '保存不可用'))
  })
  await page.route((url) => url.pathname === `/api/oee/lines/${lineId}`, (route) => {
    if (route.request().method() !== 'DELETE') return route.continue()
    return route.fulfill(json(500, '删除不可用'))
  })
  await page.route((url) => url.pathname === '/api/oee/export', (route) => route.fulfill(json(500, '导出不可用')))
  await page.route((url) => url.pathname === '/api/oee/thresholds', (route) => {
    if (route.request().method() !== 'GET') return route.continue()
    return route.fulfill(json(500, '阈值不可用'))
  })
  await page.goto('/oee')
  const exportButton = page.locator('[data-test="oee-export-xlsx"]')
  await expect(exportButton).toBeEnabled()
  await exportButton.click()
  await expect(page.locator('[data-test="oee-action-error"]')).toContainText('导出不可用')
  await page.goto('/config/oee')
  await expect(page.locator('[data-test="oee-add-line"]')).toBeVisible({ timeout: 15000 })
  await page.locator('[data-test="oee-thresholds"]').click()
  await expect(page.locator('[data-test="oee-action-error"]')).toContainText('阈值不可用')
  await expect(page.getByRole('dialog', { name: 'OEE 阈值' })).not.toBeVisible()

  await page.getByRole('tab', { name: '产线', exact: true }).click()
  await page.locator('[data-test="oee-add-line"]').click()
  const dialog = page.getByRole('dialog', { name: '产线' })
  await dialog.locator('[data-test="oee-line-name"]').fill(`新${name}`)
  await dialog.locator('[data-test="oee-line-save"]').click()
  await expect(dialog.locator('[data-test="oee-form-error"]')).toContainText('保存不可用')
  await expect(dialog).toBeVisible()
  await dialog.getByRole('button', { name: '取消' }).click()

  const row = page.locator('[data-test="oee-lines"]').getByRole('row').filter({ hasText: name })
  await row.getByRole('button', { name: '删除' }).click()
  await page.getByRole('dialog', { name: '删除' }).getByRole('button', { name: '删除' }).click()
  await expect(page.locator('[data-test="oee-action-error"]')).toContainText('删除不可用')
  await expect(row).toBeVisible()
})

test('alarm analytics query, export and threshold failures stay on the page', async ({ page, request }) => {
  test.setTimeout(60000)
  const session = await login(request)
  await sign(page, session)
  let analyzeFail = false
  await page.route((url) => url.pathname === '/api/alarm-analytics', (route) => {
    if (analyzeFail) return route.fulfill(json(500, '报警分析不可用'))
    return route.continue()
  })
  await page.route((url) => url.pathname === '/api/alarm-analytics/export', (route) => route.fulfill(json(500, '导出不可用')))
  await page.route((url) => url.pathname === '/api/alarm-analytics/thresholds', (route) => {
    if (route.request().method() !== 'GET') return route.continue()
    return route.fulfill(json(500, '阈值不可用'))
  })
  await page.goto('/alarm-analytics')
  const exportButton = page.locator('[data-test="aa-export-xlsx"]')
  await expect(exportButton).toBeEnabled()
  analyzeFail = true
  await page.locator('[data-test="aa-query"]').click()
  await expect(page.locator('[data-test="aa-query-error"]')).toContainText('报警分析不可用')
  await expect(exportButton).toBeEnabled()
  await exportButton.click()
  await expect(page.locator('[data-test="aa-action-error"]')).toContainText('导出不可用')
  await page.locator('[data-test="aa-thresholds"]').click()
  await expect(page.locator('[data-test="aa-action-error"]')).toContainText('阈值不可用')
  await expect(page.getByRole('dialog', { name: '分析阈值' })).not.toBeVisible()
})

test('project export, download, snapshot and bad json stay on the page', async ({ page, request }) => {
  test.setTimeout(60000)
  const session = await login(request)
  const note = `快照${Date.now()}`
  const version = await request.post('/api/project/versions', { headers: session.headers, data: { note } })
  expect(version.ok(), await version.text()).toBeTruthy()
  await sign(page, session)
  await page.route((url) => url.pathname === '/api/project/export', (route) => route.fulfill(json(500, '工程导出不可用')))
  await page.route((url) => /\/api\/project\/versions\/\d+\/download$/.test(url.pathname), (route) => route.fulfill(json(500, '版本下载不可用')))
  await page.goto('/config/project')
  await page.locator('[data-test="project-export"]').click()
  await expect(page.locator('[data-test="project-action-error"]')).toContainText('工程导出不可用')
  const row = page.locator('[data-test="project-versions"]').getByRole('row').filter({ hasText: note })
  await row.getByRole('button', { name: '下载' }).click()
  await expect(page.locator('[data-test="project-action-error"]')).toContainText('版本下载不可用')
  await page.locator('[data-test="project-snapshot"]').click()
  await expect(page.locator('[data-test="project-action-error"]')).toContainText('请填写快照说明')
  await page.getByRole('tab', { name: '导入发布' }).click()
  await page.locator('[data-test="project-file"]').setInputFiles({
    name: 'bad.json', mimeType: 'application/json', buffer: Buffer.from('not-json')
  })
  await expect(page.locator('[data-test="project-import-error"]')).toContainText('文件不是有效的工程包 JSON')
})

test('problem details and validation errors reach the page instead of the generic reason phrase', async ({ page, request }) => {
  const session = await login(request)
  await sign(page, session)
  // ProblemDetails：中文原因在 detail，title 只是 ASP.NET 的通用英文短语。
  await page.route((url) => url.pathname === '/api/project/export', (route) => route.fulfill({
    status: 503,
    contentType: 'application/problem+json',
    body: JSON.stringify({ type: 'about:blank', title: 'Service Unavailable', status: 503, detail: '工程导出不可用：中心存储只读' })
  }))
  await page.goto('/config/project')
  await page.locator('[data-test="project-export"]').click()
  const action = page.locator('[data-test="project-action-error"]')
  await expect(action).toContainText('工程导出不可用：中心存储只读')
  await expect(action).not.toContainText('Service Unavailable')
  // 真实服务端路径：未知分区由 ProjectFormat.Parse 抛 ProjectException → Results.Problem(detail)。
  await page.getByRole('tab', { name: '导入发布' }).click()
  await page.locator('[data-test="project-file"]').setInputFiles({
    name: 'unknown-section.json', mimeType: 'application/json',
    buffer: Buffer.from(JSON.stringify({ format: 1, sections: { bogus: [] } }))
  })
  const importError = page.locator('[data-test="project-import-error"]')
  await expect(importError).toContainText('未知分区：bogus')
  await expect(importError).not.toContainText('Bad Request')
})

test('trend catalog failure stays on the page', async ({ page, request }) => {
  const session = await login(request)
  await sign(page, session)
  await page.route((url) => url.pathname === '/api/tags', (route) => {
    if (route.request().method() !== 'GET') return route.continue()
    return route.fulfill(json(500, '测点目录不可用'))
  })
  await page.goto('/trends')
  await expect(page.locator('[data-test="trend-catalog-error"]')).toContainText('测点目录不可用')
  await expect(page.locator('[data-test="trend-query-error"]')).toHaveCount(0)
})
