import { test, expect, type Page, type APIRequestContext } from '@playwright/test'

// 总览易用性：统计卡片从纯展示变为可点击跳转，且支持键盘操作；无权页面保持纯展示。
test('overview usability: stat cards navigate to their pages', async ({ page }) => {
  test.setTimeout(90000)
  await page.goto('/login')
  await page.getByPlaceholder(/用户名/).fill('admin')
  await page.getByPlaceholder(/密码/).fill('admin123')
  await page.keyboard.press('Enter')
  await expect(page).toHaveURL(/overview/)

  // admin 可见的 4 张卡片（设备/报警/测点/存储）可点击，运行时长卡片保持纯展示
  await expect(page.locator('.stat-card.clickable')).toHaveCount(4)
  await expect(page.locator('[data-test="stat-devices"]')).toHaveClass(/clickable/)

  // 设备在线 → 实时监视
  await page.locator('[data-test="stat-devices"]').click()
  await expect(page).toHaveURL(/realtime/)

  // 未确认报警 → 报警页
  await page.goto('/overview')
  await page.locator('[data-test="stat-alarms"]').click()
  await expect(page).toHaveURL(/alarms/)

  // 历史存储 → 系统管理
  await page.goto('/overview')
  await page.locator('[data-test="overview-maintenance"] > summary').click()
  await page.locator('[data-test="stat-storage"]').click()
  await expect(page).toHaveURL(/system/)

  // 键盘可达：聚焦卡片后回车同样跳转
  await page.goto('/overview')
  await page.locator('[data-test="overview-maintenance"] > summary').click()
  await expect(page.locator('[data-test="stat-tags"]')).toHaveAttribute('tabindex', '0')
  await page.locator('[data-test="stat-tags"]').focus()
  await page.keyboard.press('Enter')
  await expect(page).toHaveURL(/realtime/)
})

// 数据口径回归：未确认报警卡片原先用「列表前 10 条」自己数，超过 10 条就少报（最多显示 10）。
// 现在必须用服务端 /alarms/summary 的全量统计。
test('overview usability: unacknowledged count comes from server summary, not the loaded slice', async ({ page }) => {
  test.setTimeout(60000)
  await page.route('**/api/alarms/summary', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ unack: 42, acked: 3, rtnUnack: 1, shelved: 0, suppressed: 0, chattering: 0, shelvedRules: 0, outOfServiceRules: 0 })
    })
  )
  // 列表只给空数组：模拟「页面拿到的活动报警列表远少于全量统计」
  await page.route(
    (url) => url.pathname === '/api/alarms' && url.searchParams.get('activeOnly') === 'true',
    (route) => route.fulfill({ status: 200, contentType: 'application/json', body: '[]' })
  )

  await page.goto('/login')
  await page.getByPlaceholder(/用户名/).fill('admin')
  await page.getByPlaceholder(/密码/).fill('admin123')
  await page.keyboard.press('Enter')
  await expect(page).toHaveURL(/overview/)

  const card = page.locator('[data-test="stat-alarms"]')
  await expect(card.locator('.value')).toHaveText('42', { timeout: 15000 })
  await expect(card).toContainText('当前活动报警 45 条')
})

test('overview lists a severe alarm ahead of a newer info alarm', async ({ page }) => {
  test.setTimeout(60000)
  let priorityFirst = false
  await page.route('**/api/alarms/summary', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ unack: 2, acked: 0, rtnUnack: 0, shelved: 0, suppressed: 0, chattering: 0, shelvedRules: 0, outOfServiceRules: 0 })
    })
  )
  await page.route(
    (url) => url.pathname === '/api/alarms' && url.searchParams.get('activeOnly') === 'true',
    (route) => {
      priorityFirst = new URL(route.request().url()).searchParams.get('priorityFirst') === 'true'
      return route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify([
          { id: 2, tagId: null, deviceId: null, ruleId: null, level: 'Info', value: 1, startTs: 2_000, endTs: null, ackUser: null, ackTs: null, message: '新的提示' },
          { id: 1, tagId: null, deviceId: null, ruleId: null, level: 'Critical', value: 9, startTs: 1_000, endTs: null, ackUser: null, ackTs: null, message: '老的严重' },
          { id: 3, level: 'Critical', value: 9, startTs: 3_000, endTs: null, ackTs: null, suppressed: true, message: '被抑制的报警' },
          { id: 4, level: 'Critical', value: 9, startTs: 4_000, endTs: null, ackTs: null, shelved: true, message: '已暂挂的报警' }
        ])
      })
    }
  )
  await page.goto('/login')
  await page.getByPlaceholder(/用户名/).fill('admin')
  await page.getByPlaceholder(/密码/).fill('admin123')
  await page.keyboard.press('Enter')
  await expect(page).toHaveURL(/overview/)
  const rows = page.locator('[data-test="overview-alarms"] .el-table__body tr')
  await expect(rows).toHaveCount(2)
  await expect(rows.nth(0)).toContainText('老的严重')
  await expect(rows.nth(1)).toContainText('新的提示')
  expect(priorityFirst).toBeTruthy()
})

test('overview shows the refresh error instead of an empty plant', async ({ page }) => {
  test.setTimeout(60000)
  await page.route('**/api/health', route => {
    const url = new URL(route.request().url())
    if (url.pathname !== '/api/health') return route.continue()
    return route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message: '总览数据不可用' }) })
  })
  await page.goto('/login')
  await page.getByPlaceholder(/用户名/).fill('admin')
  await page.getByPlaceholder(/密码/).fill('admin123')
  await page.keyboard.press('Enter')
  await expect(page).toHaveURL(/overview/)
  await expect(page.locator('[data-test="overview-load-error"]')).toContainText('总览数据不可用')
  await expect(page.getByText('当前没有活动报警')).toHaveCount(0)
  await expect(page.getByText('暂无设备')).toHaveCount(0)
})

test('a failed alarm summary is not shown as zero or as a stale plant total', async ({ page, request }) => {
  test.setTimeout(60000)
  await page.clock.install()
  const response = await request.post('/api/auth/login', { data: { userName: 'admin', password: 'admin123' } })
  expect(response.ok()).toBeTruthy()
  const body = await response.json()
  await page.addInitScript((data) => {
    localStorage.setItem('scada_token', data.token)
    localStorage.setItem('scada_user', JSON.stringify(data.user))
  }, { token: body.token, user: body.user })

  const many = Array.from({ length: 12 }, (_, index) => ({
    id: index + 1, tagId: null, deviceId: null, ruleId: null, level: 'Warning', value: 1,
    startTs: 10_000 + index, endTs: null, ackUser: null, ackTs: null, message: `报警${index + 1}`, areas: []
  }))
  let summaryMode: 'full' | 'fail' = 'full'
  let list = many
  await page.route('**/api/alarms/summary', (route) => {
    if (summaryMode === 'fail') {
      return route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message: '报警统计不可用' }) })
    }
    return route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ unack: 4, acked: 1, rtnUnack: 0, shelved: 0, suppressed: 0, chattering: 0, shelvedRules: 0, outOfServiceRules: 0 })
    })
  })
  await page.route(
    (url) => url.pathname === '/api/alarms' && url.searchParams.get('activeOnly') === 'true',
    (route) => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(list) })
  )

  await page.goto('/overview')
  const card = page.locator('[data-test="stat-alarms"]')
  await expect(card.locator('.value')).toHaveText('4')
  await expect(card).toContainText('当前活动报警 5 条')
  await expect(page.locator('[data-test="overview-summary-error"]')).toHaveCount(0)
  const bellBadge = page.locator('[data-test="alarm-bell"]').locator('xpath=ancestor::*[contains(@class,"el-badge")]')
  await expect(bellBadge.locator('.el-badge__content')).toHaveText('4')
  await expect(page.locator('[data-test="alarm-count-error"]')).toHaveCount(0)

  summaryMode = 'fail'
  list = many.slice(0, 3)
  await page.clock.fastForward(30_000)
  const summaryAlert = page.locator('[data-test="overview-summary-error"]')
  await expect(summaryAlert).toContainText('报警统计不可用')
  await expect(summaryAlert).toContainText('已加载的报警')
  await expect(card.locator('.value')).toHaveText('3')
  await expect(card).toContainText('当前活动报警 3 条')
  await expect(page.locator('[data-test="overview-alarms"] .el-table__body tr')).toHaveCount(3)
  const stale = page.locator('[data-test="alarm-count-error"]')
  await expect(stale.locator('.el-badge__content')).toHaveText('4')
  await page.locator('[data-test="alarm-bell"]').hover()
  await expect(page.getByRole('tooltip')).toContainText('上次的 4')
})

async function overviewFixture(page: Page, request: APIRequestContext) {
  const login = await request.post('/api/auth/login', { data: { userName: 'admin', password: 'admin123' } })
  expect(login.ok()).toBeTruthy()
  await page.addInitScript(({ token, user }) => {
    localStorage.setItem('scada_token', token)
    localStorage.setItem('scada_user', JSON.stringify(user))
    localStorage.setItem('scada_sound', 'off')
  }, await login.json())
  const state = { healthFails: false, listFails: false, summaryFails: false, unack: 1 }
  const devices = [
    { id: 101, name: '异常泵', online: false, error: '连接超时', lastPollTs: 1_000, lastCycleMs: 0, tagCount: 1, commFailures: 3 },
    { id: 102, name: '运行泵', online: true, error: null, lastPollTs: 2_000, lastCycleMs: 0, tagCount: 1, commFailures: 0 }
  ]
  await page.route('**/api/health', route => route.fulfill({
    status: state.healthFails ? 500 : 200, contentType: 'application/json',
    body: JSON.stringify(state.healthFails ? { message: '采集状态暂不可用' } : {
      status: 'ok', environment: 'Development', uptimeSeconds: 300, serverTime: new Date().toISOString(),
      devices: devices.map(device => ({ ...device, deviceId: device.id })), onlineDevices: 1, totalDevices: 2, configuredTags: 1, busBacklog: 0,
      disk: { freeGb: 30, totalGb: 100 }, storage: { historyBytes: 1024 }
    })
  }))
  await page.route('**/api/alarms/summary', route => route.fulfill({
    status: state.summaryFails ? 500 : 200, contentType: 'application/json',
    body: JSON.stringify(state.summaryFails ? { message: '报警统计暂不可用' } : {
      unack: state.unack, acked: 0, rtnUnack: 7, suppressed: 1, shelved: 2,
      chattering: 0, shelvedRules: 0, outOfServiceRules: 0,
      flood: { count: 20, threshold: 10, inFlood: true },
      floodGuard: { anyInFlood: true, areas: [{ areaId: 1, areaName: '动力区', inFlood: true }] }
    })
  }))
  await page.route(url => url.pathname === '/api/alarms', route => route.fulfill({
    status: state.listFails ? 500 : 200, contentType: 'application/json',
    body: JSON.stringify(state.listFails ? { message: '报警列表暂不可用' } : [
      { id: 901, tagId: 501, deviceId: 101, level: 'Critical', value: 95, startTs: 1_000, endTs: null, ackTs: null, message: '出口温度高限' }
    ])
  }))
  await page.route('**/api/tags', route => route.fulfill({
    contentType: 'application/json', body: JSON.stringify([{ id: 501, deviceId: 101, name: '出口温度', unit: '°C', dataType: 'Float', kind: 'Physical' }])
  }))
  await page.route('**/api/devices', route => route.fulfill({ contentType: 'application/json', body: JSON.stringify(devices) }))
  return state
}

test('overview prioritizes actionable alarms on desktop and narrow mobile screens', async ({ page, request }, testInfo) => {
  await overviewFixture(page, request)
  await page.goto('/overview')
  await expect(page.locator('[data-test="stat-returned"]')).toContainText('7')
  await expect(page.locator('[data-test="stat-protection"] .value')).toHaveText('1 / 2')
  await expect(page.locator('[data-test="overview-flood"]')).toContainText('动力区')
  await expect(page.locator('[data-test="health-tag"]')).toHaveText('服务可用')
  await expect(page.locator('[data-test="header-alarm-flood"]')).toHaveText('报警泛滥')
  await expect(page.locator('[data-test="overview-alarms"]')).toContainText('95.00 °C')
  await expect(page.locator('[data-test="overview-alarms"]')).toContainText('异常泵 / 出口温度')
  await expect(page.locator('[data-test="overview-quick"]')).not.toHaveAttribute('open')
  await expect(page.locator('[data-test="overview-maintenance"]')).not.toHaveAttribute('open')
  await expect(page.locator('[data-test="overview-quick-overview"]')).toHaveCount(0)
  await expect(page.locator('button button')).toHaveCount(0)
  for (const width of [1600, 390, 320]) {
    await page.setViewportSize({ width, height: 844 })
    const alarmBox = await page.locator('[data-test="overview-alarm-section"]').boundingBox()
    const deviceBox = await page.locator('[data-test="overview-device-section"]').boundingBox()
    expect(alarmBox!.y).toBeLessThan(650)
    expect(alarmBox!.y).toBeLessThan(deviceBox!.y)
    if (width < 768) {
      await expect(page.locator('[data-test="overview-alarm-cards"]')).toBeVisible()
      await expect(page.locator('[data-test="overview-alarms"]')).toBeHidden()
      await expect(page.locator('[data-test="overview-device-cards"]')).toContainText('连接超时')
    }
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true)
    await page.screenshot({ path: testInfo.outputPath(`overview-${width}.png`), fullPage: true })
  }
})

test('overview refresh failures keep prior device data while alarm counts continue updating', async ({ page, request }) => {
  const state = await overviewFixture(page, request)
  await page.goto('/overview')
  await expect(page.locator('[data-test="stat-alarms"] .value')).toHaveText('1')
  await expect(page.locator('[data-test="overview-stale"]')).toHaveCount(0)
  state.healthFails = true
  state.unack = 8
  await page.locator('[data-test="overview-refresh"]').click()
  await expect(page.locator('[data-test="overview-load-error"]')).toContainText('采集状态暂不可用')
  await expect(page.locator('[data-test="stat-alarms"] .value')).toHaveText('8')
  await expect(page.locator('[data-test="stat-devices"] .value')).toHaveText('1 / 2')
  await expect(page.locator('[data-test="overview-stale"]')).toBeVisible()
  await expect(page.locator('[data-test="overview-devices"]')).toContainText('数据已过期')
  state.healthFails = false
  state.listFails = true
  await page.locator('[data-test="overview-refresh"]').click()
  await expect(page.locator('[data-test="overview-load-error"]')).toHaveCount(0)
  await expect(page.locator('[data-test="overview-list-error"]')).toContainText('报警列表暂不可用')
  await expect(page.locator('[data-test="overview-alarms"]')).toContainText('出口温度高限')
  state.listFails = false
  await page.locator('[data-test="overview-refresh"]').click()
  await expect(page.locator('[data-test="overview-stale"]')).toHaveCount(0)
})

test('overview marks paused background data stale until foreground refresh succeeds', async ({ page, request }) => {
  await page.clock.install()
  await overviewFixture(page, request)
  await page.goto('/overview')
  await expect(page.locator('[data-test="stat-alarms"] .value')).toHaveText('1')
  await page.evaluate(() => Object.defineProperty(document, 'hidden', { configurable: true, value: true }))
  await page.clock.fastForward(20_000)
  await expect(page.locator('[data-test="overview-stale"]')).toBeVisible()
  await page.evaluate(() => {
    Object.defineProperty(document, 'hidden', { configurable: true, value: false })
    document.dispatchEvent(new Event('visibilitychange'))
  })
  await expect(page.locator('[data-test="overview-stale"]')).toHaveCount(0)
})

test('overview drilldowns preserve device, tag and alarm state context', async ({ page, request }) => {
  await overviewFixture(page, request)
  await page.goto('/overview')
  await page.locator('[data-test="overview-devices"]').getByRole('button', { name: '异常泵', exact: true }).click()
  await expect(page).toHaveURL(/realtime\?deviceId=101/)
  await expect(page.locator('[data-test="device-filter"]')).toContainText('异常泵')
  await page.goto('/overview')
  await page.locator('[data-test="overview-alarms"]').getByRole('button', { name: '趋势', exact: true }).click()
  await expect(page).toHaveURL(/trends\?tagId=501/)
  await expect(page.locator('[data-test="tag-picker"]')).toContainText('出口温度')
  await page.goto('/overview')
  await page.locator('[data-test="stat-returned"]').getByRole('button').click()
  await expect(page).toHaveURL(/alarms\?state=rtnUnack/)
  await expect(page.locator('[data-test="filter-rtn"]')).toHaveClass(/is-checked/)
  await page.goto('/overview')
  await page.locator('[data-test="stat-protection"]').getByRole('button').click()
  await expect(page.locator('[data-test="filter-suppressed"]')).toHaveClass(/is-checked/)
})
