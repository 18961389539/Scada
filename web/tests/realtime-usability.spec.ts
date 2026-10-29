import { test, expect, type APIRequestContext, type Page } from '@playwright/test'
import type { AlarmEvent } from '../src/types'

test.afterEach(async ({ page }) => {
  // Snapshot mocks sample the browser clock; finish them before the page closes.
  await page.unrouteAll({ behavior: 'wait' })
})

async function login(request: APIRequestContext) {
  const r = await request.post('/api/auth/login', { data: { userName: 'admin', password: 'admin123' } })
  expect(r.ok(), await r.text()).toBeTruthy()
  return (await r.json()) as { token: string }
}

async function realtimeFixture(page: Page, request: APIRequestContext) {
  const session = await login(request)
  await page.addInitScript(token => {
    localStorage.setItem('scada_token', token)
    localStorage.removeItem('scada_user')
    localStorage.setItem('scada_sound', 'off')
  }, session.token)
  const device = { id: 900001, name: '监视测试设备', enabled: true, pollIntervalMs: 1000, areaId: 1 }
  const tags = [
    { id: 900001, name: '运行信号', dataType: 'Bool', unit: '', writable: true },
    { id: 900002, name: '累计件数', dataType: 'UInt32', unit: '件', writable: false },
    { id: 900003, name: '出口温度', dataType: 'Float', unit: '°C', writable: false },
    { id: 900004, name: '故障压力', dataType: 'Float', unit: 'MPa', writable: false }
  ].map(tag => ({ ...tag, deviceId: device.id, kind: 'Physical', minValue: 0, maxValue: 200, description: tag.name }))
  const state = {
    tags,
    failConfig: false, failSnapshot: false, failAlarms: false, alarmSuppressed: false, snapshotRequests: 0, refreshSamples: true,
    values: tags.map((tag, index) => ({
      tagId: tag.id, value: [1, 1500123, 95, 30][index],
      quality: index === 3 ? 'CommFailure' : 'Good', ts: Date.now(),
      error: index === 3 ? '读取超时' : null
    }))
  }
  await page.route('**/api/devices', route => route.fulfill({ json: [device] }))
  const alarms = (): AlarmEvent[] => [
    { id: 990001, tagId: 900003, deviceId: device.id, ruleId: null, level: 'Critical', value: 95, startTs: Date.now(), endTs: null, ackTs: null, ackUser: null, message: '出口温度超限', state: 'Unack', suppressed: state.alarmSuppressed }
  ]
  await page.route(url => url.pathname === '/api/alarms', route => route.fulfill({ status: state.failAlarms ? 500 : 200, json: state.failAlarms ? { message: '关联报警暂不可用' } : alarms() }))
  await page.route(url => url.pathname === '/api/alarms/page', route => {
    if (state.failAlarms) return route.fulfill({ status: 500, json: { message: '关联报警暂不可用' } })
    const query = new URL(route.request().url()).searchParams
    const items = alarms().filter(alarm => {
      const active = !alarm.endTs && !alarm.shelved && !alarm.suppressed
      const filter = query.get('state')
      return (!query.get('tagId') || alarm.tagId === Number(query.get('tagId')))
        && (!query.get('level') || alarm.level === query.get('level'))
        && alarm.message.includes(query.get('keyword') ?? '')
        && (query.get('live') !== 'true' || (filter === 'suppressed' ? alarm.suppressed
          : filter === 'shelved' ? alarm.shelved : filter === 'rtnUnack' ? !!alarm.endTs && !alarm.ackTs
          : filter === 'acked' ? active && !!alarm.ackTs : filter === 'unack' ? active && !alarm.ackTs : active))
    })
    const offset = Number(query.get('offset') ?? 0), limit = Number(query.get('limit') ?? 100)
    return route.fulfill({ json: { items: items.slice(offset, offset + limit), total: items.length, offset, limit, capped: false } })
  })
  await page.route(url => url.pathname === '/api/tags', route => route.fulfill({
    status: state.failConfig ? 500 : 200,
    json: state.failConfig ? { message: '配置暂不可用' } : tags
  }))
  await page.route(url => url.pathname === '/api/realtime', async route => {
    state.snapshotRequests++
    const sampleTime = state.refreshSamples ? await page.evaluate(() => Date.now()) : null
    return route.fulfill({ status: state.failSnapshot ? 500 : 200, json: state.failSnapshot ? { message: '快照暂不可用' } : state.values.map(value => ({ ...value, ts: sampleTime ?? value.ts })) })
  })
  return state
}

test('monitoring review keeps device selection and cleared drilldowns consistent with the URL', async ({ page, request }) => {
  await realtimeFixture(page, request)
  await page.goto('/realtime')
  await page.locator('[data-test="device-filter"]').click()
  await page.getByRole('option', { name: '监视测试设备', exact: true }).click()
  await expect(page).toHaveURL(/deviceId=900001/)
  await page.reload()
  await expect(page.locator('[data-test="device-filter"]')).toContainText('监视测试设备')
  await page.locator('[data-test="realtime-reset"]').click()
  await expect(page).not.toHaveURL(/deviceId=/)
  await page.reload()
  await expect(page.locator('[data-test="device-filter"]')).not.toContainText('监视测试设备')
})

test('monitoring review validates numeric writes and locks the submitted context while pending', async ({ page, request }) => {
  const state = await realtimeFixture(page, request)
  state.tags[1]!.writable = true
  let checks = 0, submissions = 0
  await page.route(url => url.pathname === '/api/control/check', route => {
    checks++
    return route.fulfill({ json: { tagId: 900002, tagName: '累计件数', current: 42, mode: 'Remote', lockouts: [], refusals: [], dualApproval: false } })
  })
  let releaseWrite: (() => void) | undefined
  const pendingWrite = new Promise<void>(resolve => { releaseWrite = resolve })
  await page.route(url => url.pathname === '/api/commands', async route => {
    submissions++
    expect(route.request().postDataJSON()).toMatchObject({ tagId: 900002, value: 42, confirmed: true })
    await pendingWrite
    await route.fulfill({ json: { success: true, readBack: 42 } })
  })
  await page.goto('/realtime')
  await page.locator('[data-test="realtime-table"] .el-table__row').filter({ hasText: '累计件数' }).getByRole('button', { name: '写值', exact: true }).click()
  const dialog = page.locator('[data-test="realtime-write-dialog"]')
  const input = dialog.locator('[data-test="write-number"] input')
  const send = dialog.getByRole('button', { name: '下发', exact: true })
  await input.fill('')
  await send.click()
  await expect(dialog.locator('[data-test="realtime-write-error"]')).toBeVisible()
  expect(checks).toBe(0)
  await input.fill('1.5')
  await send.click()
  await expect(dialog.locator('[data-test="realtime-write-error"]')).toContainText('整数测点不接受小数')
  expect(checks).toBe(0)
  await input.fill('42')
  await send.click()
  await expect(page.locator('[data-test="control-confirm-ok"]')).toBeEnabled()
  expect(submissions).toBe(0)
  await page.locator('[data-test="control-confirm-ok"]').click()
  await expect.poll(() => submissions).toBe(1)
  await expect(input).toBeDisabled()
  await expect(dialog.getByRole('button', { name: '取消', exact: true })).toBeDisabled()
  await page.keyboard.press('Escape')
  await expect(dialog).toBeVisible()
  releaseWrite?.()
  await expect(dialog).toBeHidden()
})

test('realtime monitoring keeps scoped abnormal totals when showing only good quality', async ({ page, request }) => {
  await realtimeFixture(page, request)
  await page.goto('/realtime')
  const count = page.locator('[data-test="abnormal-count"]')
  await expect(count).toContainText('1')
  await page.locator('[data-test="quality-filter"]').click()
  await page.getByRole('option', { name: '仅正常', exact: true }).click()
  await expect(count).toContainText('1')
  await expect(page.locator('.el-table__body .el-table__row')).toHaveCount(3)
  await page.getByPlaceholder('搜索测点').fill('出口温度')
  await expect(count).toHaveCount(0)
  await page.locator('[data-test="realtime-reset"]').click()
  await expect(page.locator('[data-test="realtime-table"] .el-table__row')).toHaveCount(4)
})

test('realtime monitoring retries configuration and formats booleans and exact integer counts', async ({ page, request }) => {
  const state = await realtimeFixture(page, request)
  state.failConfig = true
  await page.goto('/realtime')
  await expect(page.locator('[data-test="realtime-load-error"]')).toContainText('配置暂不可用')
  state.failConfig = false
  await page.locator('[data-test="snapshot-refresh"]').click()
  await expect(page.locator('[data-test="realtime-load-error"]')).toHaveCount(0)
  const table = page.locator('[data-test="realtime-table"]')
  await expect(table).toContainText('置位 (1)')
  await expect(table).toContainText('1500123')
  await expect(table).toContainText('良好')
  await expect(table).toContainText('最近采样')
  await page.locator('[data-test="freeze-toggle"]').click()
  await expect(page.locator('[data-test="realtime-write"]')).toBeDisabled()
  await page.locator('[data-test="freeze-toggle"]').click()
  await expect(page.locator('[data-test="realtime-write"]')).toBeEnabled()
  await page.locator('[data-test="realtime-write"]').click()
  await expect(page.locator('[data-test="write-boolean"]')).toBeVisible()
  await page.getByRole('dialog').getByRole('button', { name: '取消', exact: true }).click()
})

test('realtime monitoring verifies unchanged samples and exposes failed verification without discarding values', async ({ page, request }) => {
  await page.clock.install()
  const state = await realtimeFixture(page, request)
  await page.goto('/realtime')
  await expect(page.locator('[data-test="realtime-table"]')).toContainText('1500123')
  await page.clock.fastForward(40_000)
  await expect.poll(() => state.snapshotRequests).toBeGreaterThan(1)
  await expect(page.locator('[data-test="realtime-stale"]')).toHaveCount(0)
  state.failSnapshot = true
  await page.locator('[data-test="snapshot-refresh"]').click()
  await expect(page.locator('[data-test="realtime-snapshot-error"]')).toContainText('快照暂不可用')
  await expect(page.locator('[data-test="realtime-stale"]')).toBeVisible()
  await expect(page.locator('[data-test="realtime-table"]')).toContainText('1500123')
  state.failSnapshot = false
  await page.locator('[data-test="snapshot-refresh"]').click()
  await expect(page.locator('[data-test="realtime-stale"]')).toHaveCount(0)
  state.refreshSamples = false
  await page.clock.fastForward(40_000)
  await page.locator('[data-test="snapshot-refresh"]').click()
  await expect(page.locator('[data-test="realtime-stale"]')).toBeVisible()
})

test('realtime monitoring separates alarm state from quality and shows diagnostic errors', async ({ page, request }) => {
  await realtimeFixture(page, request)
  await page.goto('/realtime')
  const temperature = page.locator('[data-test="realtime-table"] .el-table__row').filter({ hasText: '出口温度' })
  await expect(temperature).toContainText('良好')
  await expect(temperature).toContainText('活动报警 1')
  await expect(page.locator('[data-test="realtime-point-error"]')).toHaveText('读取超时')
  await page.locator('[data-test="realtime-sort"]').click()
  await page.getByRole('option', { name: '异常优先', exact: true }).click()
  await expect(page.locator('[data-test="realtime-table"] .el-table__row').first()).toContainText('出口温度')
  await temperature.getByRole('button', { name: '活动报警 1', exact: true }).click()
  await expect(page).toHaveURL(/alarms\?tagId=900003/)
  await expect(page.locator('.el-table__body')).toContainText('出口温度超限')
  await expect(page.locator('[data-test="alarm-tag-filter"]')).toContainText('900003')
  await page.locator('[data-test="alarm-reset-filters"]').click()
  await expect(page).not.toHaveURL(/tagId=/)
})

test('realtime monitoring exposes alarm failures and locates suppressed alarms correctly', async ({ page, request }) => {
  const state = await realtimeFixture(page, request)
  state.failAlarms = true
  await page.goto('/realtime')
  await expect(page.locator('[data-test="realtime-alarm-error"]')).toContainText('关联报警暂不可用')
  await expect(page.locator('[data-test="realtime-table"]')).not.toContainText('未见活动报警')
  state.failAlarms = false
  state.alarmSuppressed = true
  await page.locator('[data-test="snapshot-refresh"]').click()
  const alarm = page.locator('[data-test="realtime-alarm"]')
  await expect(alarm).toContainText('含抑制/暂挂')
  await alarm.click()
  await expect(page.locator('[data-test="filter-suppressed"]')).toHaveAttribute('aria-pressed', 'true')
  await expect(page.locator('.el-table__body')).toContainText('出口温度超限')
})

test('realtime monitoring offers a compact mobile list and usable filters without horizontal overflow', async ({ page, request }, testInfo) => {
  await realtimeFixture(page, request)
  for (const width of [1600, 390, 320]) {
    await page.setViewportSize({ width, height: 900 })
    await page.goto('/realtime')
    const table = page.locator('[data-test="realtime-table"]')
    const mobileList = page.locator('[data-test="realtime-mobile-list"]')
    if (width < 768) {
      await expect(table).toBeHidden()
      await expect(mobileList).toBeVisible()
      await expect(mobileList).toContainText('1500123')
      await expect(mobileList).toContainText('读取超时')
      await expect(mobileList).toContainText('活动报警 1')
      expect(await mobileList.evaluate(element => element.scrollWidth <= element.clientWidth)).toBe(true)
    } else await expect(table).toBeVisible()
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true)
    await page.screenshot({ path: testInfo.outputPath(`realtime-${width}.png`), fullPage: true })
  }
})

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
