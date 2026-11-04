import { test, expect, type APIRequestContext, type Page } from './fixtures'
import type { ScreenDocument, ScreenNode } from '../src/screens/model'
import { openRuntimeMenu, refreshRuntime } from './runtime-menu'

async function login(request: APIRequestContext) {
  const response = await request.post('/api/auth/login', { data: { userName: 'admin', password: 'admin123' } })
  expect(response.ok(), await response.text()).toBeTruthy()
  const auth = await response.json()
  return { ...auth, headers: { Authorization: `Bearer ${auth.token}` } }
}

async function sign(page: Page, auth: { token: string; user: unknown }): Promise<void> {
  await page.addInitScript(({ token, user }) => {
    localStorage.setItem('scada_token', token)
    localStorage.setItem('scada_user', JSON.stringify(user))
    localStorage.setItem('scada_locale', 'zh-CN')
  }, auth)
}

function node(id: string, type: ScreenNode['type'], extras: Partial<ScreenNode> = {}): ScreenNode {
  return {
    id, type, x: 40, y: 40, width: 220, height: 72, rotation: 0, text: id, color: '#7394b8', activeColor: '#34d399',
    unit: '', decimals: 1, min: 0, max: 100, activeThreshold: .5, tagId: null,
    action: { type: 'none', tagId: null, screenId: null, value: 1 }, ...extras
  }
}

async function create(request: APIRequestContext, headers: Record<string, string>, name: string, document?: ScreenDocument) {
  const created = await request.post('/api/screens', { headers, data: { name } })
  expect(created.status()).toBe(201)
  const screen = await created.json()
  const body = document ?? { schemaVersion: 1, width: 1200, height: 720, background: '#101b2d', nodes: [node('label', 'text', { text: name, width: 400 })] }
  const saved = await request.put(`/api/screens/${screen.id}`, { headers, data: { name, revision: screen.revision, document: body } })
  expect(saved.ok(), await saved.text()).toBeTruthy()
  const result = await saved.json()
  const published = await request.post(`/api/screens/${screen.id}/publish`, { headers, data: { revision: result.revision } })
  expect(published.ok(), await published.text()).toBeTruthy()
  return { id: screen.id as number, name }
}

test('screen catalog pins favorites, remembers recent screens, and names the published layout', async ({ page, request }) => {
  const auth = await login(request)
  const suffix = `${Date.now()}`
  const older = await create(request, auth.headers, `较早画面${suffix}`)
  const newer = await create(request, auth.headers, `较新画面${suffix}`)
  const layoutName = `已发布布局${suffix}`
  const layout = await request.post('/api/hmi/layouts', { headers: auth.headers, data: {
    name: layoutName,
    document: { monitors: [{ name: '主屏', columns: 12, rows: 12, regions: [{ id: 'main', kind: 'screen', screenId: newer.id, row: 1, col: 1, rowSpan: 12, colSpan: 12 }] }] }
  } })
  expect(layout.ok(), await layout.text()).toBeTruthy()
  const createdLayout = await layout.json()
  const publication = await request.post(`/api/hmi/layouts/${createdLayout.id}/publish`, { headers: auth.headers, data: { revision: createdLayout.revision, note: '画面目录' } })
  expect(publication.ok(), await publication.text()).toBeTruthy()
  await sign(page, auth)
  await page.goto('/screens')
  await page.locator('[data-test="screens-search"]').fill(suffix)
  await expect(page.locator('.screen-card h3')).toHaveText([newer.name, older.name])
  await page.locator(`[data-test="screen-favorite-${older.id}"]`).click()
  await expect(page.locator('[data-test="screens-section-favorites"]')).toHaveText('常用')
  await expect(page.locator('.screen-card h3')).toHaveText([older.name, newer.name])
  await page.reload()
  await page.locator('[data-test="screens-search"]').fill(suffix)
  await expect(page.locator('.screen-card h3')).toHaveText([older.name, newer.name])
  await expect(page.locator(`[data-test="screen-card-${newer.id}"] [data-test="screen-layouts"]`)).toContainText(layoutName)
  await page.locator(`[data-test="screen-run-${newer.id}"]`).click()
  await expect(page.locator('.runtime-toolbar > strong')).toHaveText(newer.name)
  await page.locator('[data-test="screen-back"]').click()
  await page.locator('[data-test="screens-search"]').fill(suffix)
  await expect(page.locator('[data-test="screens-section-recent"]')).toHaveText('最近打开')
  await expect(page.locator('.screen-card h3')).toHaveText([older.name, newer.name])
})

test('runtime quality count, named popup, trail, and remembered monitor', async ({ page, request }) => {
  const auth = await login(request)
  const suffix = `${Date.now()}`
  const device = await (await request.post('/api/devices', { headers: auth.headers, data: { name: `q-${suffix}`, protocol: 'ModbusTcp', host: '127.0.0.1', port: 1502, stationNumber: 1, byteOrder: 'ABCD', pollIntervalMs: 1000, timeoutMs: 1000, enabled: false } })).json()
  const tag = await (await request.post('/api/tags', { headers: auth.headers, data: { deviceId: device.id, name: `质量点${suffix}`, area: 'HoldingRegister', address: 0, dataType: 'Float', scale: 1, offset: 0, writable: false, archiveMode: 'None', deadband: 0, archivePeriodSec: 600 } })).json()
  const target = await create(request, auth.headers, `弹窗${suffix}`)
  const source = await create(request, auth.headers, `入口${suffix}`, {
    schemaVersion: 1, width: 1200, height: 720, background: '#101b2d', nodes: [
      node('reading', 'value', { tagId: tag.id, x: 40, y: 40, text: '质量' }),
      node('open-popup', 'button', { x: 40, y: 160, text: '打开弹窗', action: { type: 'popup', screenId: target.id, tagId: null, value: 1 } }),
      node('jump', 'button', { x: 300, y: 160, text: '进入下一画面', action: { type: 'navigate', screenId: target.id, tagId: null, value: 1 } })
    ]
  })
  let quality: 'Bad' | 'Good' = 'Bad'
  let stale = false
  await page.route(url => url.pathname === '/api/realtime', route => route.fulfill({
    json: [{ tagId: tag.id, value: 1, quality, ts: Date.now() - (stale ? 60000 : 0) }]
  }))
  await page.addInitScript(({ token, user }) => {
    localStorage.setItem('scada_token', token)
    localStorage.setItem('scada_user', JSON.stringify(user))
    localStorage.setItem('scada_locale', 'zh-CN')
    Object.defineProperty(window, 'getScreenDetails', {
      configurable: true,
      value: async () => ({ screens: [
        { label: '主屏', availLeft: 0, availTop: 0, availWidth: 1280, availHeight: 800, isPrimary: true },
        { label: '副屏', availLeft: 1920, availTop: 0, availWidth: 1920, availHeight: 1080, isPrimary: false }
      ] })
    })
  }, auth)
  await page.goto(`/screens/${source.id}`)
  await expect(page.locator('[data-test="runtime-quality"]')).toContainText('1 个测点')
  quality = 'Good'
  stale = true
  await refreshRuntime(page)
  await expect(page.locator('[data-test="runtime-quality"]')).toContainText('1 个测点')
  stale = false
  await refreshRuntime(page)
  await expect(page.locator('[data-test="runtime-quality"]')).toHaveCount(0)

  await page.locator('[data-node-id="open-popup"]').click()
  const popup = page.getByRole('dialog')
  await expect(popup.locator('[data-test="runtime-popup-title"]')).toHaveText(target.name)
  await expect(page.locator('[data-node-id="open-popup"]')).toBeVisible()
  await expect(page.locator('.el-overlay:visible')).toHaveCount(0)
  const before = (await popup.boundingBox())!
  const title = (await popup.locator('[data-test="runtime-popup-title"]').boundingBox())!
  await page.mouse.move(title.x + 24, title.y + 8)
  await page.mouse.down()
  await page.mouse.move(title.x - 80, title.y + 36, { steps: 6 })
  await page.mouse.up()
  const after = (await popup.boundingBox())!
  expect(after.x).toBeLessThan(before.x - 20)
  await popup.getByRole('button', { name: '关闭此对话框', exact: true }).click()
  await expect(popup).toBeHidden()

  await page.locator('[data-node-id="jump"]').click()
  await expect(page.locator('[data-test="screen-trail"]')).toContainText(source.name)
  await expect(page.locator('[data-test="screen-trail-current"]')).toHaveText(target.name)
  await page.goto(`/screens/${target.id}?trail=999999`)
  await expect(page.locator('[data-test="screen-trail"]')).toContainText('#999999')
  await expect(page.locator('[data-test="screen-trail-current"]')).toHaveText(target.name)

  await page.goto(`/screens/${source.id}`)
  await page.evaluate(() => {
    const calls: string[] = []
    ;(window as unknown as { __opened: string[] }).__opened = calls
    window.open = ((url?: string | URL, _name?: string, features?: string) => {
      calls.push(String(features ?? ''))
      return { moveTo() {}, resizeTo() {}, closed: false } as Window
    }) as typeof window.open
  })
  await openRuntimeMenu(page)
  await page.locator('[data-test="screen-new-window"]').click()
  const dialog = page.getByRole('dialog', { name: '选择显示器' })
  await expect(dialog).toBeVisible()
  await expect(dialog.getByRole('radio', { name: /副屏/ })).toBeChecked()
  await page.locator('[data-test="screen-open-window"]').click()
  await expect(dialog).toBeHidden()
  await expect.poll(() => page.evaluate(() => (window as unknown as { __opened: string[] }).__opened.length)).toBe(1)
  await openRuntimeMenu(page)
  await page.locator('[data-test="screen-new-window"]').click()
  await expect(dialog).toBeHidden()
  await expect.poll(() => page.evaluate(() => (window as unknown as { __opened: string[] }).__opened.length)).toBe(2)
  await expect.poll(() => page.evaluate(() => (window as unknown as { __opened: string[] }).__opened.at(-1) ?? '')).toContain('left=1920')
  await openRuntimeMenu(page)
  await page.locator('[data-test="screen-choose-monitor"]').click()
  await expect(dialog).toBeVisible()
})
