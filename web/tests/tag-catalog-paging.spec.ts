import { test, expect, type APIRequestContext, type Page } from './fixtures'

const PAGE_SIZE = 100

const device = {
  id: 901, name: 'Pump A', protocol: 'ModbusTcp', host: '10.30.1.1', port: 502, stationNumber: 1,
  byteOrder: 'ABCD', pollIntervalMs: 1000, timeoutMs: 3000, serialPort: 'COM1', baudRate: 9600,
  dataBits: 8, parity: 'None', stopBits: 'One', cpu: 'S71200', rack: 0, slot: 1,
  opcEndpointUrl: '', opcSecurityMode: 'None', opcSecurityPolicy: 'None', opcUserName: '', hasOpcPassword: false,
  enabled: true, gatewayId: null, areaId: 1
}

// 名称顺序与 id 顺序相反：不排序时（服务端默认 id 升序）首行是 TAG-250，名称升序首行才是 TAG-001。
// 这样"先分页再排序"的错误实现不可能碰巧通过首页断言。
const tags = Array.from({ length: 250 }, (_, index) => ({
  id: 5000 + index,
  deviceId: 901,
  name: `TAG-${String(250 - index).padStart(3, '0')}`,
  kind: 'Physical', areaId: 1, area: 'HoldingRegister', address: index, dataType: 'Float',
  description: '', scale: 1, offset: 0, writable: false, archiveMode: 'None', deadband: 0,
  archivePeriodSec: 600, dbNumber: 1, bitOffset: 0, nodeId: '', unit: '℃'
}))

async function prepare(page: Page, request: APIRequestContext, data = tags): Promise<void> {
  const response = await request.post('/api/auth/login', { data: { userName: 'admin', password: 'admin123' } })
  expect(response.ok()).toBeTruthy()
  const auth = await response.json()
  await page.addInitScript(({ token, user }) => {
    localStorage.setItem('scada_token', token)
    localStorage.setItem('scada_user', JSON.stringify(user))
    localStorage.setItem('scada_locale', 'zh-CN')
  }, auth)
  await page.route(url => url.pathname === '/api/areas', route => route.fulfill({ json: [{ id: 1, name: '一车间' }] }))
  await page.route(url => url.pathname === '/api/devices', route => route.fulfill({ json: [device], headers: { 'X-Total-Count': '1' } }))
  await page.route(url => url.pathname === '/api/tags', route => {
    const query = new URL(route.request().url()).searchParams
    const deviceId = query.get('deviceId')
    const search = query.get('search')?.toLowerCase() ?? ''
    let list = data.filter(tag => (!deviceId || tag.deviceId === Number(deviceId)) && (!search || tag.name.toLowerCase().includes(search)))
    const sort = query.get('sort'), order = query.get('order')
    if (sort === 'name') list = [...list].sort((a, b) => a.name.localeCompare(b.name))
    else if (sort === 'id') list = [...list].sort((a, b) => a.id - b.id)
    if (sort && (order === 'desc' || order === 'descending')) list = [...list].reverse()
    const skip = Number(query.get('skip') ?? 0)
    const take = Number(query.get('take') ?? 0)
    return route.fulfill({
      json: take > 0 ? list.slice(skip, skip + take) : list,
      headers: { 'X-Total-Count': String(list.length) }
    })
  })
  // 删除与引用检查走同一份内存数据：删掉当前页最后一条后才能验证"自动回到上一页"。
  await page.route(url => url.pathname.startsWith('/api/tags/'), route => {
    const path = new URL(route.request().url()).pathname
    if (route.request().method() === 'DELETE') {
      const id = Number(path.split('/').pop())
      const index = data.findIndex(tag => tag.id === id)
      if (index >= 0) data.splice(index, 1)
      return route.fulfill({ json: {} })
    }
    if (path.endsWith('/usage')) return route.fulfill({ json: { tagId: 0, tagName: '', references: [], unreadable: false } })
    return route.fulfill({ json: {} })
  })
}

const rows = (page: Page) => page.locator('[data-test="tags-table"] .el-table__body-wrapper .el-table__row')

test('tag catalog fetches one page at a time and keeps the server total', async ({ page, request }) => {
  const calls: URL[] = []
  page.on('request', req => { const url = new URL(req.url()); if (url.pathname === '/api/tags') calls.push(url) })
  await prepare(page, request)
  await page.goto('/config/tags')

  await expect(page.locator('[data-test="tag-result-count"]')).toHaveText('共 250 条查询结果')
  await expect(rows(page)).toHaveCount(PAGE_SIZE)
  await expect(rows(page).first()).toContainText('TAG-250')
  expect(calls.at(-1)!.searchParams.get('skip')).toBe('0')
  expect(calls.at(-1)!.searchParams.get('take')).toBe(String(PAGE_SIZE))

  await page.locator('[data-test="tag-pagination"] .btn-next').click()
  await expect(page).toHaveURL(/page=2/)
  await expect.poll(() => calls.at(-1)!.searchParams.get('skip')).toBe(String(PAGE_SIZE))
  await expect(rows(page).first()).toContainText('TAG-150')

  // 工程量列默认收起：展开后校验数值列右对齐与统一小数位（1.00 / 0.00），不再出现裸浮点。
  await page.locator('[data-test="tag-engineering-columns"]').click()
  const scaleCell = rows(page).first().locator('td').filter({ hasText: '1.00 / 0.00' }).first()
  await expect(scaleCell).toBeVisible()
  expect(await scaleCell.evaluate(el => getComputedStyle(el).textAlign)).toBe('right')
  const cellFont = await page.locator('[data-test="tags-table"] .el-table__body .cell').first().evaluate(el => getComputedStyle(el).fontVariantNumeric)
  expect(cellFont).toContain('tabular-nums')

  // 页码来自 URL：刷新后不能退回第一页。
  await page.reload()
  await expect(page).toHaveURL(/page=2/)
  await expect(rows(page)).toHaveCount(PAGE_SIZE)
  await expect(rows(page).first()).toContainText('TAG-150')
})

test('name sort is applied by the server before the page is sliced', async ({ page, request }) => {
  const calls: URL[] = []
  page.on('request', req => { const url = new URL(req.url()); if (url.pathname === '/api/tags') calls.push(url) })
  await prepare(page, request)

  await page.goto('/config/tags?sort=name&order=ascending')
  await expect(rows(page)).toHaveCount(PAGE_SIZE)
  await expect(rows(page).first()).toContainText('TAG-001')
  expect(calls.at(-1)!.searchParams.get('sort')).toBe('name')
  expect(calls.at(-1)!.searchParams.get('order')).toBe('asc')

  await page.goto('/config/tags?sort=name&order=ascending&page=2')
  await expect(page).toHaveURL(/page=2/)
  await expect(rows(page).first()).toContainText('TAG-101')

  await page.goto('/config/tags?sort=name&order=descending')
  await expect(rows(page).first()).toContainText('TAG-250')
})
test('deleting the last row of the last page falls back to the previous page', async ({ page, request }) => {
  await prepare(page, request, tags.slice(0, 101))
  await page.goto('/config/tags?page=2')
  await expect(page).toHaveURL(/page=2/)
  await expect(rows(page)).toHaveCount(1)
  await expect(rows(page).first()).toContainText('TAG-150')

  await page.locator('[data-test="tags-table"] .el-table__header .el-checkbox').first().click()
  await page.locator('[data-test="tag-batch-delete"]').click()
  await page.locator('.el-message-box button.el-button--primary').click()

  // 删掉第 2 页唯一一行后不能停在空页：应回到第 1 页并重新拉取。
  await expect(page).not.toHaveURL(/page=2/)
  await expect(page.locator('[data-test="tag-result-count"]')).toHaveText('共 100 条查询结果')
  await expect(rows(page)).toHaveCount(PAGE_SIZE)
})