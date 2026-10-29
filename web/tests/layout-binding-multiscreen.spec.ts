import { test, expect, type APIRequestContext, type Page } from './fixtures'

async function fixture(page: Page, request: APIRequestContext, legacyBinding = false) {
  const response = await request.post('/api/auth/login', { data: { userName: 'admin', password: 'admin123' } })
  expect(response.ok()).toBeTruthy()
  const { token } = await response.json()
  await page.addInitScript(({ token, legacyBinding }) => {
    localStorage.setItem('scada_token', token)
    localStorage.setItem('scada_locale', 'zh-CN')
    localStorage.removeItem('scada_user')
    if (!localStorage.getItem('binding-test-initialized')) {
      localStorage.removeItem('scada_workstation_id')
      if (legacyBinding) localStorage.setItem('scada_workstation', 'OldName')
      else localStorage.removeItem('scada_workstation')
      localStorage.setItem('binding-test-initialized', '1')
    }
  }, { token, legacyBinding })
  const layout = { id: 71001, name: '映射验收', revision: 1, updatedTs: Date.now(), updatedBy: 'admin', document: { monitors: [
    { name: '主屏', columns: 12, rows: 12, regions: [{ id: 'main', kind: 'screen', screenId: 71001, row: 1, col: 1, rowSpan: 12, colSpan: 12 }] },
    { name: '副屏', columns: 12, rows: 12, regions: [{ id: 'alarms', kind: 'alarmList', row: 1, col: 1, rowSpan: 12, colSpan: 12 }] }
  ] } }
  const state = { station: { id: 17, name: 'OldName', description: '', layoutId: layout.id, allowControl: false, updatedTs: Date.now(), updatedBy: 'admin' }, deleted: false, resolvedIds: [] as string[], receivedIds: [] as string[] }
  await page.route(url => url.pathname === '/api/hmi/layouts', route => {
    state.receivedIds.push(route.request().headers()['x-scada-workstation-id'] ?? '')
    return route.fulfill({ json: [layout] })
  })
  await page.route(url => url.pathname === '/api/hmi/workstations/resolve', route => {
    const url = new URL(route.request().url())
    state.resolvedIds.push(url.searchParams.get('id') ?? '')
    return state.deleted ? route.fulfill({ status: 404, json: { message: '工作站未登记' } }) : route.fulfill({ json: { workstation: state.station, layout } })
  })
  await page.route(url => url.pathname === '/api/screens', route => route.fulfill({ json: [{ id: 71001, name: '总览', revision: 1, publishedRevision: 1, hasDraftChanges: false, updatedTs: Date.now(), updatedBy: 'admin' }] }))
  await page.route(url => url.pathname === '/api/screens/71001', route => route.fulfill({ json: { id: 71001, name: '总览', revision: 1, publishedRevision: 1, document: { schemaVersion: 1, width: 1200, height: 720, background: '#101b2d', nodes: [] } } }))
  await page.route(url => url.pathname === '/api/alarms', route => route.fulfill({ json: [] }))
  await page.route(url => url.pathname === '/api/alarms/summary', route => route.fulfill({ json: { unack: 0, acked: 0, rtnUnack: 0, suppressed: 0, shelved: 0, flood: false } }))
  return state
}

test('legacy workstation binding migrates to a stable ID and survives renaming', async ({ page, request }) => {
  const state = await fixture(page, request, true)
  await page.goto('/console')
  await expect(page.locator('[data-test="console-station"]')).toContainText('OldName')
  await expect.poll(() => page.evaluate(() => localStorage.getItem('scada_workstation_id'))).toBe('17')
  state.station.name = 'RenamedStation'
  await page.reload()
  await expect(page.locator('[data-test="console-station"]')).toContainText('RenamedStation')
  expect(state.resolvedIds).toEqual(['', '17'])
  expect(state.receivedIds.at(-1)).toBe('17')
  expect(await page.evaluate(() => localStorage.getItem('scada_workstation'))).toBe('RenamedStation')
  state.deleted = true
  await page.reload()
  await expect(page.locator('[data-test="console-layout-error"]')).toContainText('工作站未登记')
  await expect(page.locator('[data-test="region-main"]')).toHaveCount(0)
  // Retain the stale identity until explicit re-binding, so control requests cannot silently become unbound.
  expect(await page.evaluate(() => localStorage.getItem('scada_workstation_id'))).toBe('17')
})

test('display mapping remembers physical identities across ordering changes and tracks opened windows', async ({ page, request }) => {
  await fixture(page, request)
  await page.addInitScript(() => {
    const display = (index: number) => ({ label: `物理屏 ${index + 1}`, availLeft: index * 1600, availTop: 0, availWidth: 1600, availHeight: 900, isPrimary: index === 0 })
    Object.defineProperty(window, 'getScreenDetails', { value: async () => ({ screens: (localStorage.getItem('missing-second-display') ? [0] : localStorage.getItem('reverse-display-order') ? [1, 0] : [0, 1]).map(display) }) })
    const opened: { url: string; features: string; closed: boolean; focused: boolean }[] = []
    Object.defineProperty(window, '__mappingWindows', { value: opened })
    window.open = (url, _name, features) => {
      const handle = { url: String(url), features: features ?? '', closed: false, focused: false, moveTo() {}, resizeTo() {}, focus() { this.focused = true } }
      opened.push(handle)
      return handle as unknown as Window
    }
  })
  await page.goto('/console/71001')
  await page.locator('[data-test="console-open-all"]').click()
  await page.locator('[data-test="console-monitor-map-1"]').click()
  await page.getByRole('option', { name: '物理屏 2 (1600×900)', exact: true }).click()
  await page.locator('[data-test="console-monitor-reopen-1"]').click()
  await expect(page.locator('[data-test="console-monitor-status-1"]')).toHaveText('已打开')
  await page.locator('[data-test="console-monitor-focus-1"]').click()
  expect(await page.evaluate(() => (window as unknown as { __mappingWindows: { focused: boolean }[] }).__mappingWindows[0].focused)).toBe(true)
  await page.evaluate(() => { (window as unknown as { __mappingWindows: { closed: boolean }[] }).__mappingWindows[0].closed = true })
  await expect(page.locator('[data-test="console-monitor-status-1"]')).toHaveText('已关闭')
  await expect(page.locator('[data-test="console-monitor-focus-1"]')).toBeDisabled()
  await page.evaluate(() => localStorage.setItem('reverse-display-order', '1'))
  await page.reload()
  await page.locator('[data-test="console-open-all"]').click()
  await expect(page.locator('[data-test="console-monitor-map-1"]')).toContainText('物理屏 2')
  await expect(page.locator('[data-test="console-monitor-status-1"]')).toHaveText('未跟踪')
  await page.locator('[data-test="console-monitor-reopen-1"]').click()
  expect(await page.evaluate(() => (window as unknown as { __mappingWindows: { features: string }[] }).__mappingWindows[0].features)).toContain('left=1600')
  await page.evaluate(() => { window.open = () => null })
  await page.locator('[data-test="console-open-mapped"]').click()
  await expect(page.locator('[data-test="console-mapping-dialog"]')).toBeVisible()
  await expect(page.locator('[data-test="console-mapped-window-error"]')).toContainText('浏览器阻止了新窗口')
  await page.evaluate(() => localStorage.setItem('missing-second-display', '1'))
  await page.reload()
  await page.locator('[data-test="console-open-all"]').click()
  await expect(page.locator('[data-test="console-monitor-map-1"]')).toContainText('普通新窗口')
  await expect(page.getByText('部分已保存的显示器不可用，已改为普通新窗口，请检查映射。', { exact: true })).toBeVisible()
})
