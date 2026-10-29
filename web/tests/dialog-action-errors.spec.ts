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

test('diagnostic reconnect and export failures stay in the dialog', async ({ page, request }) => {
  const session = await login(request)
  const suffix = Date.now()
  const created = await request.post('/api/devices', {
    headers: session.headers,
    data: {
      name: `ACT-D-${suffix}`, protocol: 'ModbusTcp', host: '127.0.0.1', port: 33000 + (suffix % 20000), stationNumber: 1,
      byteOrder: 'ABCD', pollIntervalMs: 1000, timeoutMs: 1000, enabled: false, areaId: 1
    }
  })
  expect(created.ok(), await created.text()).toBeTruthy()
  const device = await created.json()
  const snapshot = {
    diagnostics: {
      deviceId: device.id, name: device.name, protocol: 'Modbus TCP', connection: '127.0.0.1:502', state: 'Online', operation: 'Idle',
      operationTagId: null, currentError: null, enabled: true, configurationPending: false, configVersion: 1,
      capturedTs: Date.now(), startedTs: Date.now(), pollIntervalMs: 1000, timeoutMs: 1000, staleAfterMs: 5000,
      pendingCommands: 0, pointCount: 1,
      quality: { good: 1, bad: 0, uncertain: 0, commFailure: 0, unknown: 0, stale: 0 },
      metrics: {
        connectAttempts: 1, connectFailures: 0, pollAttempts: 1, pollSuccesses: 1, pollFailures: 0, degradedPolls: 0, consecutiveFailures: 0,
        lastAttemptTs: null, lastSuccessTs: null, lastFailureTs: null, nextRetryTs: null, lastFailure: null,
        commandsSucceeded: 0, commandsFailed: 0, commandsExpired: 0, commandsRejected: 0, lastCommandTs: null,
        lastPollMs: 1, windowAvgMs: 1, windowMaxMs: 1, windowP95Ms: 1, windowSamples: 1
      },
      readGroups: [], points: [], events: [], samples: []
    },
    total: 0, page: 1, pageSize: 100
  }
  await page.clock.install()
  await sign(page, session)
  await page.route((url) => url.pathname === `/api/devices/${device.id}/diagnostics`, (route) => {
    if (route.request().method() !== 'GET') return route.continue()
    return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(snapshot) })
  })
  await page.route((url) => url.pathname === `/api/devices/${device.id}/diagnostics/reconnect`, (route) =>
    route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message: '重连失败' }) }))
  await page.route((url) => url.pathname === `/api/devices/${device.id}/diagnostics/export`, (route) =>
    route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message: '导出失败' }) }))
  await page.goto('/config/devices')
  await page.getByRole('row', { name: device.name }).getByRole('button', { name: '采集诊断', exact: true }).click()
  const dialog = page.getByRole('dialog', { name: `采集诊断 · ${device.name}` })
  await dialog.getByRole('button', { name: '请求重连', exact: true }).click()
  await expect(dialog.locator('[data-test="diagnostics-action-error"]')).toContainText('重连失败')
  await page.clock.fastForward(4000)
  await expect(dialog.locator('[data-test="diagnostics-action-error"]')).toContainText('重连失败')
  await dialog.getByRole('button', { name: '导出诊断', exact: true }).click()
  await expect(dialog.locator('[data-test="diagnostics-action-error"]')).toContainText('导出失败')
})

test('OPC import preview and apply failures stay in the dialog', async ({ page, request }) => {
  const session = await login(request)
  const suffix = Date.now()
  const created = await request.post('/api/devices', {
    headers: session.headers,
    data: {
      name: `ACT-OPC-${suffix}`, protocol: 'OpcUa', host: '127.0.0.1', port: 4840, stationNumber: 1,
      byteOrder: 'ABCD', pollIntervalMs: 1000, timeoutMs: 1000, enabled: false, areaId: 1,
      opcEndpointUrl: 'opc.tcp://127.0.0.1:9/stub', opcSecurityMode: 'None', opcSecurityPolicy: 'None', opcUserName: ''
    }
  })
  expect(created.ok(), await created.text()).toBeTruthy()
  const device = await created.json()
  await sign(page, session)
  let previewFail = true
  await page.route((url) => url.pathname === `/api/devices/${device.id}/opc/browse`, (route) => route.fulfill({
    status: 200, contentType: 'application/json',
    body: JSON.stringify({
      nodes: [{ nodeId: 'ns=2;s=PumpSpeed', displayName: 'PumpSpeed', nodeClass: 'Variable', dataType: 'Float', readable: true, writable: true, error: null }],
      cursor: null
    })
  }))
  await page.route((url) => url.pathname === `/api/devices/${device.id}/opc/import/preview`, (route) => {
    if (previewFail) return route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message: '预览失败' }) })
    return route.fulfill({
      status: 200, contentType: 'application/json',
      body: JSON.stringify({
        id: 'prev-1', canApply: true, errors: [], expiresAt: new Date(Date.now() + 60000).toISOString(),
        rows: [{ nodeId: 'ns=2;s=PumpSpeed', name: 'PumpSpeed', dataType: 'Float', writable: false, error: null }]
      })
    })
  })
  await page.route((url) => url.pathname === `/api/devices/${device.id}/opc/import/prev-1`, (route) =>
    route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message: '导入失败' }) }))
  await page.goto('/config/devices')
  await page.getByRole('row', { name: device.name }).getByRole('button', { name: '浏览变量', exact: true }).click()
  const browser = page.getByRole('dialog', { name: `OPC UA 变量 · ${device.name}` })
  await expect(browser.getByRole('button', { name: 'PumpSpeed', exact: true })).toBeVisible()
  await browser.locator('.el-table__body').first().locator('.el-checkbox').click()
  await browser.getByRole('button', { name: '预览导入', exact: true }).click()
  await expect(browser.locator('[data-test="opc-error"]')).toContainText('预览失败')
  await expect(page.getByRole('dialog', { name: 'OPC UA 导入预览' })).not.toBeVisible()
  previewFail = false
  await browser.getByRole('button', { name: '预览导入', exact: true }).click()
  const preview = page.getByRole('dialog', { name: 'OPC UA 导入预览' })
  await expect(preview).toBeVisible()
  await preview.getByRole('button', { name: '确认创建测点', exact: true }).click()
  await expect(preview.locator('[data-test="opc-apply-error"]')).toContainText('导入失败')
  await expect(preview.getByRole('button', { name: '确认创建测点', exact: true })).toBeEnabled()
})
