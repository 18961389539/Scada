import { test, expect, type APIRequestContext } from '@playwright/test'

async function login(request: APIRequestContext, userName: string, password: string) {
  const response = await request.post('/api/auth/login', { data: { userName, password } })
  expect(response.ok(), await response.text()).toBeTruthy()
  const body = await response.json()
  return { ...body, headers: { Authorization: `Bearer ${body.token}` } }
}

test('calculated tag definition and annunciator outputs follow configurable areas', async ({ page, request }) => {
  test.setTimeout(60000)
  const admin = (await login(request, 'admin', 'admin123')).headers
  const suffix = Date.now()
  const post = async (url: string, data: unknown) => {
    const response = await request.post(url, { headers: admin, data })
    expect(response.ok(), await response.text()).toBeTruthy()
    return response.json()
  }
  const areaA = await post('/api/areas', { name: `GATE-A-${suffix}` })
  const areaB = await post('/api/areas', { name: `GATE-B-${suffix}` })
  const role = await post('/api/roles', { name: `GATE-配置-${suffix}`, permissions: ['View', 'Configure'] })
  const view = await post('/api/roles', { name: `GATE-查看-${suffix}`, permissions: ['View'] })
  const user = await post('/api/users', { userName: `gate-${suffix}`, password: 'secret123', displayName: '区域配置员', role: 'None', enabled: true })
  const assigned = await request.put(`/api/users/${user.id}/assignments`, {
    headers: admin,
    data: { assignments: [{ roleId: role.id, areaId: areaA.id }, { roleId: view.id, areaId: areaB.id }] }
  })
  expect(assigned.ok(), await assigned.text()).toBeTruthy()
  const deviceBody = (name: string, areaId: number, port: number) => ({
    name, protocol: 'ModbusTcp', host: '127.0.0.1', port, stationNumber: 1,
    byteOrder: 'ABCD', pollIntervalMs: 1000, timeoutMs: 1000, enabled: false, areaId
  })
  const tagBody = (deviceId: number, name: string) => ({
    deviceId, name, area: 'HoldingRegister', address: 0, dataType: 'UInt16', scale: 1, offset: 0,
    writable: true, archiveMode: 'None', archivePeriodSec: 60, deadband: 0
  })
  const deviceA = await post('/api/devices', deviceBody(`GATE-DA-${suffix}`, areaA.id, 21000 + (suffix % 1000)))
  const deviceB = await post('/api/devices', deviceBody(`GATE-DB-${suffix}`, areaB.id, 22000 + (suffix % 1000)))
  const tagA = await post('/api/tags', tagBody(deviceA.id, `GATE-WA-${suffix}`))
  const tagB = await post('/api/tags', tagBody(deviceB.id, `GATE-WB-${suffix}`))
  const calcA = `GATE-CA-${suffix}`
  const calcB = `GATE-CB-${suffix}`
  await post('/api/calculated-tags', { name: calcA, expression: '1', intervalMs: 1000, areaId: areaA.id })
  await post('/api/calculated-tags', { name: calcB, expression: '1', intervalMs: 1000, areaId: areaB.id })

  await page.goto('/login')
  await page.getByPlaceholder(/用户名/).fill(user.userName)
  await page.getByPlaceholder(/密码/).fill('secret123')
  await page.keyboard.press('Enter')
  await expect(page).toHaveURL(/overview/)

  await page.goto('/config/tags')
  await page.getByPlaceholder('搜索测点名称/描述').fill(calcB)
  await page.getByRole('button', { name: '查询', exact: true }).click()
  const rowB = page.locator('tr', { hasText: calcB })
  await expect(rowB).toBeVisible()
  await expect(rowB.getByRole('button', { name: '计算定义' })).toHaveCount(0)
  await page.getByPlaceholder('搜索测点名称/描述').fill(calcA)
  await page.getByRole('button', { name: '查询', exact: true }).click()
  const rowA = page.locator('tr', { hasText: calcA })
  await expect(rowA.getByRole('button', { name: '计算定义' })).toBeVisible()

  await page.goto('/config/annunciators')
  const dialog = page.getByRole('dialog', { name: '新建声光面板' })
  await page.locator('[data-test="ann-add"]').click()
  await page.getByRole('button', { name: '添加输出' }).click()
  const output = dialog.locator('.out-row').locator('.el-select').nth(1)
  await expect(output).toContainText(tagA.name)
  await output.click()
  await expect(page.getByRole('option', { name: tagA.name })).toBeVisible()
  await expect(page.getByRole('option', { name: tagB.name })).toHaveCount(0)
})

test('report and calc-job create use a configurable area', async ({ page, request }) => {
  test.setTimeout(60000)
  const admin = (await login(request, 'admin', 'admin123')).headers
  const suffix = Date.now()
  const post = async (url: string, data: unknown) => {
    const response = await request.post(url, { headers: admin, data })
    expect(response.ok(), await response.text()).toBeTruthy()
    return response.json()
  }
  const area = await post('/api/areas', { name: `GATE-R-${suffix}` })
  const role = await post('/api/roles', { name: `GATE-R-${suffix}`, permissions: ['View', 'Configure'] })
  const user = await post('/api/users', { userName: `gate-r-${suffix}`, password: 'secret123', displayName: '报表配置员', role: 'None', enabled: true })
  const assigned = await request.put(`/api/users/${user.id}/assignments`, { headers: admin, data: { assignments: [{ roleId: role.id, areaId: area.id }] } })
  expect(assigned.ok(), await assigned.text()).toBeTruthy()

  await page.route('**/api/areas', route => route.request().method() === 'GET' ? route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }) : route.continue())
  await page.goto('/login')
  await page.getByPlaceholder(/用户名/).fill(user.userName)
  await page.getByPlaceholder(/密码/).fill('secret123')
  await page.keyboard.press('Enter')
  await expect(page).toHaveURL(/overview/)
  await page.goto('/report-center')
  await expect(page.locator('.el-loading-mask')).toHaveCount(0)
  await expect(page.locator('[data-test="report-template-new"]')).toHaveCount(0)
  await page.goto('/calc-jobs')
  await expect(page.locator('.el-loading-mask')).toHaveCount(0)
  await expect(page.locator('[data-test="calc-job-new"]')).toHaveCount(0)

  await page.unroute('**/api/areas')
  await page.goto('/report-center')
  await page.locator('[data-test="report-template-new"]').click()
  const reportDialog = page.getByRole('dialog', { name: '新建报表模板' })
  await expect(reportDialog.locator('[data-test="report-template-area"]')).toContainText(area.name)
  await expect(reportDialog.locator('[data-test="report-template-area"]')).not.toContainText('默认区域')
  await reportDialog.getByRole('button', { name: '取消' }).click()

  await page.goto('/calc-jobs')
  await page.locator('[data-test="calc-job-new"]').click()
  const jobDialog = page.getByRole('dialog', { name: '新建计算任务' })
  await expect(jobDialog.locator('[data-test="calc-job-area"]')).toContainText(area.name)
  await expect(jobDialog.locator('[data-test="calc-job-area"]')).not.toContainText('默认区域')
})
