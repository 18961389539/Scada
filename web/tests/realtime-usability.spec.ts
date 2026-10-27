import { test, expect, type APIRequestContext } from '@playwright/test'

async function login(request: APIRequestContext) {
  const r = await request.post('/api/auth/login', { data: { userName: 'admin', password: 'admin123' } })
  expect(r.ok(), await r.text()).toBeTruthy()
  return (await r.json()) as { token: string }
}

// 实时页易用性：质量筛选、异常计数、异常行高亮、暂停刷新、写值对话框的量程与当前值提示。
test('realtime usability: quality filter, abnormal count, freeze toggle and write hints', async ({ page, request }) => {
  test.setTimeout(120000)
  const session = await login(request)
  const h = { Authorization: `Bearer ${session.token}` }
  const suffix = `${Date.now()}`
  const post = async (url: string, data: unknown) => {
    const r = await request.post(url, { headers: h, data })
    expect(r.ok(), await r.text()).toBeTruthy()
    return r.json()
  }

  // 设备保持停用：测点不会采集到值，质量统一为 Bad，正好用来验证「异常」筛选
  const device = await post('/api/devices', {
    name: `UX-RT-${suffix}`, protocol: 'ModbusTcp', host: '127.0.0.1', port: 1502,
    stationNumber: 1, byteOrder: 'ABCD', pollIntervalMs: 1000, timeoutMs: 3000, enabled: false, areaId: 1
  })
  const mkTag = (name: string, address: number) => post('/api/tags', {
    deviceId: device.id, name, area: 'HoldingRegister', address, dataType: 'Float',
    scale: 1, offset: 0, writable: true, archiveMode: 'OnChange', deadband: 0, archivePeriodSec: 600,
    minValue: 0, maxValue: 100, unit: '℃'
  })
  await mkTag(`UX-RT-A-${suffix}`, 20)
  await mkTag(`UX-RT-B-${suffix}`, 22)

  // 真实登录：注入 localStorage 的 user 不带权限字段，写值按钮会不渲染
  await page.goto('/login')
  await page.getByPlaceholder(/用户名/).fill('admin')
  await page.getByPlaceholder(/密码/).fill('admin123')
  await page.keyboard.press('Enter')
  await expect(page).toHaveURL(/overview/)
  await page.goto('/realtime')

  // 先按设备收敛，避免其他用例留下的测点干扰计数
  await page.locator('[data-test="device-filter"]').click()
  await page.getByRole('option', { name: `UX-RT-${suffix}` }).click()
  const rowA = page.getByRole('row').filter({ hasText: `UX-RT-A-${suffix}` })
  const rowB = page.getByRole('row').filter({ hasText: `UX-RT-B-${suffix}` })
  await expect(rowA).toBeVisible()
  await expect(rowB).toBeVisible()

  // 两个测点都无值 → 异常计数为 2
  await expect(page.locator('[data-test="abnormal-count"]')).toContainText('2')

  // 仅正常：空表
  await page.locator('[data-test="quality-filter"]').click()
  await page.getByRole('option', { name: '仅正常' }).click()
  await expect(page.getByRole('row').filter({ hasText: `UX-RT-A-${suffix}` })).toHaveCount(0)

  // 仅异常：两条都在
  await page.locator('[data-test="quality-filter"]').click()
  await page.getByRole('option', { name: '仅异常' }).click()
  await expect(rowA).toBeVisible()
  await expect(rowB).toBeVisible()

  // 暂停/恢复：按钮文案切换
  const freeze = page.locator('[data-test="freeze-toggle"]')
  await expect(freeze).toContainText('暂停刷新')
  await freeze.click()
  await expect(freeze).toContainText('继续刷新')
  await freeze.click()
  await expect(freeze).toContainText('暂停刷新')

  // 写值对话框给出量程与当前值参考
  await rowA.getByRole('button', { name: '写值' }).click()
  const dialog = page.getByRole('dialog').filter({ hasText: `UX-RT-A-${suffix}` })
  await expect(dialog.locator('[data-test="write-hints"]')).toContainText('量程 0 ~ 100')
  await expect(dialog.locator('[data-test="write-hints"]')).toContainText('当前 --')
  await dialog.getByRole('button', { name: '取消' }).click()
})

test('realtime snapshot failure stays on the page and is not shown as bad quality', async ({ page, request }) => {
  test.setTimeout(60000)
  const session = await login(request)
  const h = { Authorization: `Bearer ${session.token}` }
  const suffix = `${Date.now()}`
  const device = await (await request.post('/api/devices', { headers: h, data: {
    name: `UX-SNAP-${suffix}`, protocol: 'ModbusTcp', host: '127.0.0.1', port: 1502,
    stationNumber: 1, byteOrder: 'ABCD', pollIntervalMs: 1000, timeoutMs: 3000, enabled: false, areaId: 1
  } })).json()
  expect(device.id).toBeTruthy()
  const tag = await (await request.post('/api/tags', { headers: h, data: {
    deviceId: device.id, name: `UX-SNAP-T-${suffix}`, area: 'HoldingRegister', address: 40, dataType: 'Float',
    scale: 1, offset: 0, writable: false, archiveMode: 'OnChange', deadband: 0, archivePeriodSec: 600
  } })).json()
  expect(tag.id).toBeTruthy()
  await page.route((url) => url.pathname === '/api/realtime', (route) => {
    if (route.request().method() !== 'GET') return route.continue()
    return route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message: '实时值不可用' }) })
  })
  await page.goto('/login')
  await page.getByPlaceholder(/用户名/).fill('admin')
  await page.getByPlaceholder(/密码/).fill('admin123')
  await page.keyboard.press('Enter')
  await expect(page).toHaveURL(/overview/)
  await page.goto('/realtime')
  const row = page.getByRole('row').filter({ hasText: `UX-SNAP-T-${suffix}` })
  await expect(page.locator('[data-test="realtime-snapshot-error"]')).toContainText('实时值不可用')
  await expect(row.locator('[data-test="quality-unread"]')).toHaveText('未读取')
  await expect(row.getByText('故障', { exact: true })).toHaveCount(0)
  await expect(page.locator('[data-test="abnormal-count"]')).toHaveCount(0)
})

test('control check without a server message uses the localized failure text', async ({ page, request }) => {
  test.setTimeout(60000)
  const session = await login(request)
  const h = { Authorization: `Bearer ${session.token}` }
  const suffix = `${Date.now()}`
  const device = await (await request.post('/api/devices', { headers: h, data: {
    name: `UX-CHK-${suffix}`, protocol: 'ModbusTcp', host: '127.0.0.1', port: 1502,
    stationNumber: 1, byteOrder: 'ABCD', pollIntervalMs: 1000, timeoutMs: 3000, enabled: false, areaId: 1
  } })).json()
  const tag = await (await request.post('/api/tags', { headers: h, data: {
    deviceId: device.id, name: `UX-CHK-T-${suffix}`, area: 'HoldingRegister', address: 60, dataType: 'Float',
    scale: 1, offset: 0, writable: true, archiveMode: 'OnChange', deadband: 0, archivePeriodSec: 600
  } })).json()
  expect(tag.id).toBeTruthy()
  await page.route((url) => url.pathname === '/api/control/check', (route) => {
    if (route.request().method() !== 'POST') return route.continue()
    return route.fulfill({ status: 403, body: '' })
  })
  await page.goto('/login')
  await page.getByPlaceholder(/用户名/).fill('admin')
  await page.getByPlaceholder(/密码/).fill('admin123')
  await page.keyboard.press('Enter')
  await expect(page).toHaveURL(/overview/)
  await page.goto('/realtime')
  const row = page.getByRole('row').filter({ hasText: `UX-CHK-T-${suffix}` })
  await row.getByRole('button', { name: '写值' }).click()
  const dialog = page.getByRole('dialog').filter({ hasText: `UX-CHK-T-${suffix}` })
  await dialog.getByRole('button', { name: '下发' }).click()
  const confirm = page.locator('[data-test="control-confirm"]')
  await expect(confirm.locator('[data-test="control-check-error"]')).toContainText('控制检查失败')
  await expect(page.getByText('Request failed with status code 403')).toHaveCount(0)
  await expect(confirm.locator('[data-test="control-confirm-ok"]')).toBeDisabled()
  await expect(confirm).toBeVisible()
})

test('realtime shows a load failure instead of an empty tag list', async ({ page }) => {
  test.setTimeout(60000)
  await page.route('**/api/tags*', async route => {
    const url = new URL(route.request().url())
    if (route.request().method() === 'GET' && url.pathname === '/api/tags') {
      await route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message: '实时测点不可用' }) })
      return
    }
    await route.continue()
  })
  await page.goto('/login')
  await page.getByPlaceholder(/用户名/).fill('admin')
  await page.getByPlaceholder(/密码/).fill('admin123')
  await page.keyboard.press('Enter')
  await expect(page).toHaveURL(/overview/)
  await page.goto('/realtime')
  await expect(page.locator('[data-test="realtime-load-error"]')).toContainText('实时测点不可用')
  await expect(page.getByText('暂无测点')).toHaveCount(0)
})

test('large tag catalogs render bounded pages and keep filtering across the full catalog', async ({ page, request }) => {
  const session = await login(request)
  await page.addInitScript(token => {
    localStorage.setItem('scada_token', token)
    localStorage.removeItem('scada_user')
  }, session.token)
  const device = { id: 900001, name: 'Performance device', protocol: 'ModbusTcp', areaId: 1, enabled: false }
  const tags = Array.from({ length: 2000 }, (_, index) => ({
    id: 900001 + index, deviceId: device.id, name: `Perf-${String(index + 1).padStart(5, '0')}`,
    kind: 'Physical', description: 'Performance fixture', area: 'HoldingRegister', address: index,
    dataType: 'Float', scale: 1, offset: 0, unit: 'V', writable: false,
    archiveMode: 'OnChange', deadband: 0, archivePeriodSec: 600
  }))
  await page.route('**/api/devices', route => route.fulfill({ json: [device] }))
  await page.route('**/api/tags*', route => {
    const url = new URL(route.request().url())
    if (url.pathname !== '/api/tags') return route.continue()
    const search = url.searchParams.get('search') ?? ''
    return route.fulfill({ json: tags.filter(tag => tag.name.includes(search)) })
  })
  await page.route('**/api/realtime*', route => route.fulfill({ json: [] }))
  for (const path of ['/realtime', '/config/tags']) {
    await page.goto(path)
    const rows = page.locator('.el-table__body-wrapper .el-table__row')
    await expect(rows).toHaveCount(100)
    const pagination = page.locator('[data-test="tag-pagination"]')
    await expect(pagination).toContainText('2000')
    await pagination.locator('.btn-next').click()
    await expect(rows.first()).toContainText('Perf-00101')
    const search = path === '/realtime'
      ? page.locator('input[data-test="realtime-search"], [data-test="realtime-search"] input')
      : page.getByPlaceholder('搜索测点名称/描述')
    await search.fill('Perf-02000')
    if (path === '/config/tags') await search.press('Enter')
    await expect(rows).toHaveCount(1)
    await expect(rows.first()).toContainText('Perf-02000')
    await expect(pagination).toBeHidden()
    await search.fill('')
    if (path === '/config/tags') await search.press('Enter')
    await expect(rows).toHaveCount(100)
    await expect(rows.first()).toContainText('Perf-00001')
    await expect(pagination.locator('.btn-prev')).toBeDisabled()
  }
})
