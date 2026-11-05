import { revealConsoleControl, test, expect, type Page, type APIRequestContext } from './fixtures'

const screenDocument = (id: number, name: string, nodes: object[] = []) => ({
  id, name, revision: 1, publishedRevision: 1, hasDraftChanges: false, updatedTs: 1, updatedBy: 'admin',
  document: { schemaVersion: 1, width: 800, height: 500, background: '#101b2d', nodes }
})

const button = (id: string, text: string, type: 'navigate' | 'popup', screenId: number, x: number) => ({
  id, type: 'button', x, y: 40, width: 160, height: 48, rotation: 0, text, color: '#7394b8', activeColor: '#34d399',
  unit: '', decimals: 1, min: 0, max: 100, activeThreshold: .5, tagId: null,
  action: { type, screenId, tagId: null, value: 0 }
})

async function signIn(page: Page, request: APIRequestContext) {
  const response = await request.post('/api/auth/login', { data: { userName: 'admin', password: 'admin123' } })
  expect(response.ok()).toBeTruthy()
  const auth = await response.json()
  await page.addInitScript(auth => {
    localStorage.setItem('scada_token', auth.token)
    localStorage.setItem('scada_user', JSON.stringify(auth.user))
    localStorage.setItem('scada_locale', 'zh-CN')
    localStorage.setItem('scada_workstation_id', '7')
    localStorage.setItem('scada_workstation', '一号工位')
  }, auth)
}

test('workbench chrome keeps the target, focus, monitor choice and control lock', async ({ page, request }) => {
  test.setTimeout(60000)
  await signIn(page, request)
  await page.addInitScript(() => {
    Object.defineProperty(window, 'getScreenDetails', { configurable: true, value: async () => { throw new Error('denied') } })
    window.open = () => ({ moveTo() {}, resizeTo() {} }) as unknown as Window
  })
  const region = (id: string, title: string, col: number) => ({ id, kind: 'screen', title, screenId: 930010, row: 2, col, rowSpan: 11, colSpan: 5 })
  const primary = {
    id: 930001, name: '工作区验收', revision: 1, updatedTs: 1, updatedBy: 'admin',
    document: { monitors: [
      { name: '主屏', columns: 12, rows: 12, regions: [
        { id: 'banner', kind: 'alarmBanner', row: 1, col: 1, rowSpan: 1, colSpan: 12 },
        { id: 'nav', kind: 'navigation', title: '目录', row: 2, col: 1, rowSpan: 11, colSpan: 2 },
        region('main', '主工艺区', 3), region('side', '侧区', 8)
      ] },
      { name: '副屏', columns: 12, rows: 12, regions: [region('main', '主工艺区', 1)] }
    ] }
  }
  const other = {
    ...primary, id: 930002, name: '另一工作区',
    document: { monitors: [
      { name: '甲', columns: 12, rows: 12, regions: [region('main', '主工艺区', 1)] },
      { name: '乙', columns: 12, rows: 12, regions: [region('main', '主工艺区', 1)] }
    ] }
  }
  await page.route(url => url.pathname === '/api/hmi/layouts', route => route.fulfill({ json: [primary, other] }))
  await page.route(url => url.pathname === '/api/hmi/workstations/resolve', route => route.fulfill({ json: {
    workstation: { id: 7, name: '一号工位', description: null, layoutId: 930001, allowControl: false, updatedTs: 1, updatedBy: 'admin' }, layout: null
  } }))
  await page.route(url => url.pathname === '/api/screens', route => route.fulfill({ json: [screenDocument(930010, '锅炉/总览')] }))
  await page.route(url => url.pathname === '/api/screens/930010', route => route.fulfill({ json: screenDocument(930010, '锅炉/总览') }))
  await page.route(url => url.pathname === '/api/alarms', route => route.fulfill({ json: [] }))
  await page.route(url => url.pathname === '/api/alarms/summary', route => route.fulfill({ json: { unack: 0, acked: 0, rtnUnack: 0, suppressed: 0, shelved: 0, flood: false } }))
  await page.goto('/console/930001')
  await expect(page.locator('[data-test="region-main"] [data-test="region-current-screen"]')).toContainText('主工艺区 · 锅炉/总览')
  await expect(page.locator('[data-test="directory-target"]')).toHaveText('将在「主工艺区」打开')
  await expect(page.locator('[data-test="region-active-name"]')).toHaveText('主工艺区')
  await page.locator('[data-test="region-side"]').click()
  await expect(page.locator('[data-test="directory-target"]')).toHaveText('将在「侧区」打开')
  await expect(page.locator('[data-test="region-active-name"]')).toHaveText('侧区')
  await page.locator('[data-test="region-focus-main"]').click()
  await expect(page.locator('[data-test="region-side"]')).toBeHidden()
  await expect(page.locator('[data-test="region-focus-main"]')).toContainText('回到分区')
  await page.locator('[data-test="region-focus-main"]').click()
  await expect(page.locator('[data-test="region-side"]')).toBeVisible()
  const chooseLayout = async (name: string) => {
    await (await revealConsoleControl(page, 'console-layout')).click()
    await page.getByRole('option', { name, exact: true }).evaluate(element => (element as HTMLElement).click())
  }
  await page.locator('[data-test="console-monitor"]').getByText('副屏', { exact: true }).click()
  await chooseLayout('另一工作区')
  await expect(page.locator('.console-title')).toHaveText('另一工作区')
  await expect(page.locator('[data-test="console-monitor"] .el-radio-button.is-active')).toHaveText('甲')
  await page.locator('[data-test="console-monitor"]').getByText('乙', { exact: true }).click()
  await chooseLayout('工作区验收')
  await expect(page.locator('[data-test="console-monitor"] .el-radio-button.is-active')).toHaveText('副屏')
  await chooseLayout('另一工作区')
  await expect(page.locator('[data-test="console-monitor"] .el-radio-button.is-active')).toHaveText('乙')
  await chooseLayout('工作区验收')
  await expect(page.locator('[data-test="console-control-lock"]')).toContainText('工位 一号工位 不允许控制')
  await (await revealConsoleControl(page, 'console-monitor-mode')).click()
  await expect(page.locator('.el-message-box')).toHaveCount(0)
  await expect(page.locator('[data-test="console-control-lock"]')).toContainText('仅监视')
  await (await revealConsoleControl(page, 'console-monitor-mode')).click()
  await page.locator('.el-message-box').getByRole('button', { name: '取消', exact: true }).click()
  await expect(page.locator('[data-test="console-control-lock"]')).toContainText('仅监视')
  await (await revealConsoleControl(page, 'console-monitor-mode')).click()
  await page.locator('.el-message-box').getByRole('button', { name: /确定|确认/ }).click()
  await expect(page.locator('[data-test="console-control-lock"]')).toContainText('不允许控制')
  await page.locator('[data-test="console-more"]').click()
  await expect(page.locator('[data-test="console-configure"]')).toBeVisible()
  await expect(page.locator('[data-test="console-fullscreen"]')).toBeVisible()
  await expect(page.locator('[data-test="console-overview"]')).toBeVisible()
  await page.locator('[data-test="console-open-all"]').click()
  const mapping = page.getByRole('dialog', { name: '显示器映射', exact: true })
  await expect(mapping.locator('[data-test="console-monitor-notice"]')).toContainText('没有获得显示器权限')
  await mapping.getByRole('button', { name: '取消', exact: true }).click()
  await page.evaluate(() => Object.defineProperty(window, 'getScreenDetails', { configurable: true, value: undefined }))
  await page.locator('[data-test="region-window-main"]').click()
  const detach = page.getByRole('dialog', { name: '选择显示器', exact: true })
  await expect(detach.locator('[data-test="console-monitor-notice"]')).toContainText('当前浏览器不能列出显示器')
  await expect(detach.locator('[data-test="monitor-open"]')).toBeVisible()
})

test('workbench alarms, panels and version apply keep the operator context', async ({ page, request }) => {
  test.setTimeout(90000)
  await signIn(page, request)
  const acked: number[] = []
  let silenced = 0
  let published = false
  const kept = { id: 'main', kind: 'screen', title: '主工艺区', screenId: 930101, row: 1, col: 1, rowSpan: 8, colSpan: 12 }
  const closed = { id: 'side', kind: 'screen', title: '将关闭区', screenId: 930101, row: 9, col: 1, rowSpan: 4, colSpan: 12 }
  const alarms = { id: 'alarms', kind: 'alarmList', title: '报警', row: 1, col: 1, rowSpan: 12, colSpan: 12 }
  const layout = (revision: number) => ({
    id: 930101, name: revision === 1 ? '工作区验收' : '工作区验收-新版', revision, updatedTs: 1, updatedBy: 'admin',
    document: { monitors: [{ name: '主屏', columns: 12, rows: 12, regions: revision === 1 ? [kept, closed] : [kept] }, { name: '报警屏', columns: 12, rows: 12, regions: [alarms] }] }
  })
  await page.route(url => url.pathname === '/api/hmi/layouts', route => route.fulfill({ json: [layout(published ? 2 : 1)] }))
  await page.route(url => url.pathname === '/api/hmi/workstations/resolve', route => route.fulfill({ json: {
    workstation: { id: 7, name: '一号工位', description: null, layoutId: 930101, allowControl: true, updatedTs: 1, updatedBy: 'admin' }, layout: null
  } }))
  const catalog = [
    screenDocument(930101, '总览', [button('go', '进入', 'navigate', 930102, 30)]),
    screenDocument(930102, '面板2', [button('go', '弹面板', 'popup', 930103, 30), button('pop', '连开', 'popup', 930201, 220)]),
    ...[930103, 930104, 930105, 930106].map((id, index) => screenDocument(id, `面板${index + 3}`, [button('go', '下一个', 'popup', id + 1, 30)])),
    ...[930201, 930202, 930203, 930204, 930205, 930206].map((id, index) => screenDocument(id, `连开${index + 1}`, [button('go', '下一个', 'popup', id + 1, 30)]))
  ]
  await page.route(url => url.pathname === '/api/screens', route => route.fulfill({ json: catalog }))
  await page.route(url => /^\/api\/screens\/930\d+$/.test(url.pathname), route => {
    const id = Number(new URL(route.request().url()).pathname.split('/').at(-1))
    const found = catalog.find(item => item.id === id)
    return found ? route.fulfill({ json: found }) : route.fulfill({ status: 404, json: {} })
  })
  const events = [
    { id: 41, tagId: 1, level: 'Critical', startTs: Date.now() - 3000, endTs: null, ackTs: null, message: '紧急停机' },
    { id: 42, tagId: 1, level: 'Warning', startTs: Date.now() - 2000, endTs: null, ackTs: null, message: '压力偏高' }
  ]
  await page.route(url => url.pathname === '/api/alarms', route => route.fulfill({ json: events }))
  await page.route(url => url.pathname === '/api/alarms/summary', route => route.fulfill({ json: { unack: 2, acked: 0, rtnUnack: 0, suppressed: 0, shelved: 0, flood: false } }))
  await page.route(/\/api\/alarms\/\d+\/ack$/, route => {
    acked.push(Number(new URL(route.request().url()).pathname.split('/').at(-2)))
    return route.fulfill({ json: {} })
  })
  await page.route(url => url.pathname === '/api/annunciators/silence-visible', route => route.fulfill({ json: { silenced, total: silenced } }))
  await page.goto('/console/930101?monitor=1')
  const list = page.locator('[data-test="alarm-list"]')
  await expect(list.locator('thead')).toBeVisible()
  await page.setViewportSize({ width: 600, height: 900 })
  await expect(list.locator('thead')).toBeHidden()
  const listBox = (await list.boundingBox())!
  const ackBox = (await list.locator('[data-alarm-id="41"] button').first().boundingBox())!
  expect(ackBox.x).toBeGreaterThanOrEqual(listBox.x - 1)
  expect(ackBox.x + ackBox.width).toBeLessThanOrEqual(listBox.x + listBox.width + 1)
  await page.setViewportSize({ width: 1600, height: 1000 })
  await expect(list.locator('thead')).toBeVisible()
  await page.locator('[data-test="alarm-banner-jump"]').click()
  await expect(list.locator('[data-alarm-id="41"] button').first()).toBeFocused()
  await list.locator('[data-test="alarm-ack-visible"]').click()
  await expect(page.locator('.el-message-box')).toContainText('1 条非紧急')
  await page.locator('.el-message-box').getByRole('button', { name: /确定|确认/ }).click()
  await expect.poll(() => acked.slice()).toEqual([42])
  silenced = 0
  await page.locator('[data-test="console-silence"]').click()
  await expect(page.locator('.el-message-box')).toContainText('不会确认报警')
  await page.locator('.el-message-box').getByRole('button', { name: /确定|确认/ }).click()
  await expect(page.locator('[data-test="console-silence-error"]')).toContainText('没有可消音的声光面板')
  await expect(page.locator('[data-test="console-silence-until"]')).toHaveCount(0)
  silenced = 2
  await page.locator('[data-test="console-silence"]').click()
  await page.locator('.el-message-box').getByRole('button', { name: /确定|确认/ }).click()
  await expect(page.locator('[data-test="console-silence-until"]')).toBeVisible()
  await expect(page.locator('[data-test="console-silence-error"]')).toHaveCount(0)
  await page.locator('[data-test="console-monitor"]').getByText('主屏', { exact: true }).click()
  const main = page.locator('[data-test="region-main"]')
  await main.locator('[data-node-id="go"]').click()
  await expect(main.locator('[data-test="region-current-screen"]')).toContainText('主工艺区 · 面板2')
  await main.locator('[data-node-id="go"]').click()
  const panel = page.locator('[data-test="faceplate"]').last()
  await expect(panel).toBeVisible()
  const before = (await panel.boundingBox())!
  const handle = panel.locator('[data-test="faceplate-resize"]')
  const origin = (await handle.boundingBox())!
  await handle.dispatchEvent('pointerdown', { button: 0, clientX: origin.x + 8, clientY: origin.y + 8 })
  await page.mouse.move(origin.x + 100, origin.y + 40, { steps: 6 })
  await page.mouse.up()
  await expect.poll(async () => (await panel.boundingBox())!.width).toBeGreaterThan(before.width + 40)
  const resized = (await panel.boundingBox())!
  await page.reload()
  await page.locator('[data-test="console-monitor"]').getByText('主屏', { exact: true }).click()
  await expect(page.locator('[data-test="region-main"] [data-test="region-current-screen"]')).toContainText('主工艺区 · 面板2')
  const restored = page.locator('[data-test="faceplate"]').last()
  await expect(restored).toBeVisible()
  expect((await restored.boundingBox())!.width).toBeGreaterThan(resized.width - 8)
  published = true
  await (await revealConsoleControl(page, 'console-refresh')).click()
  await expect(page.locator('[data-test="console-update"]')).toBeVisible()
  await page.locator('[data-test="console-update"] button').click()
  const confirm = page.locator('.el-message-box')
  await expect(confirm).toContainText('主工艺区')
  await expect(confirm).toContainText('将关闭区')
  await confirm.getByRole('button', { name: /确定|确认/ }).click()
  await expect(page.locator('.console-title')).toHaveText('工作区验收-新版')
  await expect(page.locator('[data-test="region-side"]')).toHaveCount(0)
  await expect(page.locator('[data-test="region-main"] [data-test="region-current-screen"]')).toContainText('主工艺区 · 面板2')
  await expect(page.locator('[data-test="faceplate"]')).toHaveCount(1)
  await page.locator('[data-test="faceplate-actions"]').click()
  await page.getByRole('menuitem', { name: '停靠右侧' }).click()
  await page.locator('[data-test="region-main"] [data-node-id="pop"]').click()
  await expect(page.locator('[data-test="faceplate"]')).toHaveCount(2)
  for (let count = 3; count <= 6; count++) {
    await page.locator('[data-test="faceplate"]').last().locator('[data-node-id="go"]').click()
    await expect(page.locator('[data-test="faceplate"]')).toHaveCount(count)
  }
  await page.locator('[data-test="faceplate"]').last().locator('[data-node-id="go"]').click()
  await expect(page.locator('[data-test="faceplate"]')).toHaveCount(6)
  await expect(page.locator('[data-test="console-client-error"]')).toContainText('最多同时打开 6 个面板窗口')
  await expect(page.locator('[data-test="faceplate-limit"]')).toContainText('连开3')
})
