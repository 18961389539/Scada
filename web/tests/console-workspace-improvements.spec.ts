import { test, expect, type Page, type APIRequestContext } from '@playwright/test'

async function fixture(page: Page, request: APIRequestContext) {
  const response = await request.post('/api/auth/login', { data: { userName: 'admin', password: 'admin123' } })
  expect(response.ok()).toBeTruthy()
  const auth = await response.json()
  await page.addInitScript(({ token, user }) => {
    localStorage.setItem('scada_token', token)
    localStorage.setItem('scada_user', JSON.stringify(user))
    localStorage.setItem('scada_locale', 'zh-CN')
  }, auth)
  const layout = { id: 920001, name: '工作区验收', revision: 1, updatedTs: Date.now(), updatedBy: 'admin', document: { monitors: [
    { name: '主屏', rows: 12, columns: 12, regions: [
      { id: 'banner', kind: 'alarmBanner', row: 1, col: 1, rowSpan: 1, colSpan: 12 },
      { id: 'main', kind: 'screen', screenId: 920001, row: 2, col: 1, rowSpan: 11, colSpan: 12 }
    ] }, { name: '副屏', rows: 12, columns: 12, regions: [{ id: 'main', kind: 'screen', screenId: 920001, row: 1, col: 1, rowSpan: 12, colSpan: 12 }] }
  ] } }
  await page.route(url => url.pathname === '/api/hmi/layouts', route => route.fulfill({ json: [layout, { ...layout, id: 920002, name: '另一工作区' }] }))
  await page.route(url => url.pathname === '/api/screens', route => route.fulfill({ json: [920001, 920002, 920003].map(id => ({ id, name: `工作画面 ${id}`, revision: 1, publishedRevision: 1, hasDraftChanges: false, updatedTs: Date.now(), updatedBy: 'admin' })) }))
  await page.route(/\/api\/screens\/92000[123](\?|$)/, route => {
    const id = Number(new URL(route.request().url()).pathname.split('/').at(-1))
    const node = (key: string, target: number, x: number) => ({ id: key, type: 'button', x, y: 30, width: 180, height: 70, rotation: 0, text: key, color: '#7394b8', activeColor: '#34d399', unit: '', decimals: 1, min: 0, max: 100, activeThreshold: .5, tagId: null, action: { type: 'popup', screenId: target, tagId: null, value: 0 } })
    return route.fulfill({ json: { id, name: `工作画面 ${id}`, revision: 1, publishedRevision: 1, document: { schemaVersion: 1, width: 800, height: 500, background: '#101b2d', nodes: [node('open-panel', 920002, 30), node('second-panel', 920003, 300)] } } })
  })
  await page.route(url => url.pathname === '/api/alarms', route => route.fulfill({ json: [{ id: 920001, tagId: 920001, level: 'Critical', startTs: Date.now() - 2000, endTs: null, ackTs: null, message: '工作区关键报警' }] }))
  await page.route(url => url.pathname === '/api/alarms/summary', route => route.fulfill({ json: { unack: 1, acked: 0, rtnUnack: 0, suppressed: 0, shelved: 0, flood: false } }))
  return layout
}

test('panels deduplicate, minimize, dock, arrange and close without obscuring the alarm header', async ({ page, request }) => {
  await fixture(page, request)
  await page.goto('/console/920001')
  const main = page.locator('[data-test="region-main"]')
  await main.locator('[data-node-id="open-panel"]').click()
  const panel = page.locator('[data-test="faceplate"]')
  await expect(panel).toHaveCount(1)
  await panel.locator('[data-test="faceplate-minimize"]').click()
  await expect(panel).toBeHidden()
  await main.locator('[data-node-id="open-panel"]').click()
  await expect(panel).toHaveCount(1)
  await expect(panel).toBeVisible()
  await panel.locator('[data-node-id="second-panel"]').click()
  await expect(panel).toHaveCount(2)
  const last = panel.last()
  await last.locator('[data-test="faceplate-actions"]').click()
  await page.getByRole('menuitem', { name: '停靠右侧', exact: true }).click()
  await expect(last).toHaveClass(/is-docked/)
  await page.locator('[data-test="faceplate-arrange"]').click()
  await expect(last).not.toHaveClass(/is-docked/)
  const bounds = (await last.boundingBox())!
  await page.mouse.move(bounds.x + 50, bounds.y + 12)
  await page.mouse.down(); await page.mouse.move(20, 0); await page.mouse.up()
  const alarm = (await page.locator('[data-test="alarm-banner"]').boundingBox())!
  const moved = (await last.boundingBox())!
  expect(moved.y).toBeGreaterThanOrEqual(alarm.y + alarm.height)
  expect(await page.locator('[data-test="alarm-banner"]').evaluate(element => {
    const rect = element.getBoundingClientRect()
    return element.contains(document.elementFromPoint(rect.x + rect.width / 2, rect.y + rect.height / 2))
  })).toBe(true)
  await page.locator('[data-test="faceplate-close-all"]').click()
  await expect(panel).toHaveCount(0)
  await expect(page.locator('[data-test="faceplate-tray"]')).toHaveCount(0)
  await page.evaluate(() => {
    window.open = (url) => {
      Object.defineProperty(window, '__detachedScreenUrl', { configurable: true, value: String(url) })
      return { moveTo() {}, resizeTo() {} } as unknown as Window
    }
  })
  await page.locator('[data-test="console-monitor-mode"]').click()
  await page.locator('[data-test="region-window-main"]').click()
  await page.locator('[data-test="monitor-open"]').click()
  expect(await page.evaluate(() => (window as unknown as { __detachedScreenUrl: string }).__detachedScreenUrl)).toBe('/kiosk/screens/920001?monitorOnly=1')
  await page.locator('[data-test="console-monitor"]').getByText('副屏', { exact: true }).click()
  await expect(page.locator('[data-test="alarm-banner"]')).toContainText('工作区关键报警')
})

test('floating and docked panels stay below the alarm and inside the workspace on narrow screens', async ({ page, request }, testInfo) => {
  await fixture(page, request)
  for (const width of [1600, 390, 320]) {
    await page.setViewportSize({ width, height: 900 })
    await page.goto('/console/920001')
    await page.locator('[data-test="region-main"] [data-node-id="open-panel"]').click()
    const panel = page.locator('[data-test="faceplate"]')
    const workspace = (await page.locator('[data-test="console-workspace"]').boundingBox())!
    await expect(panel).toBeVisible()
    await expect.poll(async () => (await panel.boundingBox())!.y).toBeGreaterThanOrEqual(workspace.y)
    const bounds = (await panel.boundingBox())!
    expect(bounds.x).toBeGreaterThanOrEqual(0)
    expect(bounds.x + bounds.width).toBeLessThanOrEqual(width)
    expect(bounds.y + bounds.height).toBeLessThanOrEqual(workspace.y + workspace.height)
    expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(width)
    await page.screenshot({ path: testInfo.outputPath(`console-workspace-${width}.png`), animations: 'disabled' })
    await panel.locator('[data-test="faceplate-minimize"]').click()
    await page.locator('[data-test="faceplate-restore-920002"]').click()
    await expect(panel).toBeVisible()
    await panel.locator('[data-test="faceplate-close"]').click()
  }
})

test('a late display permission response cannot reopen a detached screen from the previous layout', async ({ page, request }) => {
  await fixture(page, request)
  await page.addInitScript(() => {
    Object.defineProperty(window, 'getScreenDetails', { value: () => new Promise(resolve => {
      Object.defineProperty(window, '__releaseDisplays', { configurable: true, value: () => resolve({ screens: [{ label: '测试屏', availLeft: 0, availTop: 0, availWidth: 1600, availHeight: 900 }] }) })
    }) })
  })
  await page.goto('/console/920001')
  await page.locator('[data-test="region-window-main"]').click()
  await page.waitForFunction(() => typeof (window as unknown as { __releaseDisplays?: () => void }).__releaseDisplays === 'function')
  await page.locator('[data-test="console-layout"]').click()
  await page.getByRole('option', { name: '另一工作区', exact: true }).click()
  await expect(page.locator('.console-title')).toHaveText('另一工作区')
  await page.evaluate(() => (window as unknown as { __releaseDisplays: () => void }).__releaseDisplays())
  await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))))
  await expect(page.getByRole('dialog', { name: '选择显示器', exact: true })).not.toBeVisible()
})

test('an idle floating panel does not keep triggering resize renders', async ({ page, request }) => {
  await fixture(page, request)
  await page.addInitScript(() => {
    const NativeObserver = window.ResizeObserver
    let callbacks = 0
    Object.defineProperty(window, '__panelResizeCallbacks', { get: () => callbacks })
    window.ResizeObserver = class extends NativeObserver {
      constructor(callback: ResizeObserverCallback) {
        super((entries, observer) => { if (entries.some(entry => entry.target.classList.contains('faceplate'))) callbacks++; callback(entries, observer) })
      }
    }
  })
  await page.goto('/console/920001')
  await page.locator('[data-test="region-main"] [data-node-id="open-panel"]').click()
  await expect(page.locator('[data-test="faceplate"]')).toBeVisible()
  const extraCallbacks = await page.evaluate(async () => {
    const frames = async () => { for (let frame = 0; frame < 20; frame++) await new Promise(resolve => requestAnimationFrame(resolve)) }
    await frames()
    const before = (window as unknown as { __panelResizeCallbacks: number }).__panelResizeCallbacks
    await frames()
    return (window as unknown as { __panelResizeCallbacks: number }).__panelResizeCallbacks - before
  })
  expect(extraCallbacks).toBeLessThan(5)
})

test('a delayed workstation resolution cannot overwrite a newer local binding', async ({ page, request }) => {
  const layout = await fixture(page, request)
  await page.addInitScript(() => { localStorage.setItem('scada_workstation_id', '1'); localStorage.setItem('scada_workstation', 'StationA') })
  let release!: () => void, requested = false
  const held = new Promise<void>(resolve => { release = resolve })
  await page.route(url => url.pathname === '/api/hmi/workstations/resolve', async route => {
    const id = Number(new URL(route.request().url()).searchParams.get('id'))
    if (id === 1) { requested = true; await held }
    await route.fulfill({ json: { workstation: { id, name: id === 1 ? 'StationA' : 'StationB', layoutId: layout.id, allowControl: false }, layout } })
  })
  await page.goto('/console')
  await expect.poll(() => requested).toBe(true)
  await page.evaluate(() => {
    localStorage.setItem('scada_workstation_id', '2'); localStorage.setItem('scada_workstation', 'StationB')
    window.dispatchEvent(new StorageEvent('storage', { key: 'scada_workstation_id', newValue: '2' }))
  })
  await expect(page.locator('[data-test="console-station"]')).toContainText('StationB')
  release()
  await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))))
  expect(await page.evaluate(() => localStorage.getItem('scada_workstation_id'))).toBe('2')
  expect(await page.evaluate(() => localStorage.getItem('scada_workstation'))).toBe('StationB')
  await expect(page.locator('[data-test="console-station"]')).toContainText('StationB')
})
