import { test, expect, type APIRequestContext, type Page } from '@playwright/test'

async function login(request: APIRequestContext) {
  const r = await request.post('/api/auth/login', { data: { userName: 'admin', password: 'admin123' } })
  expect(r.ok(), await r.text()).toBeTruthy()
  return (await r.json()) as { token: string }
}

async function prepareTrends(page: Page, request: APIRequestContext) {
  const session = await login(request)
  await page.addInitScript(token => { localStorage.setItem('scada_token', token); localStorage.removeItem('scada_user') }, session.token)
  const state = { failCatalog: true, catalogCalls: 0, historyTags: [] as string[] }
  await page.route(url => url.pathname === '/api/devices', route => route.fulfill({ json: [{ id: 8301, name: '趋势测试设备', areaId: 1 }] }))
  await page.route(url => url.pathname === '/api/tags', route => {
    state.catalogCalls++
    return route.fulfill({ status: state.failCatalog ? 500 : 200, json: state.failCatalog ? { message: '测点目录暂不可用' } : [{ id: 8401, name: '出口压力', deviceId: 8301 }, { id: 8402, name: '入口温度', deviceId: 8301 }] })
  })
  await page.route(url => url.pathname === '/api/history', route => {
    const params = new URL(route.request().url()).searchParams
    const ids = params.get('tagIds') ?? ''
    state.historyTags.push(ids)
    return route.fulfill({ json: { resolution: 'Raw', series: ids.split(',').map(Number).map(tagId => ({ tagId, points: [{ ts: Number(params.get('from')) + 1000, value: tagId, quality: 'Good' }] })) } })
  })
  await page.route(url => url.pathname === '/api/history/analysis', route => route.fulfill({ json: { tags: [] } }))
  return state
}

test('other pages review retries a failed trend catalog and preserves the linked tag', async ({ page, request }) => {
  const state = await prepareTrends(page, request)
  await page.goto('/trends?tagId=8402')
  await expect(page.locator('[data-test="trend-catalog-error"]')).toContainText('测点目录暂不可用')
  await expect(page.locator('[data-test="chart-empty"]')).toContainText('测点目录暂不可用')
  expect(state.historyTags).toEqual([])
  state.failCatalog = false
  await page.locator('[data-test="trend-catalog-retry"]').click()
  await expect(page.locator('[data-test="trend-catalog-error"]')).toHaveCount(0)
  await expect(page.locator('[data-test="selected-count"]')).toContainText('已选 1')
  await expect(page.locator('[data-test="tag-picker"]')).toContainText('入口温度')
  await expect(page.locator('[data-test="point-count"]')).toContainText('1')
  await expect(page.locator('[data-test="trend-export"]')).toBeEnabled()
  expect(state.catalogCalls).toBe(2)
  expect(state.historyTags).toEqual(['8402'])
})

for (const outcome of ['success', 'failure'] as const) {
  test(`other pages review ignores a late trend ${outcome} after replacing the selected tag`, async ({ page, request }) => {
    const state = await prepareTrends(page, request)
    state.failCatalog = false
    await page.goto('/trends?tagId=8401')
    await expect(page.locator('[data-test="trend-export"]')).toBeEnabled()
    let release!: () => void
    const pending = new Promise<void>(resolve => { release = resolve })
    await page.route(url => url.pathname === '/api/history', async route => {
      const params = new URL(route.request().url()).searchParams
      if (params.get('tagIds') === '8401') {
        await pending
        return route.fulfill(outcome === 'failure' ? { status: 500, json: { message: '旧趋势读取失败' } } : { json: { resolution: 'Raw', series: [{ tagId: 8401, points: [{ ts: Number(params.get('from')) + 1000, value: 111, quality: 'Good' }] }] } })
      }
      return route.fulfill({ json: { resolution: 'Raw', series: [{ tagId: 8402, points: [{ ts: Number(params.get('from')) + 1000, value: 222, quality: 'Good' }] }] } })
    })
    const started = page.waitForRequest(request => new URL(request.url()).pathname === '/api/history')
    await page.getByRole('button', { name: '查询', exact: true }).click(); await started
    try {
      await page.locator('[data-test="clear-tags"]').click()
      await expect(page.locator('[data-test="trend-export"]')).toBeDisabled()
      await page.locator('[data-test="tag-picker"]').click()
      await page.getByRole('option', { name: '趋势测试设备 / 入口温度', exact: true }).click()
      await page.keyboard.press('Escape')
      await page.getByRole('button', { name: '查询', exact: true }).click()
      await expect(page.locator('[data-test="trend-export"]')).toBeEnabled()
      const done = page.waitForResponse(response => new URL(response.url()).pathname === '/api/history' && new URL(response.url()).searchParams.get('tagIds') === '8401')
      release(); await (await done).finished()
      await page.evaluate(() => new Promise(requestAnimationFrame))
      await expect(page.locator('[data-test="trend-query-error"]')).toHaveCount(0)
      const download = page.waitForEvent('download')
      await page.locator('[data-test="trend-export"]').click()
      const stream = await (await download).createReadStream(), chunks: Buffer[] = []
      for await (const chunk of stream!) chunks.push(Buffer.from(chunk))
      const csv = Buffer.concat(chunks).toString('utf8')
      expect(csv).toContain('入口温度'); expect(csv).toContain('222')
      expect(csv).not.toContain('出口压力'); expect(csv).not.toContain('111')
    } finally { release() }
  })
}

test('other pages review keeps trend retry and recovered charts usable on narrow screens', async ({ page, request }, testInfo) => {
  const state = await prepareTrends(page, request)
  for (const width of [1600, 390, 320]) {
    state.failCatalog = true
    await page.setViewportSize({ width, height: 900 })
    await page.goto('/trends?tagId=8402')
    await page.reload()
    await expect(page.locator('[data-test="trend-catalog-retry"]')).toBeVisible()
    const picker = await page.locator('[data-test="tag-picker"]').boundingBox()
    expect(picker!.x).toBeGreaterThanOrEqual(0)
    expect(picker!.x + picker!.width).toBeLessThanOrEqual(width)
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth + 1)).toBe(true)
    state.failCatalog = false
    await page.locator('[data-test="trend-catalog-retry"]').click()
    await expect(page.locator('[data-test="trend-export"]')).toBeEnabled()
    const chart = await page.locator('[data-test="trend-chart"]').boundingBox()
    expect(chart!.width).toBeGreaterThan(100); expect(chart!.height).toBeGreaterThanOrEqual(300)
    await expect.poll(() => page.locator('[data-test="trend-chart"] canvas').first().evaluate(element => {
      const canvas = element as HTMLCanvasElement, context = canvas.getContext('2d')!
      const scale = canvas.width / canvas.getBoundingClientRect().width
      const data = context.getImageData(Math.round(54 * scale), Math.round(44 * scale), Math.floor(canvas.width - 80 * scale), Math.floor(canvas.height - 110 * scale)).data
      let points = 0
      for (let index = 0; index < data.length; index += 4) {
        if (data[index]! < 70 && data[index + 1]! > 60 && data[index + 1]! < 150 && data[index + 2]! > 150 && data[index + 3]! > 100) points++
      }
      return points
    })).toBeGreaterThan(3)
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth + 1)).toBe(true)
    await page.screenshot({ path: testInfo.outputPath(`trends-${width}.png`), fullPage: true, animations: 'disabled' })
  }
})

// 趋势页易用性：空状态区分「没选点」与「没数据」、预置范围高亮、已选计数与清空、自动刷新开关。
test('trends usability: empty hints, preset highlight, selected count and auto refresh', async ({ page, request }) => {
  test.setTimeout(120000)
  const session = await login(request)
  const h = { Authorization: `Bearer ${session.token}` }
  const suffix = `${Date.now()}`
  const post = async (url: string, data: unknown) => {
    const r = await request.post(url, { headers: h, data })
    expect(r.ok(), await r.text()).toBeTruthy()
    return r.json()
  }

  // 设备停用 → 不采集 → 区间内无历史数据，正好验证空状态文案
  const device = await post('/api/devices', {
    name: `UX-TR-${suffix}`, protocol: 'ModbusTcp', host: '127.0.0.1', port: 1502,
    stationNumber: 1, byteOrder: 'ABCD', pollIntervalMs: 1000, timeoutMs: 3000, enabled: false, areaId: 1
  })
  const tag = await post('/api/tags', {
    deviceId: device.id, name: `UX-TR-A-${suffix}`, area: 'HoldingRegister', address: 10,
    dataType: 'Float', scale: 1, offset: 0, writable: false, archiveMode: 'OnChange', deadband: 0, archivePeriodSec: 600
  })

  await page.goto('/login')
  await page.getByPlaceholder(/用户名/).fill('admin')
  await page.getByPlaceholder(/密码/).fill('admin123')
  await page.keyboard.press('Enter')
  await expect(page).toHaveURL(/overview/)
  await page.goto('/trends')

  // 清空默认选择：此时应提示「先选择测点」而不是空白
  const empty = page.locator('[data-test="chart-empty"]')
  await page.locator('[data-test="clear-tags"]').click()
  await expect(page.locator('[data-test="selected-count"]')).toContainText('已选 0')
  await expect(empty).toContainText('请先选择测点')

  // 选中刚建的测点再查询：有测点但无数据，文案应切换
  await page.locator('[data-test="tag-picker"]').click()
  await page.getByRole('option', { name: new RegExp(tag.name) }).click()
  await page.keyboard.press('Escape')
  await expect(page.locator('[data-test="selected-count"]')).toContainText('已选 1')
  await page.getByRole('button', { name: '查询', exact: true }).click()
  await expect(empty).toContainText('该时间段没有历史数据')

  // 预置范围有选中态：点「24 小时」后按钮变主色
  const preset24 = page.getByRole('button', { name: '24 小时', exact: true })
  await preset24.click()
  await expect(preset24).toHaveClass(/el-button--primary/)

  // 自动刷新默认关闭，可切换
  const auto = page.locator('[data-test="auto-refresh"]')
  await expect(auto).not.toHaveClass(/is-checked/)
  await auto.click()
  await expect(auto).toHaveClass(/is-checked/)

  await page.route((url) => url.pathname === '/api/history', (route) => route.fulfill({
    status: 500, contentType: 'application/json', body: JSON.stringify({ message: '历史库不可用' })
  }))
  await page.getByRole('button', { name: '查询', exact: true }).click()
  await expect(empty).toContainText('历史库不可用')
  await expect(empty).not.toContainText('该时间段没有历史数据')
})
