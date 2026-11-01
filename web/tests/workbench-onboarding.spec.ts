import { test, expect, type Page, type APIRequestContext } from './fixtures'
import type { HmiLayout, HmiLayoutDocument } from '../src/api/hmiLayouts'
import type { ScreenSummary } from '../src/screens/model'

const publishedScreen: ScreenSummary = { id: 101, name: '光伏总览', revision: 2, publishedRevision: 2, hasDraftChanges: false, updatedTs: 1, updatedBy: 'admin' }
const draftScreen: ScreenSummary = { ...publishedScreen, id: 102, name: '未发布画面', publishedRevision: null, hasDraftChanges: true }
const navigationDocument: HmiLayoutDocument = { monitors: [{ name: '主屏', columns: 12, rows: 12, regions: [{ id: 'nav', kind: 'navigation', row: 1, col: 1, rowSpan: 12, colSpan: 12 }] }] }

async function setup(page: Page, request: APIRequestContext, viewer = false, locale = 'zh-CN') {
  const response = await request.post('/api/auth/login', { data: { userName: 'admin', password: 'admin123' } })
  expect(response.ok()).toBeTruthy()
  const auth = await response.json()
  if (viewer) auth.user = { ...auth.user, role: 'Viewer', access: { global: ['View'], areas: [] } }
  await page.addInitScript(({ auth, locale }) => {
    localStorage.setItem('scada_token', auth.token)
    localStorage.setItem('scada_user', JSON.stringify(auth.user))
    localStorage.setItem('scada_locale', locale)
    localStorage.removeItem('scada_workstation'); localStorage.removeItem('scada_workstation_id')
  }, { auth, locale })
  const state = {
    screens: [publishedScreen, draftScreen], layouts: [] as HmiLayout[], publications: new Map<number, HmiLayout>(),
    failScreens: false, failSave: false, failLayouts: false, saveAttempts: 0, publishAttempts: 0, workstationWrites: 0
  }
  await page.route(url => url.pathname === '/api/screens', route => state.failScreens
    ? route.fulfill({ status: 500, json: { message: '画面读取暂时失败' } }) : route.fulfill({ json: state.screens }))
  await page.route(url => /^\/api\/screens\/\d+$/.test(url.pathname), route => {
    const id = Number(new URL(route.request().url()).pathname.split('/').at(-1))
    const screen = state.screens.find(item => item.id === id && item.publishedRevision != null)
    if (!screen) return route.fulfill({ status: 404 })
    return route.fulfill({ json: { ...screen, document: { schemaVersion: 1, width: 800, height: 500, background: '#101b2d', nodes: [
      { id: 'marker', type: 'button', x: 40, y: 40, width: 200, height: 60, rotation: 0, text: '已发布画面内容', color: '#7394b8', activeColor: '#34d399', unit: '', decimals: 1, min: 0, max: 100, activeThreshold: .5, tagId: null, action: { type: 'none', screenId: null, tagId: null, value: 0 } }
    ] } } })
  })
  await page.route(url => url.pathname === '/api/hmi/layouts', async route => {
    const url = new URL(route.request().url())
    if (route.request().method() === 'POST') {
      state.saveAttempts++
      if (state.failSave) return route.fulfill({ status: 500, json: { message: '草稿保存暂时失败' } })
      const body = route.request().postDataJSON()
      const layout: HmiLayout = { ...body, id: 700001 + state.layouts.length, revision: 1, publishedRevision: null, updatedTs: Date.now(), updatedBy: 'admin', hasDraftChanges: true }
      state.layouts.push(layout)
      return route.fulfill({ json: layout })
    }
    if (state.failLayouts) return route.fulfill({ status: 500, json: { message: '工作台读取暂时失败' } })
    return route.fulfill({ json: url.searchParams.get('draft') === 'true' ? state.layouts : [...state.publications.values()] })
  })
  await page.route(url => /^\/api\/hmi\/layouts\/\d+(\/versions(\/\d+)?|\/publish)?$/.test(url.pathname), route => {
    const url = new URL(route.request().url()), parts = url.pathname.split('/'), id = Number(parts[4])
    const layout = state.layouts.find(item => item.id === id)
    if (!layout) return route.fulfill({ status: 404 })
    if (parts[5] === 'publish') {
      state.publishAttempts++
      const body = route.request().postDataJSON()
      expect(body.note.trim()).not.toBe('')
      Object.assign(layout, { revision: layout.revision + 1, publishedRevision: layout.revision + 1, publishedName: layout.name, hasDraftChanges: false })
      state.publications.set(id, structuredClone(layout))
      return route.fulfill({ json: layout })
    }
    if (parts[5] === 'versions') {
      const current = state.publications.get(id)
      const version = current ? { ...current, kind: 'publish', note: '首次发布', publishedTs: 1, publishedBy: 'admin' } : null
      return route.fulfill({ json: parts[6] ? version : version ? [version] : [] })
    }
    if (route.request().method() === 'PUT') {
      state.saveAttempts++
      Object.assign(layout, route.request().postDataJSON(), { revision: layout.revision + 1, hasDraftChanges: true })
      return route.fulfill({ json: layout })
    }
    const result = url.searchParams.get('draft') === 'true' ? layout : state.publications.get(id)
    return result ? route.fulfill({ json: result }) : route.fulfill({ status: 404 })
  })
  await page.route(url => url.pathname === '/api/hmi/workstations', route => {
    if (route.request().method() !== 'GET') { state.workstationWrites++; throw new Error('Onboarding must not write workstation assignments') }
    return route.fulfill({ json: [] })
  })
  await page.route(url => url.pathname === '/api/alarms', route => route.fulfill({ json: [] }))
  await page.route(url => url.pathname === '/api/alarms/summary', route => route.fulfill({ json: { unack: 0, acked: 0, rtnUnack: 0, suppressed: 0, shelved: 0, flood: false } }))
  return state
}

async function preview(page: Page) {
  await page.locator('[data-test="quick-layout-next"]').click()
  await expect(page.locator('[data-test="quick-layout-preview"]')).toBeVisible()
  const frame = page.frameLocator('[data-test="quick-layout-preview"] [data-test="runtime-preview-frame"]')
  await expect(frame.locator('[data-test="console"]')).toHaveJSProperty('inert', true)
  await expect(frame.locator('[data-node-id="marker"]')).toBeVisible()
  await expect(frame.locator('[data-test="console-configure"]')).toHaveCount(0)
  return frame
}

test('workbench onboarding: quick creation persists a real published layout without workstation registration', async ({ page, request }) => {
  test.setTimeout(60000)
  const login = await request.post('/api/auth/login', { data: { userName: 'admin', password: 'admin123' } })
  expect(login.ok()).toBeTruthy()
  const auth = await login.json(), headers = { Authorization: `Bearer ${auth.token}` }
  await page.addInitScript(auth => {
    localStorage.setItem('scada_token', auth.token)
    localStorage.setItem('scada_user', JSON.stringify(auth.user))
    localStorage.setItem('scada_locale', 'zh-CN')
    localStorage.removeItem('scada_workstation'); localStorage.removeItem('scada_workstation_id')
  }, auth)
  const name = '现场快速创建验收'
  const created = await request.post('/api/screens', { headers, data: { name } })
  expect(created.ok()).toBeTruthy()
  const screen = await created.json()
  const savedScreen = await request.put(`/api/screens/${screen.id}`, { headers, data: {
    name, revision: screen.revision, document: { schemaVersion: 1, width: 800, height: 500, background: '#101b2d', nodes: [
      { id: 'marker', type: 'button', x: 40, y: 40, width: 200, height: 60, rotation: 0, text: '实际发布画面', color: '#7394b8', activeColor: '#34d399', unit: '', decimals: 1, min: 0, max: 100, activeThreshold: .5, tagId: null, action: { type: 'none', screenId: null, tagId: null, value: 0 } }
    ] }
  } })
  expect(savedScreen.ok(), await savedScreen.text()).toBeTruthy()
  const screenPublication = await request.post(`/api/screens/${screen.id}/publish`, { headers, data: { revision: (await savedScreen.json()).revision } })
  expect(screenPublication.ok(), await screenPublication.text()).toBeTruthy()
  await page.goto('/console')
  await page.locator('[data-test="console-create-workbench"]').click()
  await page.getByRole('textbox', { name: '工作台名称', exact: true }).fill('现场单屏工作台')
  await preview(page)
  await page.locator('[data-test="quick-layout-save"]').click()
  await expect(page.locator('[data-test="layout-lifecycle-dialog"]')).toBeVisible()
  const drafts = await (await request.get('/api/hmi/layouts?draft=true', { headers })).json() as HmiLayout[]
  expect(drafts).toHaveLength(1)
  expect(drafts[0].publishedRevision).toBeNull()
  expect(await (await request.get('/api/hmi/layouts', { headers })).json()).toEqual([])
  await page.locator('[data-test="layout-lifecycle-note"] textarea').fill('验收后确认发布')
  await page.locator('[data-test="layout-lifecycle-submit"]').click()
  await expect(page).toHaveURL(`/console/${drafts[0].id}`)
  await expect(page.locator('.console-title')).toHaveText('现场单屏工作台')
  await expect(page.locator('[data-node-id="marker"]')).toBeVisible()
  const runtime = await (await request.get(`/api/hmi/layouts/${drafts[0].id}`, { headers })).json() as HmiLayout
  expect(runtime.document.monitors).toHaveLength(1)
  expect(runtime.document.monitors[0].regions[0].screenId).toBe(screen.id)
  expect(runtime.publishedRevision).toBeGreaterThan(0)
  expect(await (await request.get('/api/hmi/workstations', { headers })).json()).toEqual([])
  await page.reload()
  await expect(page.locator('.console-title')).toHaveText('现场单屏工作台')
  await expect(page.locator('[data-node-id="marker"]')).toBeVisible()
})

test('workbench onboarding: empty runtime explains screen/layout separation and exposes visible actions', async ({ page, request }) => {
  await setup(page, request)
  for (const size of [{ width: 1600, height: 900 }, { width: 1167, height: 768 }, { width: 320, height: 640 }]) {
    await page.setViewportSize(size)
    await page.goto('/console')
    await expect(page.locator('.console-title')).toHaveText('运行工作台')
    await expect(page.locator('[data-test="console-layout-error"]')).toContainText('画面已发布不代表工作台已配置')
    await expect(page.locator('[data-test="console-create-workbench"]')).toBeVisible()
    expect(await page.locator('.console-controls [data-test="console-layout-error"]').count()).toBe(0)
    const banner = (await page.locator('[data-test="alarm-banner"]').boundingBox())!
    const button = (await page.locator('[data-test="console-create-workbench"]').boundingBox())!
    expect(button.y).toBeGreaterThanOrEqual(banner.y + banner.height)
    expect(button.y + button.height).toBeLessThanOrEqual(size.height)
    expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(size.width)
  }
  await page.locator('[data-test="console-empty-configure"]').click()
  await expect(page).toHaveURL('/config/layouts')
  await expect(page.locator('[data-test="layout-quick-create"]')).toBeVisible()
  await expect(page.locator('[data-test="workbench-setup-help"]')).toContainText('工作站分配为可选操作')
})

test('workbench onboarding: published screen creates a previewed draft and publishes only after confirmation', async ({ page, request }) => {
  const state = await setup(page, request)
  await page.goto('/screens')
  await expect(page.locator('[data-test="screen-create-workbench-102"]')).toHaveCount(0)
  await page.locator('[data-test="screen-create-workbench-101"]').click()
  await expect(page.locator('[data-test="quick-layout-screen"]')).toContainText(publishedScreen.name)
  await expect(page.locator('[data-test="quick-layout-template"]')).toContainText('单屏：全屏画面')
  await page.getByRole('textbox', { name: '工作台名称', exact: true }).fill('光伏监控工作台')
  await preview(page)
  expect(state.saveAttempts).toBe(0)
  await page.locator('[data-test="quick-layout-save"]').click()
  const dialog = page.locator('[data-test="layout-lifecycle-dialog"]')
  await expect(dialog).toBeVisible()
  expect(state.layouts).toHaveLength(1)
  expect(state.layouts[0].publishedRevision).toBeNull()
  expect(state.layouts[0].document.monitors).toHaveLength(1)
  expect(state.layouts[0].document.monitors[0].regions[0].screenId).toBe(101)
  expect(state.publishAttempts).toBe(0)
  await expect(page.locator('[data-test="layout-publication-preview-details"]')).toHaveJSProperty('open', false)
  await expect(page.locator('[data-test="layout-publication-comparison-details"]')).toHaveJSProperty('open', false)
  await expect(page.locator('[data-test="layout-publish-references"]')).toContainText(publishedScreen.name)
  await expect(page.locator('[data-test="layout-lifecycle-submit"]')).toBeDisabled()
  await page.locator('[data-test="layout-lifecycle-note"] textarea').fill('首次创建单屏监控工作台')
  await page.locator('[data-test="layout-lifecycle-submit"]').click()
  await expect(page).toHaveURL('/console/700001')
  await expect(page.locator('.console-title')).toHaveText('光伏监控工作台')
  await expect(page.locator('[data-node-id="marker"]')).toBeVisible()
  expect(state.publishAttempts).toBe(1)
  expect(state.workstationWrites).toBe(0)
})

test('workbench onboarding: viewers get a single-screen path without configuration or creation controls', async ({ page, request }) => {
  await setup(page, request, true)
  await page.goto('/console')
  await expect(page.locator('[data-test="console-layout-error"]')).toContainText('请联系管理员')
  await expect(page.locator('[data-test="console-configure"]')).toHaveCount(0)
  await expect(page.locator('[data-test="console-create-workbench"]')).toHaveCount(0)
  await page.locator('[data-test="console-open-screens"]').click()
  await expect(page).toHaveURL('/screens')
  await expect(page.locator('[data-test^="screen-create-workbench-"]')).toHaveCount(0)
  await page.locator('[data-test="screen-run-101"]').click()
  await expect(page).toHaveURL('/screens/101')
})

test('workbench onboarding: screen read failures retry explicitly and no published screen blocks progression', async ({ page, request }) => {
  const state = await setup(page, request)
  await page.goto('/config/layouts')
  state.failScreens = true
  await page.locator('[data-test="layout-quick-create"]').click()
  await expect(page.locator('[data-test="quick-layout-error"]')).toContainText('画面读取暂时失败')
  await expect(page.locator('[data-test="quick-layout-next"]')).toBeDisabled()
  state.failScreens = false
  await page.locator('[data-test="quick-layout-retry"]').click()
  await expect(page.locator('[data-test="quick-layout-next"]')).toBeEnabled()
  await page.locator('[data-test="quick-layout-dialog"]').getByRole('button', { name: '取消', exact: true }).click()
  state.screens = [draftScreen]
  await page.locator('[data-test="layout-quick-create"]').click()
  await expect(page.locator('[data-test="quick-layout-dialog"]')).toContainText('暂无已发布画面')
  await expect(page.locator('[data-test="quick-layout-next"]')).toBeDisabled()
  expect(state.saveAttempts).toBe(0)
})

test('workbench onboarding: a failed save preserves preview and cancellation leaves an unpublished draft', async ({ page, request }) => {
  const state = await setup(page, request)
  await page.goto('/console')
  await page.locator('[data-test="console-create-workbench"]').click()
  await page.getByRole('textbox', { name: '工作台名称', exact: true }).fill('保留输入的工作台')
  await page.locator('[data-test="quick-layout-template"]').click()
  await page.getByRole('option', { name: '单屏：画面与报警', exact: true }).click()
  await preview(page)
  state.failSave = true
  await page.locator('[data-test="quick-layout-save"]').click()
  await expect(page.locator('[data-test="quick-layout-error"]')).toContainText('草稿保存暂时失败')
  await expect(page.locator('[data-test="quick-layout-preview"]')).toContainText('保留输入的工作台')
  await page.locator('[data-test="quick-layout-back"]').click()
  await expect(page.getByRole('textbox', { name: '工作台名称', exact: true })).toHaveValue('保留输入的工作台')
  await expect(page.locator('[data-test="quick-layout-template"]')).toContainText('单屏：画面与报警')
  await preview(page)
  state.failSave = false
  await page.locator('[data-test="quick-layout-save"]').click()
  await page.locator('[data-test="layout-lifecycle-dialog"]').getByRole('button', { name: '关闭', exact: true }).click()
  expect(state.saveAttempts).toBe(2)
  expect(state.layouts).toHaveLength(1)
  expect(state.layouts[0].publishedRevision).toBeNull()
  expect(state.publishAttempts).toBe(0)
  expect(state.workstationWrites).toBe(0)
})

test('workbench onboarding: normal advanced save presents the publish next step without auto-publishing', async ({ page, request }) => {
  const state = await setup(page, request)
  state.layouts.push({ id: 501, name: '待编辑工作台', revision: 1, publishedRevision: null, hasDraftChanges: true, document: navigationDocument, updatedTs: 1, updatedBy: 'admin' })
  await page.goto('/config/layouts')
  await page.locator('[data-test="layouts"]').getByRole('button', { name: '编辑', exact: true }).click()
  await page.locator('[data-test="layout-name"]').fill('已保存草稿工作台')
  await page.locator('[data-test="layout-save"]').click()
  await expect(page.locator('[data-test="layout-saved-next"]')).toContainText('运行版未更新')
  expect(state.publishAttempts).toBe(0)
  await page.locator('[data-test="layout-saved-publish"]').click()
  await expect(page.locator('[data-test="layout-lifecycle-dialog"]')).toBeVisible()
  await expect(page.locator('[data-test="layout-publication-comparison-details"]')).toHaveJSProperty('open', true)
})

test('workbench onboarding: runtime load errors remain explicit and English entry labels are distinct', async ({ page, request }) => {
  const state = await setup(page, request, false, 'en-US')
  state.failLayouts = true
  await page.goto('/console')
  await expect(page.locator('[data-test="console-layout-error"]')).toContainText('工作台读取暂时失败')
  await expect(page.locator('[data-test="console-layout-error"]')).not.toContainText('No published workbench is available')
  state.failLayouts = false
  await page.locator('[data-test="console-layout-retry"]').click()
  await expect(page.locator('.console-title')).toHaveText('Runtime workbench')
  await page.locator('[data-test="console-empty-configure"]').click()
  await expect(page.locator('[data-test="nav-item-layouts"]')).toContainText('Workbench setup')
  await expect(page.locator('[data-test="nav-item-console"]')).toContainText('Runtime workbench')
})

for (const locale of ['zh-CN', 'en-US']) {
  test(`workbench tooltips: ${locale} help supports hover and focus without consuming Enter or Space`, async ({ page, request }) => {
    const state = await setup(page, request, false, locale)
    await page.goto('/console')
    const create = page.locator('[data-test="console-create-workbench"]'), help = page.locator('.workbench-tooltip:visible')
    await create.hover()
    await expect(help).toContainText(locale === 'zh-CN' ? '无需登记工位' : 'No workstation registration')
    await page.mouse.move(1, 1)
    await create.focus()
    await expect(help).toContainText(locale === 'zh-CN' ? '已发布画面' : 'published screen')
    await create.press('Enter')
    await expect(page.locator('[data-test="quick-layout-dialog"]')).toBeVisible()
    const name = page.getByRole('textbox', { name: locale === 'zh-CN' ? '工作台名称' : 'Workbench name', exact: true })
    await name.focus()
    await expect(help).toContainText('100')
    await page.locator('[data-test="quick-layout-next"]').focus()
    await page.locator('[data-test="quick-layout-next"]').press('Space')
    await expect(page.locator('[data-test="quick-layout-preview"]')).toBeVisible()
    await expect(page.frameLocator('[data-test="quick-layout-preview"] [data-test="runtime-preview-frame"]').locator('[data-node-id="marker"]')).toBeVisible()
    await page.locator('[data-test="quick-layout-save"]').hover()
    await expect(help).toContainText(locale === 'zh-CN' ? '取消审阅保留草稿' : 'Cancelling review keeps the draft')
    expect(state.saveAttempts).toBe(0)
    expect(state.publishAttempts).toBe(0)
    expect(state.workstationWrites).toBe(0)
  })
}

test('workbench tooltips: disabled unpublished layouts explain the prerequisite without opening or publishing', async ({ page, request }) => {
  const state = await setup(page, request)
  state.layouts.push({ id: 501, name: '尚未发布的工作台', revision: 1, publishedRevision: null, hasDraftChanges: true, document: navigationDocument, updatedTs: 1, updatedBy: 'admin' })
  await page.goto('/config/layouts')
  const open = page.locator('[data-test="layout-open-501"]'), trigger = open.locator('..')
  await expect(open).toBeDisabled()
  await expect(trigger).toHaveClass(/disabled-tooltip-trigger/)
  await trigger.focus()
  await expect(page.locator('.workbench-tooltip:visible')).toContainText('尚未发布')
  await expect(trigger).toHaveAttribute('aria-describedby', /.+/)
  await trigger.press('Enter')
  await expect(page).toHaveURL(/\/config\/layouts$/)
  await expect(page.locator('[data-test="layout-lifecycle-dialog"]')).toBeHidden()
  expect(state.saveAttempts).toBe(0)
  expect(state.publishAttempts).toBe(0)
})
