import { test, expect, type APIRequestContext, type Page } from '@playwright/test'

const devices = [
  { id: 201, name: 'Pump A', protocol: 'ModbusTcp', host: '10.10.1.1', enabled: true, gatewayId: 7, areaId: 1 },
  { id: 202, name: 'Pump B', protocol: 'OpcUa', host: '10.10.1.2', enabled: false, gatewayId: null, areaId: 2 },
  { id: 203, name: 'Boiler', protocol: 'ModbusTcp', host: '10.10.2.3', enabled: false, gatewayId: 7, areaId: 1 }
].map(device => ({
  port: 502, stationNumber: 1, byteOrder: 'ABCD', pollIntervalMs: 1000, timeoutMs: 3000,
  serialPort: 'COM1', baudRate: 9600, dataBits: 8, parity: 'None', stopBits: 'One',
  cpu: 'S71200', rack: 0, slot: 1, opcEndpointUrl: 'opc.tcp://10.10.1.2:4840',
  opcSecurityMode: 'SignAndEncrypt', opcSecurityPolicy: 'Basic256Sha256', opcUserName: '', hasOpcPassword: false,
  ...device
}))
const tags = [
  { id: 31, deviceId: 201, name: 'Pump 温度' },
  { id: 32, deviceId: 202, name: 'Tank 压力' },
  { id: 33, deviceId: 203, name: 'Boiler 流量' }
].map(tag => ({
  kind: 'Physical', areaId: 1, area: 'HoldingRegister', address: 0, dataType: 'Float', description: '',
  scale: 1, offset: 0, writable: false, archiveMode: 'None', deadband: 0, archivePeriodSec: 600,
  dbNumber: 1, bitOffset: 0, nodeId: '', ...tag
}))
const rules = [
  { id: 41, tagId: 31, message: '温度过高', enabled: true, level: 'Critical', outOfService: false },
  { id: 42, tagId: 31, message: '温度过低', enabled: false, level: 'Warning', outOfService: false },
  { id: 43, tagId: 32, message: '压力过高', enabled: true, level: 'Warning', outOfService: true }
].map(rule => ({ type: 'High', threshold: 90, delaySec: 3, offDelaySec: 0, recoverDeadband: 0, suppressMode: 'None', ...rule }))

const rows = (page: Page) => page.locator('.el-table__body-wrapper .el-table__row')

async function prepare(page: Page, request: APIRequestContext): Promise<void> {
  const response = await request.post('/api/auth/login', { data: { userName: 'admin', password: 'admin123' } })
  expect(response.ok()).toBeTruthy()
  const auth = await response.json()
  await page.addInitScript(({ token, user }) => {
    localStorage.setItem('scada_token', token)
    localStorage.setItem('scada_user', JSON.stringify(user))
  }, auth)
  await page.route(url => url.pathname === '/api/devices', route => route.fulfill({ json: devices }))
  await page.route(url => url.pathname === '/api/areas', route => route.fulfill({ json: [{ id: 1, name: '一车间' }, { id: 2, name: '二车间' }] }))
  await page.route(url => url.pathname === '/api/tags', route => {
    const query = new URL(route.request().url()).searchParams
    const deviceId = query.get('deviceId'), search = query.get('search')?.toLowerCase()
    return route.fulfill({ json: tags.filter(tag => (!deviceId || tag.deviceId === Number(deviceId)) && (!search || tag.name.toLowerCase().includes(search))) })
  })
  await page.route(url => url.pathname === '/api/alarm-rules', route => {
    const tagId = new URL(route.request().url()).searchParams.get('tagId')
    return route.fulfill({ json: rules.filter(rule => !tagId || rule.tagId === Number(tagId)) })
  })
}

async function choose(page: Page, testId: string, label: string): Promise<void> {
  await page.locator(`[data-test="${testId}"]`).click()
  await page.getByRole('option', { name: label, exact: true }).click()
}

test('device search, protocol and state filters clear hidden selections and retain gateway scope', async ({ page, request }) => {
  await prepare(page, request)
  await page.goto('/config/devices')
  await expect(rows(page)).toHaveCount(3)
  await expect(page.locator('[data-test="device-filter-protocol"]')).toContainText('全部协议')
  await rows(page).filter({ hasText: 'Pump A' }).locator('.el-checkbox').click()
  await expect(page.locator('[data-test="device-batch-count"]')).toContainText('1')

  await page.getByRole('textbox', { name: '搜索设备名称、地址或区域' }).fill('BOILER 10.10')
  await expect(rows(page)).toHaveCount(3)
  await page.locator('[data-test="device-query"]').click()
  await expect(rows(page)).toHaveCount(1)
  await expect(rows(page)).toContainText('Boiler')
  await expect(page.locator('[data-test="device-batch-count"]')).toHaveCount(0)
  await page.locator('[data-test="device-reset-filters"]').click()
  await choose(page, 'device-filter-protocol', 'OPC UA')
  await page.locator('[data-test="device-query"]').click()
  await expect(rows(page)).toContainText('Pump B')
  await choose(page, 'device-filter-enabled', '已启用')
  await page.locator('[data-test="device-query"]').click()
  await expect(rows(page)).toHaveCount(0)
  await expect(page.locator('.el-table__empty-block')).toContainText('没有符合筛选条件的记录')
  await page.locator('.el-table__empty-block').getByRole('button', { name: '清除筛选' }).click()
  await expect(rows(page)).toHaveCount(3)

  await page.goto('/config/devices?gatewayId=7&keep=1')
  await expect(page.locator('[data-test="device-result-count"]')).toHaveText('显示 2 / 2 条')
  await page.getByRole('textbox', { name: '搜索设备名称、地址或区域' }).fill('一车间 pump')
  await page.locator('[data-test="device-query"]').click()
  await expect(page.locator('[data-test="device-result-count"]')).toHaveText('显示 1 / 2 条')
  await page.locator('[data-test="device-reset-filters"]').click()
  await expect(page).toHaveURL('/config/devices?gatewayId=7&keep=1')
  await expect(rows(page)).toHaveCount(2)
  await page.screenshot({ path: test.info().outputPath('device-filters.png'), animations: 'disabled' })
})

test('device to tags to alarm rules carries context through creation, refresh and browser back', async ({ page, request }) => {
  await prepare(page, request)
  await page.goto('/config/devices')
  await rows(page).filter({ hasText: 'Pump A' }).locator('[data-test="device-tags"]').click()
  await expect(page).toHaveURL('/config/tags?deviceId=201')
  await expect(rows(page)).toHaveCount(1)
  await expect(rows(page)).toContainText('Pump 温度')
  await page.locator('[data-test="tag-create"]').click()
  await expect(page.locator('[data-test="tag-dialog"]')).toBeVisible()
  await expect(page.locator('[data-test="tag-device"]')).toContainText('Pump A')
  await page.keyboard.press('Escape')

  await rows(page).locator('[data-test="tag-rules"]').click()
  await expect(page).toHaveURL('/config/rules?tagId=31')
  await expect(rows(page)).toHaveCount(2)
  await page.locator('[data-test="rule-create"]').click()
  await expect(page.locator('[data-test="rule-tag"]')).toContainText('Pump 温度')
  await page.keyboard.press('Escape')
  await page.reload()
  await expect(rows(page)).toHaveCount(2)
  await expect(page.locator('[data-test="rule-filter-tag"]')).toContainText('Pump 温度')

  await page.goBack()
  await expect(page).toHaveURL('/config/tags?deviceId=201')
  await expect(rows(page)).toHaveCount(1)
  await choose(page, 'tag-filter-device', 'Pump B')
  await page.locator('[data-test="tag-query"]').click()
  await expect(page).toHaveURL('/config/tags?deviceId=202')
  await expect(rows(page)).toContainText('Tank 压力')
  await page.reload()
  await expect(rows(page)).toContainText('Tank 压力')
  await page.locator('[data-test="tag-reset-filters"]').click()
  await expect(page).toHaveURL('/config/tags')
  await expect(rows(page)).toHaveCount(3)
})

test('alarm rule search and status filters support empty recovery and clear hidden selections', async ({ page, request }) => {
  await prepare(page, request)
  await page.goto('/config/rules')
  await expect(rows(page)).toHaveCount(3)
  await rows(page).filter({ hasText: '温度过高' }).locator('.el-checkbox').click()
  await expect(page.locator('[data-test="rule-batch-count"]')).toContainText('1')
  await page.getByRole('textbox', { name: '搜索测点名称或报警消息' }).fill('PUMP 过低')
  await expect(rows(page)).toHaveCount(1)
  await expect(rows(page)).toContainText('温度过低')
  await expect(page.locator('[data-test="rule-batch-count"]')).toHaveCount(0)
  await page.locator('[data-test="rule-reset-filters"]').click()
  await choose(page, 'rule-filter-state', '禁用')
  await expect(rows(page)).toContainText('温度过低')
  await choose(page, 'rule-filter-state', '停用中')
  await expect(rows(page)).toContainText('压力过高')
  await page.getByRole('textbox', { name: '搜索测点名称或报警消息' }).fill('no-match')
  await expect(rows(page)).toHaveCount(0)
  await page.locator('.el-table__empty-block').getByRole('button', { name: '清除筛选' }).click()
  await expect(page.locator('[data-test="rule-result-count"]')).toHaveText('显示 3 / 3 条')
  await page.screenshot({ path: test.info().outputPath('rule-filters.png'), animations: 'disabled' })
})

test('clearing a tag search waits for Query, while IME confirmation does not submit a search', async ({ page, request }) => {
  await prepare(page, request)
  await page.goto('/config/tags')
  await expect(rows(page)).toHaveCount(3)
  const search = page.getByRole('textbox', { name: '搜索测点名称/描述' })
  const requests: string[] = []
  page.on('request', request => { if (new URL(request.url()).pathname === '/api/tags') requests.push(request.url()) })
  await search.fill('Pump')
  await search.dispatchEvent('keydown', { key: 'Enter', isComposing: true })
  await page.evaluate(() => new Promise(requestAnimationFrame))
  expect(requests).toHaveLength(0)
  await search.press('Enter')
  await expect(rows(page)).toHaveCount(1)
  await search.hover()
  await page.locator('.el-input').filter({ has: search }).locator('.el-input__clear').click()
  await expect(search).toHaveValue('')
  await expect(rows(page)).toHaveCount(1)
  await expect(page.locator('[data-test="tag-query-pending"]')).toBeVisible()
  await page.locator('[data-test="tag-query"]').click()
  await expect(rows(page)).toHaveCount(3)
  await expect(page.locator('[data-test="tag-result-count"]')).toHaveText('共 3 条查询结果')
})

for (const failOld of [false, true]) {
  test(`new tag filter wins over a delayed older ${failOld ? 'failure' : 'result'}`, async ({ page, request }) => {
    await prepare(page, request)
    await page.goto('/config/tags')
    await expect(rows(page)).toHaveCount(3)
    let release!: () => void
    const gate = new Promise<void>(resolve => { release = resolve })
    await page.route(url => url.pathname === '/api/tags' && url.searchParams.get('deviceId') === '201', async route => {
      await gate
      await route.fulfill(failOld ? { status: 500, json: { message: '过期的加载失败' } } : { json: [tags[0]] })
    })
    const pending = page.waitForRequest(url => new URL(url.url()).searchParams.get('deviceId') === '201')
    await choose(page, 'tag-filter-device', 'Pump A')
    await page.locator('[data-test="tag-query"]').click()
    await pending
    try {
      await choose(page, 'tag-filter-device', 'Pump B')
      await page.getByRole('textbox', { name: '搜索测点名称/描述' }).press('Enter')
      await expect(rows(page)).toHaveCount(1)
      await expect(rows(page)).toContainText('Tank 压力')
      const response = page.waitForResponse(response => new URL(response.url()).searchParams.get('deviceId') === '201')
      release()
      await (await response).finished()
      await page.evaluate(() => new Promise(requestAnimationFrame))
      await expect(rows(page)).toContainText('Tank 压力')
      await expect(page.locator('[data-test="tags-load-error"]')).toHaveCount(0)
      await expect(page.getByRole('button', { name: '查询', exact: true })).not.toHaveClass(/is-loading/)
    } finally { release() }
  })
}

test('new rule filter wins over a delayed older result', async ({ page, request }) => {
  await prepare(page, request)
  await page.goto('/config/rules')
  await expect(rows(page)).toHaveCount(3)
  let release!: () => void
  const gate = new Promise<void>(resolve => { release = resolve })
  await page.route(url => url.pathname === '/api/alarm-rules' && url.searchParams.get('tagId') === '31', async route => {
    await gate
    await route.fulfill({ json: rules.filter(rule => rule.tagId === 31) })
  })
  const pending = page.waitForRequest(request => new URL(request.url()).pathname === '/api/alarm-rules' && new URL(request.url()).searchParams.get('tagId') === '31')
  await choose(page, 'rule-filter-tag', 'Pump 温度 (#31)')
  await pending
  try {
    await choose(page, 'rule-filter-tag', 'Tank 压力 (#32)')
    await expect(rows(page)).toHaveCount(1)
    await expect(rows(page)).toContainText('压力过高')
    const response = page.waitForResponse(response => new URL(response.url()).pathname === '/api/alarm-rules' && new URL(response.url()).searchParams.get('tagId') === '31')
    release()
    await (await response).finished()
    await page.evaluate(() => new Promise(requestAnimationFrame))
    await expect(rows(page)).toContainText('压力过高')
    await expect(page.locator('[data-test="rule-refresh"]')).not.toHaveClass(/is-loading/)
  } finally { release() }
})

test('invalid context IDs are ignored without sending invalid filters to the server', async ({ page, request }) => {
  await prepare(page, request)
  await page.goto('/config/tags?deviceId=201&deviceId=202')
  await expect(rows(page)).toHaveCount(3)
  await page.goto('/config/rules?tagId=9007199254740992')
  await expect(rows(page)).toHaveCount(3)
  await expect(page.locator('[data-test="rules-load-error"]')).toHaveCount(0)
})
