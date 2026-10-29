import { test, expect, type Page, type APIRequestContext } from './fixtures'
import path from 'node:path'
import type { HmiLayout, HmiLayoutDocument, HmiLayoutVersion } from '../src/api/hmiLayouts'

const document = (title = '生产导航'): HmiLayoutDocument => ({ monitors: [{ name: '操作屏', columns: 4, rows: 4, regions: [{ id: 'navigation', kind: 'navigation', title, row: 1, col: 1, rowSpan: 4, colSpan: 4 }] }] })
const screenshot = (name: string) => path.resolve(process.cwd(), '..', '.local', `layout-lifecycle-${name}.png`)

async function login(request: APIRequestContext, page: Page) {
  const response = await request.post('/api/auth/login', { data: { userName: 'admin', password: 'admin123' } })
  expect(response.ok()).toBeTruthy()
  const auth = await response.json()
  await page.addInitScript(auth => {
    localStorage.setItem('scada_token', auth.token)
    localStorage.setItem('scada_user', JSON.stringify(auth.user))
    localStorage.setItem('scada_locale', 'zh-CN')
    localStorage.removeItem('scada_workstation'); localStorage.removeItem('scada_workstation_id')
  }, auth)
  return { Authorization: `Bearer ${auth.token}` }
}

test('saved drafts publish independently and rollback publishes history while preserving the saved draft', async ({ page, request }) => {
  test.setTimeout(90000)
  const headers = await login(request, page)
  const unique = `布局生命周期-${Date.now()}`
  const createdResponse = await request.post('/api/hmi/layouts', { headers, data: { name: unique, document: document() } })
  expect(createdResponse.ok()).toBeTruthy()
  let draft = await createdResponse.json() as HmiLayout
  const originalDocument = structuredClone(draft.document)
  const id = draft.id
  let runtimePage: Page | undefined
  try {
    expect(draft.publishedRevision).toBeNull()
    const initialRuntimeList = await (await request.get('/api/hmi/layouts', { headers })).json() as HmiLayout[]
    expect(initialRuntimeList.some(layout => layout.id === id)).toBeFalsy()
    expect((await request.get(`/api/hmi/layouts/${id}`, { headers })).status()).toBe(404)
    await page.goto('/config/layouts')
    await expect(page.locator(`[data-test="layout-open-${id}"]`)).toBeDisabled()
    await page.locator(`[data-test="layout-publish-${id}"]`).click()
    const dialog = page.locator('[data-test="layout-lifecycle-dialog"]')
    await expect(dialog.getByText('发布已保存草稿', { exact: true })).toBeVisible()
    await expect(page.locator('[data-test="layout-version-preview"]')).toBeVisible()
    await expect(page.locator('[data-test="layout-lifecycle-submit"]')).toBeDisabled()
    await page.locator('[data-test="layout-lifecycle-note"] textarea').fill('首次发布，供生产运行使用')
    await page.locator('.layout-lifecycle .el-dialog__body').evaluate(element => { element.scrollTop = 0 })
    await page.screenshot({ path: screenshot('publish-review'), fullPage: true })
    await page.locator('[data-test="layout-lifecycle-submit"]').click()
    await expect(dialog).toBeHidden()
    draft = await (await request.get(`/api/hmi/layouts/${id}?draft=true`, { headers })).json()
    const firstPublication = draft.publishedRevision!
    expect(firstPublication).toBeGreaterThan(0)
    expect(draft.hasDraftChanges).toBeFalsy()
    runtimePage = await page.context().newPage()
    await runtimePage.goto(`/console/${id}`)
    await expect(runtimePage.locator('.console-title')).toHaveText(unique)
    await expect(runtimePage.locator('[data-test="console-version"]')).toHaveText(`V${firstPublication}`)

    const savedResponse = await request.put(`/api/hmi/layouts/${id}`, { headers, data: { name: `${unique}-第二版`, document: document('新版导航'), revision: draft.revision } })
    expect(savedResponse.ok()).toBeTruthy()
    draft = await savedResponse.json()
    let runtime = await (await request.get(`/api/hmi/layouts/${id}`, { headers })).json() as HmiLayout
    expect(runtime.name).toBe(unique)
    expect(runtime.revision).toBe(firstPublication)
    expect(runtime.document.monitors[0].regions[0].title).toBe('生产导航')
    expect(draft.hasDraftChanges).toBeTruthy()
    const draftRefresh = runtimePage.waitForResponse(response => new URL(response.url()).pathname === '/api/hmi/layouts')
    await runtimePage.locator('[data-test="console-refresh"]').click()
    await draftRefresh
    await expect(runtimePage.locator('[data-test="console-update"]')).toBeHidden()
    await expect(runtimePage.locator('.console-title')).toHaveText(unique)

    await page.reload()
    await expect(page.locator('[data-test="layouts"]').getByText('草稿有变更', { exact: true })).toBeVisible()
    await page.locator(`[data-test="layout-publish-${id}"]`).click()
    await expect(page.locator('[data-test="layout-version-diff"]')).toContainText('新版导航')
    await page.locator('[data-test="layout-lifecycle-note"] textarea').fill('调整导航标题并发布')
    await page.locator('[data-test="layout-lifecycle-submit"]').click()
    await expect(dialog).toBeHidden()
    draft = await (await request.get(`/api/hmi/layouts/${id}?draft=true`, { headers })).json()
    const secondPublication = draft.publishedRevision!
    expect(secondPublication).toBeGreaterThan(firstPublication)
    expect((await (await request.get(`/api/hmi/layouts/${id}`, { headers })).json()).name).toBe(`${unique}-第二版`)
    const publicationRefresh = runtimePage.waitForResponse(response => new URL(response.url()).pathname === '/api/hmi/layouts')
    await runtimePage.locator('[data-test="console-refresh"]').click()
    await publicationRefresh
    await expect(runtimePage.locator('[data-test="console-update"]')).toBeVisible()
    await expect(runtimePage.locator('.console-title')).toHaveText(unique)
    await expect(runtimePage.locator('[data-test="console-version"]')).toHaveText(`V${firstPublication}`)
    await runtimePage.locator('[data-test="console-update"] button').click()
    await runtimePage.locator('.el-message-box').getByRole('button', { name: /确定|确认/ }).click()
    await expect(runtimePage.locator('.console-title')).toHaveText(`${unique}-第二版`)
    await expect(runtimePage.locator('[data-test="console-version"]')).toHaveText(`V${secondPublication}`)

    const preservedName = `${unique}-继续编辑的草稿`
    const preservedDocument = document('当前草稿不能被回退覆盖')
    const thirdDraftResponse = await request.put(`/api/hmi/layouts/${id}`, { headers, data: { name: preservedName, document: preservedDocument, revision: draft.revision } })
    expect(thirdDraftResponse.ok()).toBeTruthy()
    draft = await thirdDraftResponse.json()
    const savedDraftDocument = structuredClone(draft.document)
    await page.reload()
    await page.locator(`[data-test="layout-more-${id}"]`).click()
    await page.locator(`[data-test="layout-history-${id}"]`).click()
    await page.locator(`[data-test="layout-version-${firstPublication}"]`).click()
    await expect(page.locator('[data-test="layout-version-preview"]')).toBeVisible()
    await page.locator('[data-test="layout-rollback-start"]').click()
    await expect(page.locator('[data-test="layout-rollback-confirmation"]')).toContainText('当前已保存草稿将完整保留')
    await page.locator('[data-test="layout-lifecycle-note"] textarea').fill('验证后回退到首次发布版本')
    await expect(page.locator('[data-test="layout-lifecycle-submit"]')).toBeDisabled()
    await page.locator('[data-test="layout-rollback-ack"]').click()
    await page.locator('[data-test="layout-lifecycle-submit"]').click()
    await expect(dialog).toBeHidden()
    runtime = await (await request.get(`/api/hmi/layouts/${id}`, { headers })).json()
    const retainedDraft = await (await request.get(`/api/hmi/layouts/${id}?draft=true`, { headers })).json() as HmiLayout
    expect(runtime.name).toBe(unique)
    expect(runtime.revision).toBeGreaterThan(secondPublication)
    expect(runtime.document).toEqual(originalDocument)
    expect(retainedDraft.name).toBe(preservedName)
    expect(retainedDraft.document).toEqual(savedDraftDocument)
    expect(retainedDraft.hasDraftChanges).toBeTruthy()
    expect(retainedDraft.revision).toBeGreaterThan(draft.revision)
    const history = await (await request.get(`/api/hmi/layouts/${id}/versions`, { headers })).json() as HmiLayoutVersion[]
    expect(history[0]).toMatchObject({ kind: 'rollback', restoredFromRevision: firstPublication, note: '验证后回退到首次发布版本', revision: runtime.revision })
  } finally {
    await runtimePage?.close()
    const latest = await request.get(`/api/hmi/layouts/${id}?draft=true`, { headers })
    if (latest.ok()) await request.delete(`/api/hmi/layouts/${id}?revision=${(await latest.json()).revision}`, { headers })
  }
})

async function mockFixture(page: Page) {
  await page.addInitScript(() => {
    localStorage.setItem('scada_token', 'layout-lifecycle-test')
    localStorage.setItem('scada_user', JSON.stringify({ id: 1, userName: 'admin', displayName: '管理员', role: 'Admin', access: { global: ['View', 'Control', 'AckAlarm', 'Configure', 'ManageUsers', 'System'], areas: [] } }))
    localStorage.setItem('scada_locale', 'zh-CN')
    localStorage.removeItem('scada_workstation'); localStorage.removeItem('scada_workstation_id')
  })
  const draft: HmiLayout = { id: 501, name: '当前已保存草稿', revision: 7, updatedTs: Date.UTC(2026, 9, 29, 5), updatedBy: 'editor', document: document('草稿导航'), publishedRevision: 5, publishedName: '当前生产布局', hasDraftChanges: true }
  const old: HmiLayoutVersion = { revision: 2, name: '旧生产布局', publishedTs: Date.UTC(2026, 9, 28, 5), publishedBy: 'admin', note: '首次上线', kind: 'publish', document: document('旧版导航') }
  const current: HmiLayoutVersion = { revision: 5, name: '当前生产布局', publishedTs: Date.UTC(2026, 9, 29, 4), publishedBy: 'operator', note: '调整生产区域', kind: 'publish', document: document('当前运行导航') }
  const state = { draft, versions: [current, old], publishAttempts: 0, rollbackAttempts: 0, publishFailure: 0, rollbackConflict: false, draftReads: 0, submittedNotes: [] as string[] }
  await page.route('**/api/**', async route => {
    const url = new URL(route.request().url()), method = route.request().method(), pathname = url.pathname
    if (pathname === '/api/hmi/layouts') return route.fulfill({ json: [draft] })
    if (pathname === '/api/hmi/layouts/501' && method === 'GET') { state.draftReads++; return route.fulfill({ json: draft }) }
    if (pathname === '/api/hmi/layouts/501' && method === 'PUT') {
      Object.assign(draft, route.request().postDataJSON(), { revision: draft.revision + 1 })
      return route.fulfill({ json: draft })
    }
    if (pathname === '/api/hmi/layouts/501/versions') return route.fulfill({ json: state.versions })
    if (pathname.startsWith('/api/hmi/layouts/501/versions/')) return route.fulfill({ json: state.versions.find(version => version.revision === Number(pathname.split('/').at(-1))) })
    if (pathname === '/api/hmi/layouts/501/publish') {
      state.publishAttempts++; state.submittedNotes.push(route.request().postDataJSON().note)
      if (state.publishFailure) { const status = state.publishFailure; state.publishFailure = 0; return route.fulfill({ status, json: { message: '发布服务暂时不可用' } }) }
      draft.publishedRevision = ++draft.revision; draft.hasDraftChanges = false
      return route.fulfill({ json: draft })
    }
    if (pathname === '/api/hmi/layouts/501/rollback') {
      state.rollbackAttempts++
      if (state.rollbackConflict) return route.fulfill({ status: 409, json: { message: 'revision conflict' } })
      draft.publishedRevision = ++draft.revision
      return route.fulfill({ json: draft })
    }
    if (pathname === '/api/hmi/workstations') return route.fulfill({ json: [] })
    if (pathname === '/api/screens') return route.fulfill({ json: [] })
    if (pathname === '/api/auth/site-info') return route.fulfill({ json: { siteName: '测试项目', demoMode: false } })
    if (pathname === '/api/license') return route.fulfill({ json: { status: 'Active', blocked: false } })
    if (pathname === '/api/health') return route.fulfill({ json: { status: 'ok' } })
    if (pathname === '/api/alarms/summary') return route.fulfill({ json: { unack: 0, acked: 0, rtnUnack: 0, flood: false } })
    return route.fulfill({ json: [] })
  })
  return state
}

test('publication reloads the saved draft and retains the note after a transient failure for direct retry', async ({ page }) => {
  const state = await mockFixture(page)
  state.publishFailure = 500
  await page.goto('/config/layouts')
  // List data can already be stale when the operator starts publication review.
  state.draft.name = '服务器最新保存的草稿'
  await page.locator('[data-test="layout-publish-501"]').click()
  await expect(page.locator('[data-test="layout-lifecycle-dialog"]')).toContainText('服务器最新保存的草稿')
  await expect(page.locator('[data-test="layout-version-diff"]')).toContainText('草稿导航')
  expect(state.draftReads).toBeGreaterThan(0)
  const note = page.locator('[data-test="layout-lifecycle-note"] textarea')
  await note.fill('发布测试说明')
  await page.locator('[data-test="layout-lifecycle-submit"]').click()
  await expect(page.locator('[data-test="layout-lifecycle-error"]')).toContainText('发布服务暂时不可用')
  await expect(note).toHaveValue('发布测试说明')
  await expect(page.locator('[data-test="layout-lifecycle-submit"]')).toBeEnabled()
  await page.locator('[data-test="layout-lifecycle-submit"]').click()
  await expect(page.locator('[data-test="layout-lifecycle-dialog"]')).toBeHidden()
  expect(state.publishAttempts).toBe(2)
  expect(state.submittedNotes).toEqual(['发布测试说明', '发布测试说明'])
})

test('rollback conflicts keep confirmation and note, require reload, and never retry automatically', async ({ page }) => {
  const state = await mockFixture(page)
  state.rollbackConflict = true
  await page.goto('/config/layouts')
  await page.locator('[data-test="layout-more-501"]').click()
  await page.locator('[data-test="layout-history-501"]').click()
  await page.locator('[data-test="layout-version-2"]').click()
  await page.locator('[data-test="layout-rollback-start"]').click()
  await page.locator('[data-test="layout-lifecycle-note"] textarea').fill('回退验证说明')
  await page.locator('[data-test="layout-rollback-ack"]').click()
  await page.locator('[data-test="layout-lifecycle-submit"]').click()
  await expect(page.locator('[data-test="layout-lifecycle-error"]')).toContainText('重新载入审阅内容')
  await expect(page.locator('[data-test="layout-lifecycle-note"] textarea')).toHaveValue('回退验证说明')
  await expect(page.locator('[data-test="layout-rollback-confirmation"]')).toBeVisible()
  await expect(page.locator('[data-test="layout-lifecycle-submit"]')).toBeDisabled()
  expect(state.rollbackAttempts).toBe(1)
  // Switching history cannot silently clear a conflict against the stale saved draft.
  await page.locator('[data-test="layout-version-5"]').click()
  await page.locator('[data-test="layout-version-2"]').click()
  await expect(page.locator('[data-test="layout-lifecycle-error"]')).toContainText('重新载入审阅内容')
  state.rollbackConflict = false; state.draft.revision++
  await page.locator('[data-test="layout-lifecycle-reload"]').click()
  await page.locator('[data-test="layout-rollback-start"]').click()
  await expect(page.locator('[data-test="layout-lifecycle-note"] textarea')).toHaveValue('回退验证说明')
  await expect(page.locator('[data-test="layout-lifecycle-submit"]')).toBeDisabled()
  await page.locator('[data-test="layout-rollback-ack"]').click()
  await page.locator('[data-test="layout-lifecycle-submit"]').click()
  await expect(page.locator('[data-test="layout-lifecycle-dialog"]')).toBeHidden()
  expect(state.rollbackAttempts).toBe(2)
})

test('unfinished drafts remain saveable while publication validates references and geometry', async ({ page }) => {
  const state = await mockFixture(page)
  state.draft.document.monitors[0].regions.push({ id: '', kind: 'screen', row: -2, col: 1, rowSpan: 8, colSpan: 4, screenId: null })
  await page.goto('/config/layouts')
  await page.locator('[data-test="layouts"]').getByRole('button', { name: '编辑', exact: true }).click()
  await expect(page.locator('[data-test="layout-region-issues"]')).toBeVisible()
  await expect(page.locator('[data-test="layout-save"]')).toBeEnabled()
  await page.locator('[data-test="layout-save"]').click()
  await expect(page.locator('[data-test="layout-dialog"]')).toBeHidden()
  await page.locator('[data-test="layout-publish-501"]').click()
  await expect(page.locator('[data-test="layout-publish-issues"]')).toBeVisible()
  await expect(page.locator('[data-test="layout-publish-references"]')).toContainText('不可发布')
  await page.locator('[data-test="layout-lifecycle-note"] textarea').fill('未完成草稿不能发布')
  await expect(page.locator('[data-test="layout-lifecycle-submit"]')).toBeDisabled()
  expect(state.publishAttempts).toBe(0)
})

test('history and detailed comparison fit a narrow viewport and preserve every region difference', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 })
  const state = await mockFixture(page)
  state.draft.document.monitors[0].regions.push(
    { id: '', kind: 'navigation', title: '空ID区域的草稿差异', row: 1, col: 1, rowSpan: 1, colSpan: 1 },
    { id: '#2', kind: 'navigation', title: '真实井号ID区域的草稿差异', row: 2, col: 2, rowSpan: 1, colSpan: 1 }
  )
  await page.goto('/config/layouts')
  await page.locator('[data-test="layout-more-501"]').click()
  await page.locator('[data-test="layout-history-501"]').click()
  await page.locator('[data-test="layout-version-2"]').click()
  await page.locator('[data-test="layout-version-compare"]').click()
  await page.getByRole('option', { name: '与当前已保存草稿对比', exact: true }).click()
  await expect(page.locator('[data-test="layout-version-diff"]')).toContainText('空ID区域的草稿差异')
  await expect(page.locator('[data-test="layout-version-diff"]')).toContainText('真实井号ID区域的草稿差异')
  const dialog = page.locator('[data-test="layout-lifecycle-dialog"]'), box = (await dialog.boundingBox())!
  expect(box.x).toBeGreaterThanOrEqual(0)
  expect(box.x + box.width).toBeLessThanOrEqual(391)
  await page.locator('[data-test="layout-version-diff"]').scrollIntoViewIfNeeded()
  await page.screenshot({ path: screenshot('history-narrow'), fullPage: true })
  await dialog.getByRole('button', { name: '关闭', exact: true }).click()
  await expect(dialog).toBeHidden()
})
