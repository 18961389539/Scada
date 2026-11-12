import { test, expect, type APIRequestContext, type Page, loginApi } from './fixtures'

// 这三处都属于「服务端少给一个字段 / 窄屏少一条提示」的退化，类型检查看不出来，
// 只有真跑浏览器才暴露，所以单独固化成用例。

async function mockApiFallback(page: Page) {
  // 未显式 mock 的接口一律回空数组：否则请求会落到静态服务器，把 index.html 当 JSON 解析
  await page.route('**/api/**', route => route.fulfill({ json: [] }))
}

async function signInAs(page: Page, request: APIRequestContext) {
  const auth = await loginApi(request)
  await page.addInitScript(({ token, user }) => {
    localStorage.setItem('scada_token', token)
    localStorage.setItem('scada_user', JSON.stringify(user))
  }, auth)
}

// 故意少给 disk / storage / devices：老版本服务端、降级路径或前后端版本错位都会这样。
const partialHealth = { status: 'ok', onlineDevices: 0, totalDevices: 0, configuredTags: 0, busBacklog: 0 }

test('health 缺少 disk/storage 时总览与系统页不抛错', async ({ page, request }) => {
  await signInAs(page, request)
  await mockApiFallback(page)
  await page.route(url => url.pathname === '/api/health', route => route.fulfill({ json: partialHealth }))

  const errors: string[] = []
  // 只断言未捕获的 JS 异常。接口 500 之类的网络噪声（例如 SignalR 协商）由连接提示负责，
  // 这里关心的是「少一个字段会不会把整块渲染打挂」。
  page.on('pageerror', error => errors.push(String(error)))

  for (const path of ['/overview', '/system']) {
    await page.goto(path)
    // 页面渲染完成即可，等的是「没有异常」而不是某个具体数值
    await expect(page.locator('.page, main, .content').first()).toBeVisible()
    expect(errors, `${path} 渲染时抛错`).toEqual([])
  }
})

test('画面文档缺少 nodes 时给出可读错误，而不是空白画布', async ({ page, request }) => {
  await signInAs(page, request)
  await mockApiFallback(page)
  await page.route(url => url.pathname === '/api/screens/1', route => route.fulfill({
    // 只给宽高、没有图元列表：历史版本或导入损坏的画面长这样
    json: { id: 1, name: '损坏的画面', revision: 3, publishedRevision: null, document: { width: 1200, height: 720 } }
  }))
  await page.route(url => url.pathname === '/api/screens', route => route.fulfill({
    json: [{ id: 1, name: '损坏的画面', revision: 3, publishedRevision: null }]
  }))

  const errors: string[] = []
  page.on('pageerror', error => errors.push(String(error)))

  await page.goto('/config/screens/1')
  await expect(page.locator('.el-alert__title').first()).toContainText('画面数据不完整')
  // 关键：不能退化成空画布。空画布会被当成「本来就没有元素」，一保存就把原内容覆盖掉。
  await expect(page.locator('.designer-body')).toHaveCount(0)
  expect(errors).toEqual([])
})

test('窄屏表格能横向滚动时，右缘给出可滚动提示', async ({ page, request }) => {
  await signInAs(page, request)
  await mockApiFallback(page)
  await page.route(url => url.pathname === '/api/health', route => route.fulfill({
    json: {
      ...partialHealth,
      devices: [],
      disk: { root: '/', totalGb: 100, freeGb: 60 },
      storage: { configDb: 'config.db', configDbBytes: 1, historyBytes: 1 },
      warnings: [], historyCapacity: null, historyQueries: null, ha: null
    }
  }))
  await page.route(url => url.pathname === '/api/devices', route => route.fulfill({
    json: [{ id: 8301, name: '验证设备', areaId: 1, protocol: 'ModbusTcp', enabled: true }]
  }))
  await page.route(url => url.pathname === '/api/tags', route => route.fulfill({
    json: [1, 2, 3].map(index => ({
      id: 8400 + index, name: `出口压力通道${index}`, deviceId: 8301, unit: 'kPa',
      dataType: 'Float', kind: 'Physical', address: index, area: 'HoldingRegister', enabled: true
    }))
  }))

  // 768 及以下解除了右侧固定操作列，操作按钮会被滚出视野；
  // 而 Element Plus 在这套表格里既不显示原生滚动条、模拟滚动条也是 0×0。
  await page.setViewportSize({ width: 768, height: 900 })
  await page.goto('/config/tags')
  await expect(page.locator('.el-table').first()).toHaveClass(/el-table--scrollable-x/)
  await expect.poll(async () => page.evaluate(() =>
    getComputedStyle(document.querySelector('.el-table')!, '::after').content !== 'none'
  )).toBe(true)

  // 滚到最右后不再有「右边还有内容」的意思，提示要消失
  await page.locator('.el-table .el-scrollbar__wrap').first().evaluate(element => { element.scrollLeft = element.scrollWidth })
  await expect.poll(async () => page.evaluate(() =>
    getComputedStyle(document.querySelector('.el-table')!, '::after').content
  )).toBe('none')
})

test('宽屏表格不出现可滚动提示', async ({ page, request }) => {
  await signInAs(page, request)
  await mockApiFallback(page)
  await page.route(url => url.pathname === '/api/health', route => route.fulfill({
    json: {
      ...partialHealth,
      devices: [],
      disk: { root: '/', totalGb: 100, freeGb: 60 },
      storage: { configDb: 'config.db', configDbBytes: 1, historyBytes: 1 },
      warnings: [], historyCapacity: null, historyQueries: null, ha: null
    }
  }))
  await page.route(url => url.pathname === '/api/tags', route => route.fulfill({
    json: [{ id: 8401, name: '出口压力', deviceId: 8301, unit: 'kPa', address: 1, area: 'HoldingRegister' }]
  }))

  await page.setViewportSize({ width: 1440, height: 900 })
  await page.goto('/config/tags')
  await expect(page.locator('.el-table').first()).not.toHaveClass(/el-table--scrollable-x/)
  expect(await page.evaluate(() =>
    getComputedStyle(document.querySelector('.el-table')!, '::after').content
  )).toBe('none')
})
