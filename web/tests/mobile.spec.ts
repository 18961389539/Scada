import { test, expect, devices, type APIRequestContext, type Page } from '@playwright/test'

const { defaultBrowserType: _ignored, ...pixel } = devices['Pixel 7']
test.use({ ...pixel })

async function login(request: APIRequestContext) {
  const r = await request.post('/api/auth/login', { data: { userName: 'admin', password: 'admin123' } }); expect(r.ok()).toBeTruthy()
  const result = await r.json(); return { ...result, headers: { Authorization: `Bearer ${result.token}` } }
}
async function commandScreen(request: APIRequestContext, h: Record<string, string>) {
  const u = `${Date.now()}`.slice(-6)
  const device = await (await request.post('/api/devices', { headers: h, data: { name: `mob-${u}`, protocol: 'ModbusTcp', host: '127.0.0.1', port: 1502, stationNumber: 1, byteOrder: 'ABCD', pollIntervalMs: 1000, timeoutMs: 1000, enabled: false } })).json()
  const tag = await (await request.post('/api/tags', { headers: h, data: { deviceId: device.id, name: `手机泵${u}`, area: 'HoldingRegister', address: 0, dataType: 'Float', scale: 1, offset: 0, writable: true, archiveMode: 'None', deadband: 0, archivePeriodSec: 600, minValue: 0, maxValue: 50 } })).json()
  const s = await (await request.post('/api/screens', { headers: h, data: { name: `手机-${u}` } })).json()
  const nodes = [{ id: 'run', type: 'button', x: 600, y: 300, width: 240, height: 90, rotation: 0, text: '启动', color: '#7394b8', activeColor: '#34d399', unit: '', decimals: 1, min: 0, max: 100, activeThreshold: .5, tagId: tag.id, action: { type: 'command', tagId: tag.id, screenId: null, value: 20 } }]
  const put = await request.put(`/api/screens/${s.id}`, { headers: h, data: { name: `手机-${u}`, revision: s.revision, document: { schemaVersion: 1, width: 1600, height: 900, background: '#101b2d', nodes } } }); expect(put.ok(), await put.text()).toBeTruthy(); const saved = await put.json()
  const p = await request.post(`/api/screens/${s.id}/publish`, { headers: h, data: { revision: saved.revision } }); expect(p.ok(), await p.text()).toBeTruthy()
  return s.id as number
}
async function pinch(page: Page, from: number, to: number) {
  await page.locator('[data-test="runtime-stage"]').evaluate((el, [a, b]) => {
    const r = el.getBoundingClientRect(), cx = r.left + r.width / 2, cy = r.top + 100
    const touches = (d: number) => [new Touch({ identifier: 1, target: el, clientX: cx - d / 2, clientY: cy }), new Touch({ identifier: 2, target: el, clientX: cx + d / 2, clientY: cy })]
    const fire = (type: string, d: number) => el.dispatchEvent(new TouchEvent(type, { touches: type === 'touchend' ? [] : touches(d), changedTouches: touches(d), bubbles: true, cancelable: true }))
    fire('touchstart', a); for (let i = 1; i <= 5; i++) fire('touchmove', a + (b - a) * i / 5); fire('touchend', b)
  }, [from, to])
}

test('mobile shell: drawer navigation, scale-to-fit with pinch zoom, touch control through the control pipeline, server mobile policy', async ({ page, request }) => {
  const auth = await login(request), id = await commandScreen(request, auth.headers)
  await page.addInitScript(({ token, user }) => { localStorage.setItem('scada_token', token); localStorage.setItem('scada_user', JSON.stringify(user)) }, auth)
  await page.goto('/overview')
  await expect(page.locator('.aside').first()).toBeHidden()
  await page.locator('[data-test="mobile-menu"]').tap()
  await page.getByRole('menuitem', { name: '工艺画面', exact: true }).tap()
  await expect(page).toHaveURL('/screens')
  await expect(page.locator('.mobile-drawer')).toBeHidden()

  const headers: string[] = []
  page.on('request', r => { if (r.url().includes('/api/')) headers.push(r.headers()['x-scada-client'] ?? '') })
  await page.goto(`/screens/${id}`)
  const stage = page.locator('[data-test="runtime-stage"]')
  await expect(page.locator('[data-test="runtime-zoom"]')).toContainText('适应')
  // zoom computed 在 stage 尚未测量时返回默认 .75，直接读属性会拿到回退值
  await expect.poll(async () => Number(await stage.getAttribute('data-zoom')), { timeout: 10000 }).toBeLessThan(.3)
  const fit = Number(await stage.getAttribute('data-zoom'))
  const box = await page.locator('.screen-canvas').boundingBox(); expect(box!.width).toBeLessThanOrEqual(412)
  await pinch(page, 100, 300)
  await expect.poll(async () => Number(await stage.getAttribute('data-zoom'))).toBeGreaterThan(fit * 2.5)
  expect(headers.every(x => x === 'mobile')).toBeTruthy()

  // 允许手机控制（默认）：点按进入与桌面相同的确认 → 下发闭环
  await page.locator('[data-test="runtime-zoom"]').click(); await page.getByRole('option', { name: '适应' }).tap()
  await page.locator('[data-node-id="run"]').tap()
  await expect(page.getByRole('dialog').filter({ hasText: '确认' }).first()).toBeVisible()
  await page.keyboard.press('Escape')

  // 服务端策略不允许：界面只读并拦截，不发出控制请求
  await page.route('**/api/client-policy', r => r.fulfill({ json: { mobileControl: false } }))
  await page.reload()
  await expect(page.locator('[data-test="mobile-readonly"]')).toBeVisible()
  const actions: string[] = []; page.on('request', r => { if (r.url().includes('/actions/') || r.url().includes('/commands')) actions.push(r.url()) })
  await page.locator('[data-node-id="run"]').tap()
  await expect(page.getByText('手机端不允许下发控制')).toBeVisible()
  expect(actions).toHaveLength(0)
  await page.screenshot({ path: test.info().outputPath('mobile.png') })
})
