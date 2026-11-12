import { test, expect, type APIRequestContext, loginApi, applySession } from './fixtures'

// 第二十二轮：设计不合理之处（见 docs/quality/usability-audit.md 第 23 节）

async function writableTag(request: APIRequestContext, headers: Record<string, string>, address: number) {
  const suffix = `${Date.now()}-${address}`
  const device = await request.post('/api/devices', {
    headers, data: { name: `DR-D-${suffix}`, protocol: 'ModbusTcp', host: '127.0.0.1', port: 1502, stationNumber: 1, byteOrder: 'ABCD', pollIntervalMs: 1000, timeoutMs: 1000, enabled: false, areaId: 1 }
  })
  expect(device.ok(), await device.text()).toBeTruthy()
  const tag = await request.post('/api/tags', {
    headers, data: { deviceId: (await device.json()).id, name: `DR-T-${suffix}`, area: 'HoldingRegister', address, dataType: 'Float', scale: 1, offset: 0, writable: true, archiveMode: 'None', deadband: 0, archivePeriodSec: 60 }
  })
  expect(tag.ok(), await tag.text()).toBeTruthy()
  return (await tag.json()).id as number
}

const criticalHornOnly = { level: 'Critical', horn: true, beacon: false, towerRed: false, towerYellow: false, towerBlue: false, towerGreen: false, flash: false, soundKey: null }

async function createPanel(request: APIRequestContext, headers: Record<string, string>, name: string, tagId: number) {
  const created = await request.post('/api/annunciators', {
    headers,
    data: {
      name, areaId: null, enabled: true, latchMode: 'Follow', silenceMinutes: 5,
      flashOnMs: 700, flashOffMs: 300, criticalFlashOnMs: 150, criticalFlashOffMs: 150, browserSound: true,
      outputs: [{ kind: 'Horn', tagId, activeValue: 1, inactiveValue: 0, enabled: true }],
      levels: [criticalHornOnly]
    }
  })
  expect(created.ok(), await created.text()).toBeTruthy()
  return await created.json()
}

test('声光面板：保存校验失败不会删掉已有输出', async ({ request }) => {
  const { headers } = await loginApi(request)
  const tagId = await writableTag(request, headers, 21)
  const panel = await createPanel(request, headers, `声光校验${Date.now()}`, tagId)

  const duplicated = await request.put(`/api/annunciators/${panel.id}`, {
    headers,
    data: {
      name: panel.name, areaId: null, enabled: true, latchMode: 'Follow', silenceMinutes: 5,
      flashOnMs: 700, flashOffMs: 300, criticalFlashOnMs: 150, criticalFlashOffMs: 150, browserSound: true,
      outputs: [{ kind: 'Horn', tagId, activeValue: 1, inactiveValue: 0, enabled: true }, { kind: 'Horn', tagId, activeValue: 1, inactiveValue: 0, enabled: true }],
      levels: [criticalHornOnly]
    }
  })
  expect(duplicated.status()).toBe(400)
  const after = await (await request.get(`/api/annunciators/${panel.id}`, { headers })).json()
  expect(after.outputs).toHaveLength(1)
  expect(after.levels).toHaveLength(1)
})

test('声光面板：编辑保存保留闪烁与等级映射，新增输出不会选到已占用通道', async ({ page, request }) => {
  const session = await loginApi(request)
  const tagId = await writableTag(request, session.headers, 22)
  const name = `声光保留${Date.now()}`
  const panel = await createPanel(request, session.headers, name, tagId)
  await applySession(page, session)
  await page.goto('/config/annunciators')
  const row = page.getByRole('row').filter({ hasText: name })
  await row.locator('[data-test="ann-edit"]').click()
  const dialog = page.getByRole('dialog', { name: '编辑声光面板' })
  await dialog.locator('[data-test="ann-save"]').click()
  await expect(dialog).toBeHidden()

  const saved = await (await request.get(`/api/annunciators/${panel.id}`, { headers: session.headers })).json()
  expect(saved.flashOnMs).toBe(700)
  expect(saved.criticalFlashOnMs).toBe(150)
  expect(saved.levels).toHaveLength(1)
  expect(saved.levels[0]).toMatchObject({ level: 'Critical', horn: true, beacon: false })

  await row.locator('[data-test="ann-edit"]').click()
  await dialog.locator('[data-test="ann-add-output"]').click()
  await expect(dialog.locator('[data-test="ann-output-kind"]').nth(1)).toContainText('Beacon')
})

test('声光面板：试验先确认会驱动哪些输出，状态显示消音截止与写入失败', async ({ page, request }) => {
  const session = await loginApi(request)
  const tagId = await writableTag(request, session.headers, 23)
  const name = `声光试验${Date.now()}`
  const panel = await createPanel(request, session.headers, name, tagId)
  await applySession(page, session)
  let tests = 0
  await page.route(url => url.pathname === `/api/annunciators/${panel.id}/test`, route => { tests++; return route.fulfill({ json: {} }) })
  const until = new Date(2026, 10, 2, 9, 45).getTime()
  await page.route(url => url.pathname === '/api/annunciators/status', route => route.fulfill({ json: [{
    panelId: panel.id, name, areaId: null, enabled: true, drivingLevel: null, horn: false, beacon: false, towerRed: false, towerYellow: false,
    towerBlue: false, towerGreen: false, flashing: false, silenced: true, silenceUntilTs: until, soundKey: null, lastError: '写入超时'
  }] }))
  await page.goto('/config/annunciators')
  const row = page.getByRole('row').filter({ hasText: name })
  await expect(row.locator('[data-test="ann-silenced"]')).toHaveText('消音至 09:45')
  await expect(row.locator('[data-test="ann-last-error"]')).toHaveText('输出写入失败')

  await row.locator('[data-test="ann-test"]').click()
  const box = page.locator('.el-message-box')
  await expect(box).toContainText('Horn')
  await expect(box).toContainText('1.5 秒')
  await box.getByRole('button', { name: '取消' }).click()
  expect(tests).toBe(0)
  await row.locator('[data-test="ann-test"]').click()
  await box.getByRole('button', { name: '确定' }).click()
  await expect.poll(() => tests).toBe(1)
})

test('报表模板：报警次数列可按等级保存，填过内容的编辑框关闭前确认', async ({ page, request }) => {
  const session = await loginApi(request)
  await applySession(page, session)
  await page.goto('/report-center')
  await page.locator('[data-test="report-center-templates"]').click()
  await page.locator('[data-test="report-template-new"]').click()
  const dialog = page.getByRole('dialog', { name: '新建报表模板' })
  await dialog.locator('[data-test="report-template-name"]').fill('未保存的模板')
  await dialog.getByRole('button', { name: '取消', exact: true }).click()
  await expect(page.getByRole('button', { name: '放弃修改', exact: true })).toBeVisible()
  await page.getByRole('button', { name: '继续编辑', exact: true }).click()
  await expect(dialog).toBeVisible()

  const name = `报警等级${Date.now()}`
  await dialog.locator('[data-test="report-template-name"]').fill(name)
  await dialog.locator('[data-test="report-add-column"]').click()
  await dialog.locator('[data-test="report-column-kind-0"]').click()
  await page.getByRole('option', { name: '报警次数', exact: true }).click()
  await dialog.locator('[data-test="report-column-0"] .el-select').filter({ hasText: '最低级别' }).click()
  await page.getByRole('option', { name: '警告', exact: true }).click()
  await dialog.locator('[data-test="report-template-save"]').click()
  await expect(dialog).toBeHidden()
  const templates = await (await request.get('/api/report-templates', { headers: session.headers })).json()
  const saved = templates.find((x: { name: string }) => x.name === name)
  expect(saved.columns[0]).toMatchObject({ kind: 'AlarmCount', minLevel: 1 })
})

test('主备切换：提交后按钮锁定，不能连点再发一次', async ({ page, request }) => {
  const session = await loginApi(request)
  await applySession(page, session)
  await page.route(url => url.pathname === '/api/ha/status', route => route.fulfill({ json: {
    enabled: true, nodeId: 'node-a', role: 'Primary', term: 2, generation: 1, selfUrl: null, peerUrl: 'http://127.0.0.1:9',
    peer: null, peerSeenTs: 0, witness: null, lease: null, witnessError: null, autoFailover: true, promotedTs: 0,
    configSyncTs: 0, historySyncTs: 0, historyRowsBehind: 0, configApplied: 0, historyRowsApplied: 0, replicationError: null, switchovers: 1, events: []
  } }))
  let switchovers = 0
  let finish: () => void = () => {}
  await page.route(url => url.pathname === '/api/ha/switchover', async route => {
    switchovers++
    await new Promise<void>(resolve => { finish = resolve })
    await route.fulfill({ json: {} })
  })
  await page.goto('/ops')
  const button = page.locator('[data-test="ha-switchover"]')
  await button.click()
  const box = page.locator('.el-message-box')
  await box.locator('input').fill('计划检修')
  await box.getByRole('button', { name: '确定' }).click()
  await expect.poll(() => switchovers).toBe(1)
  await expect(button).toBeDisabled()
  finish()
  await expect(button).toBeEnabled()
  expect(switchovers).toBe(1)
})

test('工程导入：预览进行中锁定冲突策略，发布确认写明策略与各类变更条数', async ({ page, request }) => {
  const session = await loginApi(request)
  await applySession(page, session)
  const plan = {
    baseSha: 'sha-1', versionId: null, counts: { add: 1, update: 1 }, hidden: 0, errors: [], forbidden: [], warnings: [],
    entries: [
      { section: 'devices', sectionLabel: '设备', key: 'D1', finalKey: 'D1', action: 'add', areas: [], changes: [] },
      { section: 'devices', sectionLabel: '设备', key: 'D2', finalKey: 'D2', action: 'update', areas: [], changes: [] }
    ]
  }
  let release: () => void = () => {}
  await page.route(url => url.pathname === '/api/project/import/preview', async route => {
    await new Promise<void>(resolve => { release = resolve })
    await route.fulfill({ json: plan })
  })
  const applied: { resolution: string }[] = []
  await page.route(url => url.pathname === '/api/project/import', route => {
    applied.push(route.request().postDataJSON())
    return route.fulfill({ json: { ...plan, versionId: 9 } })
  })
  await page.goto('/config/project')
  await page.getByRole('tab', { name: '导入发布' }).click()
  await page.locator('[data-test="project-file"]').setInputFiles({ name: 'pkg.json', mimeType: 'application/json', buffer: Buffer.from('{"devices":[]}') })
  await expect(page.locator('[data-test="project-resolution"] .el-select__wrapper')).toHaveClass(/is-disabled/)
  release()
  await expect(page.locator('[data-test="project-resolution"] .el-select__wrapper')).not.toHaveClass(/is-disabled/)

  await page.locator('[data-test="project-apply"]').click()
  const box = page.locator('.el-message-box')
  await expect(box).toContainText('冲突处理：跳过')
  await expect(box).toContainText('新增 1、覆盖 1、改名 0、删除 0')
  await box.getByRole('button', { name: '发布' }).click()
  await expect.poll(() => applied.length).toBe(1)
  expect(applied[0].resolution).toBe('Skip')
})

test('系统管理：磁盘读不到显示未知、展示累计丢弃、日志默认范围写明、改角色前说明会断开会话', async ({ page, request }) => {
  const session = await loginApi(request)
  const userName = `dr${Date.now()}`.slice(0, 20)
  const created = await request.post('/api/users', { headers: session.headers, data: { userName, password: 'pass1234', displayName: '设计复查', role: 'Operator', enabled: true } })
  expect(created.ok(), await created.text()).toBeTruthy()
  await applySession(page, session)
  await page.route(url => url.pathname === '/api/health', async route => {
    const health = await (await route.fetch()).json()
    await route.fulfill({ json: { ...health, busDropped: 12, disk: { root: 'X:\\data', totalGb: 0, freeGb: 0 } } })
  })
  let updates = 0
  await page.route(url => /^\/api\/users\/\d+$/.test(url.pathname), route => {
    if (route.request().method() === 'PUT') updates++
    return route.continue()
  })
  await page.goto('/system')

  const userRow = page.getByRole('row').filter({ hasText: userName })
  await userRow.getByRole('button', { name: '编辑', exact: true }).click()
  const dialog = page.locator('.el-overlay-dialog[aria-label="编辑用户"]')
  await dialog.locator('.el-select').click()
  await page.getByRole('option', { name: '查看员', exact: true }).click()
  await dialog.getByRole('button', { name: '保存', exact: true }).click()
  const box = page.locator('.el-message-box')
  await expect(box).toContainText('立即断开')
  await box.getByRole('button', { name: '取消' }).click()
  expect(updates).toBe(0)
  await expect(dialog).toBeVisible()
  await dialog.getByRole('button', { name: '取消', exact: true }).click()

  await page.getByRole('tab', { name: '操作日志' }).click()
  await expect(page.locator('[data-test="log-default-range"]')).toHaveText('未选时间时查询最近 7 天')

  await page.getByRole('tab', { name: '系统状态' }).click()
  await expect(page.locator('[data-test="health-disk-free"]')).toHaveText('未知（读取失败）')
  await expect(page.locator('[data-test="health-bus-sub"]')).toContainText('启动以来已丢弃 12 条采集值')
})

test('计算测点：停止自动刷新不改写质量，删除前写明会连带删除的报警规则', async ({ page, request }) => {
  const session = await loginApi(request)
  await applySession(page, session)
  const tag = {
    id: 880001, name: '设计复查计算点', kind: 'Calculated', deviceId: null, areaId: 1, unit: 'kW', expression: '#1 + 1', description: '',
    calculationEnabled: true, calculationIntervalMs: 1000, inputMaxAgeMs: 5000, calculationRevision: 3, archiveMode: 'None', deadband: 0, archivePeriodSec: 60, writable: false
  }
  await page.route(url => url.pathname === '/api/calculated-tags', route => route.fulfill({ json: [
    { tag, dependencies: [], current: { tagId: tag.id, value: 5, quality: 'Good', ts: Date.now() }, definitionError: null }
  ] }))
  await page.route(url => url.pathname === '/api/alarm-rules', route => route.fulfill({ json: [{ id: 1, tagId: tag.id }, { id: 2, tagId: tag.id }] }))
  let deletes = 0
  await page.route(url => url.pathname === `/api/calculated-tags/${tag.id}`, route => { deletes++; return route.fulfill({ status: 204 }) })
  await page.goto('/config/calculations')
  const row = page.getByRole('row').filter({ hasText: tag.name })
  await expect(row.locator('[data-test="calc-quality"]')).toHaveText('正常')
  await page.locator('.page .toolbar .el-switch').filter({ hasText: '每 2 秒刷新' }).click()
  await expect(page.locator('[data-test="calc-snapshot"]')).toBeVisible({ timeout: 10000 })
  await expect(row.locator('[data-test="calc-quality"]')).toHaveText('正常')
  await expect(row.locator('[data-test="calc-stale"]')).toHaveText('待刷新')

  await row.getByRole('button', { name: '删除', exact: true }).click()
  const box = page.locator('.el-message-box')
  await expect(box).toContainText('2 条报警规则会一并删除')
  await box.getByRole('button', { name: '取消' }).click()
  expect(deletes).toBe(0)
})

test('通知失败：全部已知悉先确认条数', async ({ page, request }) => {
  const session = await loginApi(request)
  await applySession(page, session)
  await page.route(url => url.pathname === '/api/notification-outbox/failed', route => route.fulfill({ json: [
    { id: 4, alarmEventId: 8, alarmMessage: '泵超限', routeId: 1, channelId: 1, action: 'raise', attempts: 3, lastError: '超时', finishedTs: Date.now() },
    { id: 5, alarmEventId: 9, alarmMessage: '阀卡滞', routeId: 1, channelId: 1, action: 'raise', attempts: 3, lastError: '超时', finishedTs: Date.now() }
  ] }))
  let acks = 0
  await page.route(url => url.pathname === '/api/notification-outbox/acknowledge', route => { acks++; return route.fulfill({ json: { acknowledged: 2 } }) })
  await page.goto('/config/notifications')
  await page.getByRole('tab', { name: /投递失败/ }).click()
  await page.locator('[data-test="outbox-ack-all"]').click()
  const box = page.locator('.el-message-box')
  await expect(box).toContainText('2 条失败通知')
  await box.getByRole('button', { name: '取消' }).click()
  expect(acks).toBe(0)
  await page.locator('[data-test="outbox-ack-all"]').click()
  await box.getByRole('button', { name: '确定' }).click()
  await expect.poll(() => acks).toBe(1)
})
