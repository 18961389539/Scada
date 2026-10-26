import { test, expect, type APIRequestContext } from '@playwright/test'

async function login(request: APIRequestContext) {
  const r = await request.post('/api/auth/login', { data: { userName: 'admin', password: 'admin123' } }); expect(r.ok()).toBeTruthy()
  const result = await r.json(); return { ...result, headers: { Authorization: `Bearer ${result.token}` } }
}
const node = (id: string, text: string, action: object, x = 40) => ({ id, type: 'button', x, y: 40, width: 200, height: 60, rotation: 0, text, color: '#7394b8', activeColor: '#34d399', unit: '', decimals: 1, min: 0, max: 100, activeThreshold: 0.5, tagId: null, action })
const none = { type: 'none', screenId: null, tagId: null, value: 0 }

test('multi-screen layout: workstation assignment, regions with navigation history, faceplate windows, monitors and new window', async ({ page, request }) => {
  test.setTimeout(90000)
  const auth = await login(request), h = auth.headers, u = `${Date.now()}`.slice(-6)
  async function screen(name: string, nodes: object[]) {
    const s = await (await request.post('/api/screens', { headers: h, data: { name } })).json()
    const saved = await (await request.put(`/api/screens/${s.id}`, { headers: h, data: { name, revision: s.revision, document: { schemaVersion: 1, width: 1200, height: 720, background: '#101b2d', nodes } } })).json()
    const p = await request.post(`/api/screens/${s.id}/publish`, { headers: h, data: { revision: saved.revision } }); expect(p.ok(), await p.text()).toBeTruthy(); return s.id as number
  }
  const detail = await screen(`详情-${u}`, [node('d', '详情画面', none)])
  const face = await screen(`面板-${u}`, [node('f', '泵面板', none)])
  const overview = await screen(`总览-${u}`, [node('go', '去详情', { type: 'navigate', screenId: detail, tagId: null, value: 0 }), node('pop', '弹面板', { type: 'popup', screenId: face, tagId: null, value: 0 }, 300)])
  const layout = await (await request.post('/api/hmi/layouts', { headers: h, data: { name: `中控-${u}`, document: { monitors: [
    { name: '主屏', columns: 12, rows: 12, regions: [{ id: 'banner', kind: 'alarmBanner', row: 1, col: 1, rowSpan: 1, colSpan: 12 }, { id: 'nav', kind: 'navigation', row: 2, col: 1, rowSpan: 11, colSpan: 2 }, { id: 'main', kind: 'screen', screenId: overview, row: 2, col: 3, rowSpan: 11, colSpan: 10 }] },
    { name: '副屏', columns: 12, rows: 12, regions: [{ id: 'alarms', kind: 'alarmList', row: 1, col: 1, rowSpan: 12, colSpan: 12 }] }] } } })).json()
  expect(layout.id).toBeTruthy()

  await page.addInitScript(({ token, user }) => { localStorage.setItem('scada_token', token); localStorage.setItem('scada_user', JSON.stringify(user)) }, auth)
  // 登记工作站并设为本机
  await page.goto('/config/layouts')
  await page.locator('[data-test="station-new"]').click()
  await page.locator('[data-test="station-name"]').fill('中文工位')
  await page.locator('[data-test="station-save"]').click()
  await expect(page.getByText('工作站名称须为 1～60 个字母、数字、点、下划线或连字符')).toBeVisible()
  await expect(page.locator('[data-test="station-name"]')).toBeVisible()
  await page.locator('[data-test="station-name"]').fill(`OP-${u}`)
  await page.locator('[data-test="station-layout"]').click(); await page.getByRole('option', { name: `中控-${u}` }).click()
  await page.locator('[data-test="station-save"]').click()
  await page.locator(`[data-test="station-local-OP-${u}"]`).click()
  await expect(page.locator('[data-test="local-station"]')).toContainText(`OP-${u}`)

  await page.goto('/console')
  await expect(page.locator('[data-test="console-station"]')).toContainText(`OP-${u}`)
  const main = page.locator('[data-test="region-main"]')
  await expect(page.locator('[data-test="alarm-banner"]')).toBeVisible()
  await main.locator('[data-node-id="go"]').click()
  await expect(main.locator('[data-node-id="d"]')).toBeVisible()
  await main.locator('[data-test="region-back-main"]').click()
  await expect(main.locator('[data-node-id="go"]')).toBeVisible()
  await main.locator('[data-test="region-forward-main"]').click()
  await expect(main.locator('[data-node-id="d"]')).toBeVisible()
  // 画面目录在主区域打开
  await page.locator(`[data-test="nav-${overview}"]`).click()
  await main.locator('[data-node-id="pop"]').click()
  await expect(page.locator('[data-test="faceplate"]')).toHaveCount(1)
  await expect(page.locator('[data-test="faceplate"] [data-node-id="f"]')).toBeVisible()
  await page.locator('[data-test="faceplate-close"]').click(); await expect(page.locator('[data-test="faceplate"]')).toHaveCount(0)
  // 新窗口（Window Management 不可用时退化为普通窗口）
  await main.locator('[data-test="region-window-main"]').click()
  const popup = page.waitForEvent('popup'); await page.locator('[data-test="monitor-open"]').click()
  const win = await popup; await win.waitForLoadState(); expect(win.url()).toContain(`/kiosk/screens/${overview}`)
  await expect(win.locator('[data-node-id="pop"]')).toBeVisible(); await win.close()
  // 切换显示器
  await page.locator('[data-test="console-monitor"]').getByText('副屏').click()
  await expect(page.locator('[data-test="alarm-list"]')).toBeVisible()
  await page.screenshot({ path: test.info().outputPath('console.png') })

  await page.locator('[data-test="console-monitor"]').getByText('主屏').click()
  await page.evaluate(() => { window.open = () => null })
  await main.locator('[data-test="region-window-main"]').click()
  const dialog = page.getByRole('dialog', { name: '选择显示器' })
  await expect(dialog).toBeVisible()
  await page.locator('[data-test="monitor-open"]').click()
  await expect(page.getByText('浏览器阻止了新窗口，请允许本站弹出窗口')).toBeVisible()
  await expect(dialog).toBeVisible()
  await dialog.getByRole('button', { name: '取消' }).click()
  await main.locator('[data-node-id="pop"]').click()
  await expect(page.locator('[data-test="faceplate"]')).toHaveCount(1)
  await page.locator('[data-test="faceplate"]').getByRole('button', { name: '新窗口' }).click()
  await expect(dialog).toBeVisible()
  await expect(page.locator('[data-test="faceplate"]')).toHaveCount(1)
  await page.locator('[data-test="monitor-open"]').click()
  await expect(dialog).toBeVisible()
  await expect(page.locator('[data-test="faceplate"]')).toHaveCount(1)
  await dialog.getByRole('button', { name: '取消' }).click()
  await page.locator('[data-test="console-open-all"]').click()
  await expect(page.getByText('浏览器阻止了新窗口，请允许本站弹出窗口').last()).toBeVisible()
  await expect(page.getByText('已在新窗口中打开')).toHaveCount(0)
  await expect(page.getByText('已以新窗口打开')).toHaveCount(0)
})
