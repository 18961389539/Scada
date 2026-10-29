import { test, expect, type Page } from './fixtures'
import type { HmiLayout, HmiLayoutVersion, HmiMonitor, HmiRegion, HmiWorkstation } from '../src/api/hmiLayouts'

const screen = (id = 'main', screenId = 10): HmiRegion => ({ id, screenId, kind: 'screen', row: 1, col: 1, rowSpan: 12, colSpan: 12 })
const monitor = (name = '主屏', regions = [screen()]): HmiMonitor => ({ name, columns: 12, rows: 12, previewWidth: 1920, previewHeight: 1080, regions })
const layout = (id: number, name: string, publishedRevision: number | null, changed = false): HmiLayout => ({ id, name, revision: 7, updatedTs: 1, updatedBy: 'admin', document: { monitors: [monitor()] }, publishedRevision, publishedName: publishedRevision == null ? null : name, hasDraftChanges: changed })

async function fixture(page: Page, binding: { id?: string; name?: string } = { id: '77', name: 'OLD-NAME' }) {
  await page.addInitScript(binding => {
    localStorage.setItem('scada_token', 'layout-catalog-test')
    localStorage.setItem('scada_user', JSON.stringify({ id: 1, userName: 'admin', displayName: '管理员', role: 'Admin', access: { global: ['View', 'Control', 'AckAlarm', 'Configure', 'ManageUsers', 'System'], areas: [] } }))
    localStorage.setItem('scada_locale', 'zh-CN')
    localStorage.removeItem('scada_workstation_id'); localStorage.removeItem('scada_workstation')
    if (binding.id) localStorage.setItem('scada_workstation_id', binding.id)
    if (binding.name) localStorage.setItem('scada_workstation', binding.name)
  }, binding)
  const layouts = [layout(501, '泵站草稿', 5, true), layout(502, '备用运行', 4), layout(503, '新建草稿', null)]
  layouts[0].publishedName = '正在生产的布局'
  const stations: HmiWorkstation[] = [
    { id: 77, name: 'OP-A', description: '主控制室', layoutId: 501, allowControl: true, updatedTs: 1, updatedBy: 'admin' },
    { id: 78, name: 'OP-B', description: '观察工位', layoutId: 501, allowControl: false, updatedTs: 1, updatedBy: 'admin' }
  ]
  const current: HmiLayoutVersion = { ...layouts[0], revision: 5, name: '正在生产的布局', publishedTs: 1, publishedBy: 'admin', kind: 'publish', note: '生产版本', document: structuredClone(layouts[0].document) }
  const state = { layouts, stations, current, stationError: false, writes: [] as { path: string; method: string; body: HmiLayout }[] }
  await page.route('**/api/**', async route => {
    const path = new URL(route.request().url()).pathname, method = route.request().method()
    if (path === '/api/hmi/layouts' && method === 'GET') return route.fulfill({ json: state.layouts })
    if (path === '/api/hmi/layouts' && method === 'POST') {
      const body = route.request().postDataJSON()
      state.writes.push({ path, method, body })
      const created = { ...body, id: 600, revision: 1, publishedRevision: null, hasDraftChanges: true, updatedTs: 1, updatedBy: 'admin' }
      state.layouts.push(created)
      return route.fulfill({ json: created })
    }
    if (path === '/api/hmi/layouts/501') return route.fulfill({ json: state.layouts[0] })
    if (path === '/api/hmi/layouts/501/versions') return route.fulfill({ json: [state.current] })
    if (path === '/api/hmi/layouts/501/versions/5') return route.fulfill({ json: state.current })
    if (path === '/api/hmi/workstations') return state.stationError ? route.fulfill({ status: 500, json: { message: '工位读取失败' } }) : route.fulfill({ json: state.stations })
    if (path === '/api/hmi/workstations/resolve') return route.fulfill({ json: { workstation: state.stations[0], layout: { ...state.layouts[0], name: state.current.name, document: state.current.document } } })
    if (path === '/api/screens') return route.fulfill({ json: [{ id: 10, name: '泵站总览', publishedRevision: 2 }, { id: 11, name: '泵站详情', publishedRevision: 3 }] })
    if (path === '/api/screens/10' || path === '/api/screens/11') return route.fulfill({ json: { id: 10, name: '泵站总览', revision: 2, document: { schemaVersion: 1, width: 1200, height: 720, background: '#101b2d', nodes: [] } } })
    if (path === '/api/auth/site-info') return route.fulfill({ json: { siteName: '测试项目', demoMode: false } })
    if (path === '/api/license') return route.fulfill({ json: { status: 'Active', blocked: false } })
    if (path === '/api/health') return route.fulfill({ json: { status: 'ok' } })
    if (path === '/api/alarms/summary') return route.fulfill({ json: { unack: 0, acked: 0, rtnUnack: 0, flood: false } })
    return route.fulfill({ json: [] })
  })
  return state
}

const rows = (page: Page) => page.locator('[data-test="layouts"] .el-table__body-wrapper tbody tr')
async function choose(page: Page, selector: string, name: string) {
  await page.locator(`[data-test="${selector}"]`).click()
  await page.getByRole('option', { name, exact: true }).click()
}

test('catalog filters combine names, publication and workstation usage and reset cleanly', async ({ page }) => {
  await fixture(page)
  await page.goto('/config/layouts')
  await expect(rows(page)).toHaveCount(3)
  await page.locator('[data-test="layouts-search"]').fill('正在生产')
  await expect(rows(page)).toHaveCount(1)
  await expect(rows(page)).toContainText('泵站草稿')
  await choose(page, 'layouts-status-filter', '草稿有变更')
  await choose(page, 'layouts-usage-filter', '未分配工作站')
  await expect(rows(page)).toHaveCount(0)
  await expect(page.locator('[data-test="layouts"]')).toContainText('没有符合筛选条件')
  await page.locator('[data-test="layouts-reset-filters"]').click()
  await choose(page, 'layouts-status-filter', '未发布')
  await expect(rows(page)).toHaveCount(1)
  await expect(rows(page)).toContainText('新建草稿')
  await page.locator('[data-test="layouts-reset-filters"]').click()
  await choose(page, 'layouts-usage-filter', '已分配工作站')
  await expect(rows(page)).toHaveCount(1)
  await expect(page.locator('[data-test="layouts-result-count"]')).toContainText('1 / 3')
})

test('local entry and workstation details display published assignment, stable identity and permissions', async ({ page }) => {
  await fixture(page)
  await page.goto('/config/layouts')
  const local = page.locator('[data-test="local-workstation-summary"]')
  await expect(local).toContainText('OP-A')
  await expect(local).toContainText('正在生产的布局 · V5')
  await expect(local).not.toContainText('泵站草稿')
  await rows(page).first().locator('[data-test="layout-usage"]').click()
  const detail = page.locator('[data-test="layout-usage-detail-501"]')
  await expect(detail).toContainText('主控制室')
  await expect(detail).toContainText('观察工位')
  await expect(detail).toContainText('本机')
  await expect(detail).toContainText('允许控制')
  await expect(detail).toContainText('只监视')
  await page.keyboard.press('Escape')
  await page.locator('[data-test="layout-open-local"]').click()
  await expect(page).toHaveURL(/\/console$/)
})

for (const scenario of ['invalid', 'unassigned', 'unpublished', 'missing', 'error'] as const) {
  test(`local runtime remains unavailable when assignment is ${scenario}`, async ({ page }) => {
    const state = await fixture(page, scenario === 'invalid' ? { id: 'broken', name: 'OP-A' } : undefined)
    if (scenario === 'unassigned') state.stations[0].layoutId = null
    if (scenario === 'unpublished') state.stations[0].layoutId = 503
    if (scenario === 'missing') state.stations[0].layoutId = 999
    if (scenario === 'error') state.stationError = true
    await page.goto('/config/layouts')
    await expect(page.locator('[data-test="layout-open-local"]')).toBeDisabled()
    const messages = { invalid: '绑定已失效', unassigned: '尚未分配布局', unpublished: '尚未发布', missing: '布局不存在', error: '读取失败' }
    await expect(page.locator('[data-test="local-workstation-summary"]')).toContainText(messages[scenario])
    if (scenario === 'error') {
      await expect(page.locator('[data-test="layouts-usage-filter"]').getByRole('combobox')).toBeDisabled()
      await expect(rows(page).first().locator('[data-test="layout-usage"]')).toContainText('待校验')
    }
  })
}

test('draft thumbnails explain bindings and hide unchanged publish action in the more menu', async ({ page }) => {
  const state = await fixture(page)
  state.layouts[0].document.monitors.push(monitor('副屏'), monitor('第三屏'))
  await page.goto('/config/layouts')
  await expect(rows(page).first()).toContainText('草稿结构')
  await expect(rows(page).first().getByRole('img')).toHaveCount(2)
  await expect(rows(page).first().getByRole('img').first()).toHaveAttribute('aria-label', /泵站总览/)
  await expect(rows(page).first()).toContainText('另有 1 个显示器')
  await page.screenshot({ path: test.info().outputPath('layout-catalog-desktop.png'), animations: 'disabled' })
  await expect(page.locator('[data-test="layout-publish-502"]')).toBeHidden()
  await page.locator('[data-test="layout-more-502"]').click()
  await expect(page.locator('[data-test="layout-publish-502"]')).toBeVisible()
  await page.keyboard.press('Escape')
  await expect(page.locator('[data-test="layout-publish-502"]')).toBeHidden()
  await page.locator('[data-test="layout-more-502"]').click()
  await expect(page.locator('[data-test="layout-publish-502"]')).toBeVisible()
  await page.setViewportSize({ width: 390, height: 844 })
  await expect(page.locator('[data-test="layout-open-local"]')).toBeInViewport()
  await expect(page.locator('[data-test="layout-publish-502"]')).toBeHidden()
  await expect.poll(() => page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true)
  await page.screenshot({ path: test.info().outputPath('layout-catalog-mobile.png'), animations: 'disabled' })
})

test('copy creates a uniquely named independent draft and monitor copies retain geometry with undo', async ({ page }) => {
  const state = await fixture(page)
  state.layouts.push(layout(504, '泵站草稿 副本', null))
  const original = structuredClone(state.layouts[0])
  await page.goto('/config/layouts')
  await page.locator('[data-test="layout-more-501"]').click()
  await page.locator('[data-test="layout-copy-501"]').click()
  await expect(page.locator('[data-test="layout-name"]')).toHaveValue('泵站草稿 副本 2')
  await expect(page.locator('[data-test="layout-copy-help"]')).toBeVisible()
  await page.locator('[data-test="layout-copy-monitor"]').first().click()
  await expect(page.getByRole('tab', { name: '主屏 副本', exact: true })).toHaveAttribute('aria-selected', 'true')
  await expect(page.locator('[data-test="monitor-preview-width"] input').last()).toHaveValue('1920')
  await page.locator('[data-test="layout-undo"]').click()
  await expect(page.getByRole('tab')).toHaveCount(1)
  await page.locator('[data-test="layout-redo"]').click()
  await page.getByRole('tab', { name: '主屏 副本', exact: true }).click()
  await page.locator('[data-test="region-title"]').fill('副屏专用')
  await page.locator('[data-test="layout-save"]').click()
  await expect(page.locator('[data-test="layout-dialog"]')).toBeHidden()
  expect(state.writes).toHaveLength(1)
  expect(state.writes[0]).toMatchObject({ path: '/api/hmi/layouts', method: 'POST', body: { revision: 0, name: '泵站草稿 副本 2' } })
  expect(state.writes[0].body.id).toBeUndefined()
  const monitors = state.writes[0].body.document.monitors
  expect(monitors[1]).toMatchObject({ name: '主屏 副本', previewWidth: 1920, previewHeight: 1080 })
  expect(monitors[0].regions[0].title).toBeUndefined()
  expect(monitors[1].regions[0].title).toBe('副屏专用')
  expect(state.layouts[0]).toEqual(original)
  await expect(page.locator('[data-test="layout-open-600"]')).toBeDisabled()
})

test('all templates produce valid independent geometry and support undo before saving', async ({ page }) => {
  const state = await fixture(page)
  await page.goto('/config/layouts')
  await page.locator('[data-test="layout-new"]').click()
  await page.locator('[data-test="layout-name"]').fill('模板布局')
  const options = ['单屏：全屏画面', '单屏：导航与画面', '单屏：画面与报警', '单屏：左右双画面', '双屏：总览与详情']
  for (const option of options) {
    await choose(page, 'layout-template', option)
    await page.locator('[data-test="layout-template-preview"] summary').click()
    await expect(page.locator('[data-test="layout-template-preview"] [role="img"]').first()).toBeVisible()
    await page.locator('[data-test="layout-preset"]').click()
    await page.locator('.el-message-box').getByRole('button', { name: '确定', exact: true }).click()
    await expect(page.locator('[data-test="layout-region-issues"]')).toBeHidden()
    await expect(page.getByRole('tab')).toHaveCount(option.startsWith('双屏') ? 2 : 1)
    await page.locator('[data-test="layout-template-preview"] summary').click()
  }
  await page.locator('[data-test="layout-undo"]').click()
  await expect(page.getByRole('tab')).toHaveCount(1)
  await page.locator('[data-test="layout-save"]').click()
  await expect(page.locator('[data-test="layout-dialog"]')).toBeHidden()
  expect(state.writes[0].body.document.monitors[0].regions.map(region => region.id)).toEqual(['left', 'right'])
  expect(state.writes[0].body.document.monitors[0].regions.every(region => region.kind !== 'alarmBanner')).toBe(true)
})

test('comparison reports readable binding names and matches retained monitors after removal', async ({ page }) => {
  const state = await fixture(page)
  state.current.document.monitors = [monitor('将移除的屏幕'), monitor('保留屏幕')]
  state.layouts[0].name = state.current.name
  state.layouts[0].document.monitors = [monitor('保留屏幕', [screen('main', 11)])]
  await page.goto('/config/layouts')
  await page.locator('[data-test="layout-publish-501"]').click()
  const summary = page.locator('[data-test="layout-diff-summary"]')
  await expect(summary).toContainText('移除 1 个显示器')
  await expect(summary).toContainText('修改 1 个画面绑定')
  await expect(summary).not.toContainText('新增')
  const binding = page.locator('[data-test="layout-version-diff"] tr').filter({ hasText: '保留屏幕' }).filter({ hasText: '画面' })
  await expect(binding).toContainText('泵站总览 (#10)')
  await expect(binding).toContainText('泵站详情 (#11)')
})

test('pure region rename has a concise summary and preserves duplicate draft identifiers in the diff', async ({ page }) => {
  const state = await fixture(page)
  state.layouts[0].name = state.current.name
  state.layouts[0].document.monitors[0].regions[0].id = 'renamed'
  await page.goto('/config/layouts')
  await page.locator('[data-test="layout-publish-501"]').click()
  await expect(page.locator('[data-test="layout-diff-summary"]')).toHaveText('修改 1 个区域的标识')
  await expect(page.locator('[data-test="layout-version-diff"] tbody tr')).toHaveCount(1)
  await page.getByRole('button', { name: '关闭', exact: true }).click()
  state.current.document.monitors[0].regions = [{ ...screen(''), colSpan: 6 }, { ...screen(''), col: 7, colSpan: 6 }]
  state.layouts[0].document = structuredClone(state.current.document)
  state.layouts[0].document.monitors[0].regions[1].title = '重复标识区域'
  await page.locator('[data-test="layout-publish-501"]').click()
  await expect(page.locator('[data-test="layout-version-diff"] tbody tr')).toHaveCount(1)
  await expect(page.locator('[data-test="layout-version-diff"]')).toContainText('? (2)')
  await expect(page.locator('[data-test="layout-version-diff"]')).toContainText('重复标识区域')
})
