import { test, expect, type APIRequestContext, type Page } from '@playwright/test'

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

test('calc job reset and delete failures stay on the page', async ({ page, request }) => {
  const session = await login(request)
  const suffix = `${Date.now()}`
  const device = await request.post('/api/devices', {
    headers: session.headers,
    data: { name: `job-${suffix}`, protocol: 'ModbusTcp', host: '127.0.0.1', port: 1502, stationNumber: 1, byteOrder: 'ABCD', pollIntervalMs: 1000, timeoutMs: 1000, enabled: false }
  })
  expect(device.ok()).toBeTruthy()
  const tag = await request.post('/api/tags', {
    headers: session.headers,
    data: {
      deviceId: (await device.json()).id, name: `job-tag-${suffix}`, area: 'HoldingRegister', address: 0, dataType: 'Float',
      scale: 1, offset: 0, writable: false, archiveMode: 'None', archivePeriodSec: 60, deadband: 0
    }
  })
  expect(tag.ok()).toBeTruthy()
  const name = `任务${suffix}`
  const job = await request.post('/api/calc-jobs', {
    headers: session.headers,
    data: { name, kind: 'Accumulator', areaId: 1, inputTagId: (await tag.json()).id, mode: 'Flow', factor: 1, resetPeriod: 'Day' }
  })
  expect(job.status()).toBe(201)
  const created = await job.json()
  await sign(page, session)
  await page.route((url) => url.pathname === `/api/calc-jobs/${created.id}/reset`, (route) =>
    route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message: '复位不可用' }) }))
  await page.route((url) => url.pathname === `/api/calc-jobs/${created.id}`, (route) => {
    if (route.request().method() !== 'DELETE') return route.continue()
    return route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message: '删除不可用' }) })
  })
  await page.goto('/calc-jobs')
  const row = page.locator('[data-test="calc-job-table"]').getByRole('row').filter({ hasText: name })
  await row.getByRole('button', { name: '复位', exact: true }).click()
  await page.getByRole('dialog', { name: '确认复位' }).getByRole('button', { name: '确定' }).click()
  await expect(page.locator('[data-test="calc-job-action-error"]')).toContainText('复位不可用')
  await expect(row).toBeVisible()
  await row.getByRole('button', { name: '删除', exact: true }).click()
  await page.getByRole('dialog', { name: '确认删除' }).getByRole('button', { name: '确定' }).click()
  await expect(page.locator('[data-test="calc-job-action-error"]')).toContainText('删除不可用')
  await expect(row).toBeVisible()
})

test('calculated tag delete failure stays on the page', async ({ page, request }) => {
  const session = await login(request)
  const name = `计算${Date.now()}`
  const created = await request.post('/api/calculated-tags', { headers: session.headers, data: { name, expression: '1', intervalMs: 1000 } })
  expect(created.ok(), await created.text()).toBeTruthy()
  const tag = await created.json()
  await sign(page, session)
  await page.route((url) => url.pathname === `/api/calculated-tags/${tag.id}`, (route) => {
    if (route.request().method() !== 'DELETE') return route.continue()
    return route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message: '删除不可用' }) })
  })
  await page.goto('/config/calculations')
  const row = page.getByRole('row').filter({ hasText: name })
  await row.getByRole('button', { name: '删除', exact: true }).click()
  await page.getByRole('dialog', { name: '删除计算测点' }).getByRole('button', { name: '确认删除' }).click()
  await expect(page.locator('[data-test="calc-action-error"]')).toContainText('删除不可用')
  await expect(row).toBeVisible()
})

test('alarm rule save, disable, delete and tag catalog failures stay visible', async ({ page, request }) => {
  const session = await login(request)
  const suffix = `${Date.now()}`
  const tag = await request.post('/api/calculated-tags', { headers: session.headers, data: { name: `规则点${suffix}`, expression: '1', intervalMs: 1000 } })
  expect(tag.ok()).toBeTruthy()
  const tagId = (await tag.json()).id
  const message = `规则${suffix}`
  const rule = await request.post('/api/alarm-rules', {
    headers: session.headers,
    data: { tagId, type: 'High', threshold: 10, level: 'Warning', delaySec: 0, recoverDeadband: 0, enabled: true, message }
  })
  expect(rule.ok(), await rule.text()).toBeTruthy()
  const ruleId = (await rule.json()).id
  await sign(page, session)
  await page.route((url) => url.pathname === '/api/alarm-rules', (route) => {
    if (route.request().method() !== 'POST') return route.continue()
    return route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message: '保存不可用' }) })
  })
  await page.route((url) => url.pathname === `/api/alarm-rules/${ruleId}/out-of-service`, (route) =>
    route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message: '停用不可用' }) }))
  await page.route((url) => url.pathname === `/api/alarm-rules/${ruleId}`, (route) => {
    if (route.request().method() !== 'DELETE') return route.continue()
    return route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message: '删除不可用' }) })
  })
  await page.goto('/config/rules')
  await page.locator('[data-test="rule-create"]').click()
  const dialog = page.getByRole('dialog', { name: '新增报警规则' })
  await dialog.locator('[data-test="rule-save"]').click()
  await expect(dialog.locator('[data-test="rule-save-error"]')).toContainText('保存不可用')
  await expect(dialog).toBeVisible()
  await dialog.getByRole('button', { name: '取消', exact: true }).click()
  const row = page.getByRole('row').filter({ hasText: message })
  await row.getByRole('button', { name: '停用', exact: true }).click()
  const prompt = page.getByRole('dialog', { name: '停用报警规则' })
  await prompt.getByRole('textbox').fill('检修')
  await prompt.getByRole('button', { name: '停用', exact: true }).click()
  await expect(page.locator('[data-test="rules-action-error"]')).toContainText('停用不可用')
  await expect(row).toBeVisible()
  await row.getByRole('button', { name: '删除', exact: true }).click()
  await page.locator('.el-popconfirm').getByRole('button', { name: '确定' }).click()
  await expect(page.locator('[data-test="rules-action-error"]')).toContainText('删除不可用')
  await expect(row).toBeVisible()
  await page.route((url) => url.pathname === '/api/tags', (route) => {
    if (route.request().method() !== 'GET') return route.continue()
    return route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message: '测点目录不可用' }) })
  })
  await page.reload()
  await expect(page.locator('[data-test="rules-catalog-error"]')).toContainText('测点目录不可用')
  await expect(page.locator('[data-test="rules-load-error"]')).toHaveCount(0)
  await expect(page.getByRole('row').filter({ hasText: message })).toBeVisible()
})

test('gateway name, toggle, rotate and copy failures stay visible', async ({ page, request }) => {
  const session = await login(request)
  const name = `旧${Date.now()}`
  const created = await request.post('/api/gateways', { headers: session.headers, data: { name } })
  expect(created.ok(), await created.text()).toBeTruthy()
  const gateway = await created.json()
  await sign(page, session)
  await page.addInitScript(() => {
    Object.defineProperty(navigator, 'clipboard', { configurable: true, value: { writeText: () => Promise.reject(new Error('denied')) } })
  })
  await page.route((url) => url.pathname === `/api/gateways/${gateway.id}`, (route) => {
    if (route.request().method() !== 'PUT') return route.continue()
    return route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message: '停用不可用' }) })
  })
  await page.route((url) => url.pathname === `/api/gateways/${gateway.id}/rotate-key`, (route) =>
    route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message: '轮换不可用' }) }))
  await page.goto('/config/gateways')
  await page.getByRole('button', { name: '新建网关', exact: true }).click()
  const createDialog = page.getByRole('dialog', { name: '新建网关' })
  await createDialog.getByRole('button', { name: '创建并生成密钥' }).click()
  await expect(createDialog.locator('[data-test="gateway-save-error"]')).toContainText('网关名称须为 1～100 个字符')
  await expect(createDialog).toBeVisible()
  await createDialog.locator('[data-test="gateway-name"]').fill(`新${Date.now()}`)
  await createDialog.getByRole('button', { name: '创建并生成密钥' }).click()
  const secret = page.getByRole('dialog', { name: '保存本次网关密钥' })
  await secret.getByRole('button', { name: '复制密钥', exact: true }).click()
  await expect(secret.locator('[data-test="gateway-copy-error"]')).toContainText('复制失败')
  await expect(secret).toBeVisible()
  await secret.getByRole('button', { name: '关闭并清除' }).click()
  const row = page.getByRole('row').filter({ hasText: name })
  await row.getByRole('button', { name: '更多网关操作' }).click()
  await page.getByRole('menuitem', { name: '停用网关' }).click()
  await page.getByRole('dialog', { name: '停用网关' }).getByRole('button', { name: '确认操作' }).click()
  await expect(page.locator('[data-test="gateway-action-error"]')).toContainText('停用不可用')
  await expect(row).toBeVisible()
  await row.getByRole('button', { name: '更多网关操作' }).click()
  await page.getByRole('menuitem', { name: '轮换密钥' }).click()
  await page.getByRole('dialog', { name: '轮换网关密钥' }).getByRole('button', { name: '确认轮换' }).click()
  await expect(page.locator('[data-test="gateway-action-error"]')).toContainText('轮换不可用')
  await expect(row).toBeVisible()
})

test('certificate trust and download failures stay in the dialog', async ({ page, request }) => {
  const session = await login(request)
  await sign(page, session)
  const rejected = 'a'.repeat(64)
  const own = 'b'.repeat(64)
  const cert = (sha256: string, store: string, subject: string) => ({
    sha256, thumbprint: sha256.slice(0, 12), subject, issuer: 'CN=Stub',
    notBefore: '2020-01-01T00:00:00Z', notAfter: '2030-01-01T00:00:00Z', store
  })
  await page.route((url) => url.pathname === '/api/opc/certificates', (route) => {
    if (route.request().method() !== 'GET') return route.continue()
    return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify([
      cert(rejected, 'rejected', 'CN=Rejected'), cert(own, 'own', 'CN=Own')
    ]) })
  })
  await page.route((url) => url.pathname === `/api/opc/certificates/${rejected}/trust`, (route) =>
    route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message: '信任不可用' }) }))
  await page.route((url) => url.pathname === `/api/opc/certificates/${own}/public`, (route) =>
    route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message: '下载不可用' }) }))
  await page.goto('/config/devices')
  await page.locator('[data-test="device-certificates"]').click()
  const dialog = page.getByRole('dialog', { name: 'OPC UA 证书' })
  await dialog.getByRole('button', { name: '信任证书', exact: true }).click()
  await page.getByRole('dialog', { name: '核对服务器证书' }).getByRole('button', { name: '指纹已核对，信任' }).click()
  await expect(dialog.locator('[data-test="opc-cert-action-error"]')).toContainText('信任不可用')
  await expect(dialog.getByText('CN=Rejected')).toBeVisible()
  await dialog.getByRole('tab', { name: '客户端公钥' }).click()
  await dialog.getByRole('button', { name: '下载公钥', exact: true }).click()
  await expect(dialog.locator('[data-test="opc-cert-action-error"]')).toContainText('下载不可用')
  await expect(dialog.getByText('CN=Own')).toBeVisible()
})

test('layout and workstation save and delete failures stay visible', async ({ page, request }) => {
  const session = await login(request)
  const suffix = `${Date.now()}`
  const layout = await request.post('/api/hmi/layouts', {
    headers: session.headers,
    data: { name: `布局${suffix}`, document: { monitors: [{ name: '主屏', columns: 12, rows: 12, regions: [{ id: 'banner', kind: 'alarmBanner', row: 1, col: 1, rowSpan: 12, colSpan: 12 }] }] } }
  })
  expect(layout.ok(), await layout.text()).toBeTruthy()
  const layoutId = (await layout.json()).id
  const station = await request.post('/api/hmi/workstations', {
    headers: session.headers,
    data: { name: `OP-${suffix}`, description: null, layoutId: null, allowControl: true }
  })
  expect(station.ok(), await station.text()).toBeTruthy()
  const stationId = (await station.json()).id
  await sign(page, session)
  await page.route((url) => url.pathname === '/api/hmi/layouts', (route) => {
    if (route.request().method() !== 'POST') return route.continue()
    return route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message: '布局不可用' }) })
  })
  await page.route((url) => url.pathname === `/api/hmi/layouts/${layoutId}`, (route) => {
    if (route.request().method() !== 'DELETE') return route.continue()
    return route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message: '删除布局不可用' }) })
  })
  await page.route((url) => url.pathname === '/api/hmi/workstations', (route) => {
    if (route.request().method() !== 'POST') return route.continue()
    return route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message: '工作站不可用' }) })
  })
  await page.route((url) => url.pathname === `/api/hmi/workstations/${stationId}`, (route) => {
    if (route.request().method() !== 'DELETE') return route.continue()
    return route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message: '删除工作站不可用' }) })
  })
  await page.goto('/config/layouts')
  await page.locator('[data-test="layout-new"]').click()
  const layoutDialog = page.getByRole('dialog', { name: '新建布局' })
  await layoutDialog.locator('[data-test="layout-name"]').fill(`新布局${suffix}`)
  await layoutDialog.locator('[data-test="layout-save"]').click()
  await expect(layoutDialog.locator('[data-test="layout-save-error"]')).toContainText('布局不可用')
  await expect(layoutDialog).toBeVisible()
  await layoutDialog.getByRole('button', { name: '取消', exact: true }).click()
  const layoutRow = page.locator('[data-test="layouts"]').getByRole('row').filter({ hasText: `布局${suffix}` })
  await layoutRow.getByRole('button', { name: '删除', exact: true }).click()
  await page.getByRole('dialog', { name: '删除' }).getByRole('button', { name: '确定' }).click()
  await expect(page.locator('[data-test="layouts-action-error"]')).toContainText('删除布局不可用')
  await expect(layoutRow).toBeVisible()
  await page.locator('[data-test="station-new"]').click()
  const stationDialog = page.getByRole('dialog', { name: '登记工作站' })
  await stationDialog.locator('[data-test="station-name"]').fill(`NW-${suffix}`)
  await stationDialog.locator('[data-test="station-save"]').click()
  await expect(stationDialog.locator('[data-test="station-save-error"]')).toContainText('工作站不可用')
  await expect(stationDialog).toBeVisible()
  await stationDialog.getByRole('button', { name: '取消', exact: true }).click()
  const stationRow = page.locator('[data-test="stations"]').getByRole('row').filter({ hasText: `OP-${suffix}` })
  await stationRow.getByRole('button', { name: '删除', exact: true }).click()
  await page.getByRole('dialog', { name: '删除' }).getByRole('button', { name: '确定' }).click()
  await expect(page.locator('[data-test="layouts-action-error"]')).toContainText('删除工作站不可用')
  await expect(stationRow).toBeVisible()
})
