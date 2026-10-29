import { test, expect, type Page } from '@playwright/test'
import type { HmiLayout, HmiLayoutDocument, HmiLayoutVersion, HmiWorkstation } from '../src/api/hmiLayouts'

const document = (title = '导航'): HmiLayoutDocument => ({ monitors: [{ name: '主屏', columns: 4, rows: 4, regions: [{ id: 'nav', kind: 'navigation', title, row: 1, col: 1, rowSpan: 4, colSpan: 4 }] }] })
const user = (id = 1, userName = 'admin') => ({ id, userName, displayName: userName, role: 'Admin', access: { global: ['View', 'Control', 'AckAlarm', 'Configure', 'ManageUsers', 'System'], areas: [] } })

async function fixture(page: Page) {
  page.on('dialog', dialog => void dialog.accept())
  await page.addInitScript(admin => {
    if (!localStorage.getItem('scada_token')) {
      localStorage.setItem('scada_token', 'layout-safety-test')
      localStorage.setItem('scada_user', JSON.stringify(admin))
      localStorage.setItem('scada_locale', 'zh-CN')
      localStorage.setItem('scada_workstation_id', '77'); localStorage.setItem('scada_workstation', 'OP-A')
    }
  }, user())
  const draft: HmiLayout = { id: 501, name: '生产布局草稿', revision: 7, publishedRevision: 4, publishedName: '生产运行布局', hasDraftChanges: true, document: document(), updatedTs: 1, updatedBy: 'admin' }
  const current: HmiLayoutVersion = { name: '生产运行布局', revision: 4, document: document('已发布导航'), publishedTs: 1, publishedBy: 'admin', note: '当前版本', kind: 'publish' }
  const old: HmiLayoutVersion = { ...current, revision: 2, document: document('旧版导航'), note: '旧版本' }
  const stations: HmiWorkstation[] = [
    { id: 77, name: 'OP-A', description: '主控室', layoutId: 501, allowControl: false, updatedTs: 1000, updatedBy: 'admin' },
    { id: 78, name: 'OP-B', description: '观察室', layoutId: 501, allowControl: true, updatedTs: 1000, updatedBy: 'admin' },
    { id: 79, name: 'OP-C', description: '其他布局', layoutId: null, allowControl: false, updatedTs: 1000, updatedBy: 'admin' }
  ]
  const state = { draft, stations, current, old, deleted: false, failStations: false, failDraftRead: false, draftReads: 0, publishAttempts: 0, rollbackAttempts: 0,
    layoutWrites: [] as { method: string; body: HmiLayout }[], stationWrites: [] as Record<string, unknown>[], layoutDeletes: [] as string[], stationDeletes: [] as string[] }
  await page.route('**/api/**', async route => {
    const url = new URL(route.request().url()), path = url.pathname, method = route.request().method()
    if (path === '/api/hmi/layouts' && method === 'GET') return route.fulfill({ json: state.deleted ? [] : [state.draft] })
    if (path === '/api/hmi/layouts' && method === 'POST') {
      const body = route.request().postDataJSON(); state.layoutWrites.push({ method, body })
      return route.fulfill({ json: { ...body, id: 601, revision: 1, publishedRevision: null, updatedTs: 1, updatedBy: 'admin' } })
    }
    if (path === '/api/hmi/layouts/501' && method === 'GET') {
      state.draftReads++
      if (state.failDraftRead) return route.fulfill({ status: 500, json: { message: '服务器读取失败' } })
      return state.deleted ? route.fulfill({ status: 404 }) : route.fulfill({ json: state.draft })
    }
    if (path === '/api/hmi/layouts/501' && method === 'PUT') {
      const body = route.request().postDataJSON(); state.layoutWrites.push({ method, body })
      if (body.revision !== state.draft.revision) return route.fulfill({ status: 409, json: { message: '布局已被他人修改' } })
      Object.assign(state.draft, body, { revision: state.draft.revision + 1 })
      return route.fulfill({ json: state.draft })
    }
    if (path === '/api/hmi/layouts/501' && method === 'DELETE') {
      state.layoutDeletes.push(url.searchParams.get('revision') ?? '')
      if (Number(url.searchParams.get('revision')) !== state.draft.revision) return route.fulfill({ status: 409, json: { message: '布局已被他人修改，请刷新并合并修改' } })
      state.deleted = true; return route.fulfill({ status: 204 })
    }
    if (path === '/api/hmi/workstations') return state.failStations ? route.fulfill({ status: 500, json: { message: '工位影响范围读取失败' } }) : route.fulfill({ json: state.stations })
    if (path === '/api/hmi/workstations/77' && method === 'PUT') {
      const body = route.request().postDataJSON(); state.stationWrites.push(body)
      if (body.expectedUpdatedTs !== state.stations[0].updatedTs) return route.fulfill({ status: 409, json: { message: '工作站已被他人修改，请重新加载后核对分配和控制权限' } })
      Object.assign(state.stations[0], body, { updatedTs: state.stations[0].updatedTs + 1 })
      return route.fulfill({ json: state.stations[0] })
    }
    if (path === '/api/hmi/workstations/77' && method === 'DELETE') {
      state.stationDeletes.push(url.searchParams.get('updatedTs') ?? '')
      if (Number(url.searchParams.get('updatedTs')) !== state.stations[0].updatedTs) return route.fulfill({ status: 409, json: { message: '工作站已被他人修改' } })
      state.stations.shift(); return route.fulfill({ status: 204 })
    }
    if (path === '/api/hmi/layouts/501/versions') return route.fulfill({ json: [state.current, state.old] })
    if (path === '/api/hmi/layouts/501/versions/4') return route.fulfill({ json: state.current })
    if (path === '/api/hmi/layouts/501/versions/2') return route.fulfill({ json: state.old })
    if (path === '/api/hmi/layouts/501/publish' || path === '/api/hmi/layouts/501/rollback') {
      if (path.endsWith('publish')) state.publishAttempts++; else state.rollbackAttempts++
      state.draft.revision++; state.draft.publishedRevision = state.draft.revision
      return route.fulfill({ json: state.draft })
    }
    if (path === '/api/auth/site-info') return route.fulfill({ json: { siteName: '测试项目', demoMode: false } })
    if (path === '/api/license') return route.fulfill({ json: { status: 'Active', blocked: false } })
    if (path === '/api/health') return route.fulfill({ json: { status: 'ok' } })
    if (path === '/api/alarms/summary') return route.fulfill({ json: { unack: 0, acked: 0, rtnUnack: 0, flood: false } })
    return route.fulfill({ json: [] })
  })
  return state
}

const cacheKeys = (page: Page) => page.evaluate(() => Object.keys(localStorage).filter(key => key.startsWith('scada_layout_recovery:')))
async function openEditor(page: Page) {
  await page.goto('/config/layouts')
  await page.locator('[data-test="layouts"]').getByRole('button', { name: '编辑', exact: true }).click()
  await expect(page.locator('[data-test="layout-dialog"]')).toBeVisible()
}
async function cacheEdit(page: Page, name = '本地未保存布局') {
  await page.locator('[data-test="layout-name"]').fill(name)
  await expect(page.locator('[data-test="layout-local-saved"]')).toBeVisible()
  await expect.poll(() => cacheKeys(page).then(keys => keys.length)).toBe(1)
}
async function confirm(page: Page) { await page.locator('.el-message-box').getByRole('button', { name: '确定', exact: true }).click() }
async function deleteLayout(page: Page) { await page.locator('[data-test="layout-more-501"]').click(); await page.locator('[data-test="layout-delete-501"]').click(); await confirm(page) }

test('layout deletion submits the reviewed revision and needs a fresh list after conflict', async ({ page }) => {
  const state = await fixture(page)
  await page.goto('/config/layouts')
  await expect(page.locator('[data-test="layout-more-501"]')).toBeVisible()
  state.draft.revision = 8
  await deleteLayout(page)
  await expect(page.locator('[data-test="layouts-action-error"]')).toContainText('已被他人修改')
  expect(state.layoutDeletes).toEqual(['7']); expect(state.deleted).toBe(false)
  await page.locator('[data-test="layouts-conflict-reload"]').click()
  await expect(page.locator('[data-test="layouts-action-error"]')).toBeHidden()
  await deleteLayout(page)
  await expect(page.locator('[data-test="layout-more-501"]')).toHaveCount(0)
  expect(state.layoutDeletes).toEqual(['7', '8'])
})

test('workstation deletion submits its reviewed stamp and cannot delete a changed assignment', async ({ page }) => {
  const state = await fixture(page)
  await page.goto('/config/layouts')
  const row = page.locator('[data-test="stations"]').getByRole('row').filter({ hasText: 'OP-A' })
  await expect(row).toBeVisible(); state.stations[0].updatedTs++
  await row.getByRole('button', { name: '删除', exact: true }).click(); await confirm(page)
  await expect(page.locator('[data-test="layouts-action-error"]')).toContainText('工作站已被他人修改')
  expect(state.stationDeletes).toEqual(['1000']); expect(state.stations[0].id).toBe(77)
  await page.locator('[data-test="layouts-conflict-reload"]').click()
  await expect(page.locator('[data-test="layouts-action-error"]')).toBeHidden()
  await row.getByRole('button', { name: '删除', exact: true }).click(); await confirm(page)
  await expect(row).toHaveCount(0); expect(state.stationDeletes).toEqual(['1000', '1001'])
})

test('a station conflict keeps local edits, blocks retries, and reloads current permissions explicitly', async ({ page }) => {
  const state = await fixture(page)
  await page.goto('/config/layouts')
  await page.locator('[data-test="stations"]').getByRole('row').filter({ hasText: 'OP-A' }).getByRole('button', { name: '编辑', exact: true }).click()
  await page.locator('[data-test="station-name"]').fill('OP-LOCAL')
  state.stations[0].allowControl = true; state.stations[0].description = '服务器最新分配'; state.stations[0].updatedTs++
  await page.locator('[data-test="station-save"]').click()
  await expect(page.locator('[data-test="station-save-error"]')).toContainText('已被他人修改')
  await expect(page.locator('[data-test="station-name"]')).toHaveValue('OP-LOCAL')
  await expect(page.locator('[data-test="station-save"]')).toBeDisabled()
  expect(state.stationWrites).toHaveLength(1); expect(state.stationWrites[0].expectedUpdatedTs).toBe(1000)
  await page.locator('[data-test="station-conflict-reload"]').click(); await confirm(page)
  await expect(page.locator('[data-test="station-name"]')).toHaveValue('OP-A')
  await expect(page.locator('[data-test="station-control"]').getByRole('switch')).toBeChecked()
  await page.locator('[data-test="station-save"]').click()
  await expect(page.locator('.station-editor')).toBeHidden()
  expect(state.stationWrites[1]).toMatchObject({ expectedUpdatedTs: 1001, allowControl: true, description: '服务器最新分配' })
})

test('unsaved edits survive reload, retain their cache on a read failure, and save only after recovery', async ({ page }) => {
  const state = await fixture(page)
  // Incomplete server drafts may contain null names and region identifiers.
  state.draft.document.monitors[0].name = null as unknown as string
  state.draft.document.monitors[0].regions[0].id = null as unknown as string
  await openEditor(page)
  await page.locator('[data-test="region-title"]').fill('本地导航标题')
  await cacheEdit(page)
  await page.reload()
  await expect(page.locator('[data-test="layout-local-drafts"]')).toContainText('本地未保存布局')
  await expect(page.locator('[data-test="layout-dialog"]')).toHaveCount(0)
  state.failDraftRead = true
  await page.locator('[data-test="layout-recover"]').click()
  await expect(page.locator('[data-test="layout-recovery-error"]')).toContainText('服务器读取失败')
  expect(await cacheKeys(page)).toHaveLength(1)
  state.failDraftRead = false
  await page.locator('[data-test="layout-recover"]').click()
  await expect(page.locator('[data-test="layout-name"]')).toHaveValue('本地未保存布局')
  await expect(page.locator('[data-test="region-title"]')).toHaveValue('本地导航标题')
  expect(state.layoutWrites).toHaveLength(0)
  await page.locator('[data-test="layout-save"]').click()
  await expect(page.locator('[data-test="layout-dialog"]')).toBeHidden()
  expect(state.layoutWrites).toHaveLength(1)
  expect(state.layoutWrites[0]).toMatchObject({ method: 'PUT', body: { revision: 7, name: '本地未保存布局' } })
  expect(await cacheKeys(page)).toHaveLength(0)
  await page.reload(); await expect(page.locator('[data-test="layout-local-drafts"]')).toHaveCount(0)
})

for (const change of ['revision', 'content', 'deleted'] as const) {
  test(`recovery becomes an independent draft when the original ${change} changed`, async ({ page }) => {
    const state = await fixture(page)
    await openEditor(page); await cacheEdit(page)
    if (change === 'revision') state.draft.revision++
    if (change === 'content') state.draft.document.monitors[0].regions[0].title = '服务器更新内容'
    if (change === 'deleted') state.deleted = true
    const latest = structuredClone(state.draft)
    await page.reload(); await page.locator('[data-test="layout-recover"]').click()
    if (change === 'revision') {
      await page.locator('.el-message-box').getByRole('button', { name: '取消', exact: true }).click()
      await expect(page.locator('[data-test="layout-dialog"]')).toHaveCount(0)
      expect(await cacheKeys(page)).toHaveLength(1)
      await page.locator('[data-test="layout-recover"]').click()
    }
    await expect(page.locator('.el-message-box')).toContainText(change === 'deleted' ? '原布局已删除' : '服务器当前')
    await page.locator('.el-message-box').getByRole('button', { name: '恢复为新草稿', exact: true }).click()
    await expect(page.locator('[data-test="layout-name"]')).toHaveValue('本地未保存布局 副本')
    await expect(page.locator('[data-test="region-title"]')).toHaveValue('导航')
    await page.locator('[data-test="layout-save"]').click(); await expect(page.locator('[data-test="layout-dialog"]')).toBeHidden()
    expect(state.layoutWrites).toHaveLength(1); expect(state.layoutWrites[0].method).toBe('POST')
    expect(state.layoutWrites[0].body.id).toBeUndefined(); expect(state.draft).toEqual(latest)
    expect(await cacheKeys(page)).toHaveLength(0)
  })
}

test('new layout edits are recoverable without an existing server ID', async ({ page }) => {
  const state = await fixture(page)
  await page.goto('/config/layouts'); await page.locator('[data-test="layout-new"]').click(); await cacheEdit(page, '新建的本地布局')
  await page.reload(); await page.locator('[data-test="layout-recover"]').click()
  await expect(page.locator('[data-test="layout-name"]')).toHaveValue('新建的本地布局')
  await page.locator('[data-test="layout-save"]').click(); await expect(page.locator('[data-test="layout-dialog"]')).toBeHidden()
  expect(state.layoutWrites[0]).toMatchObject({ method: 'POST', body: { name: '新建的本地布局', revision: 0 } })
  expect(await cacheKeys(page)).toHaveLength(0)
})

test('canceling discard keeps the edit and confirming discard removes the recovery copy', async ({ page }) => {
  await fixture(page); await openEditor(page); await cacheEdit(page)
  const dialog = page.locator('[data-test="layout-dialog"]')
  await dialog.getByRole('button', { name: '取消', exact: true }).click()
  await page.locator('.el-message-box').getByRole('button', { name: '取消', exact: true }).click()
  await expect(dialog).toBeVisible(); expect(await cacheKeys(page)).toHaveLength(1)
  await dialog.getByRole('button', { name: '取消', exact: true }).click(); await confirm(page)
  await expect(dialog).toBeHidden(); expect(await cacheKeys(page)).toHaveLength(0)
  await page.reload(); await expect(page.locator('[data-test="layout-local-drafts"]')).toHaveCount(0)
})

test('local drafts are isolated by account on the same browser origin', async ({ page }) => {
  await fixture(page); await openEditor(page); await cacheEdit(page, '管理员的本地编辑')
  await page.evaluate(other => localStorage.setItem('scada_user', JSON.stringify(other)), user(2, 'second-admin'))
  await page.reload(); await expect(page.locator('[data-test="layout-local-drafts"]')).toHaveCount(0)
  await page.locator('[data-test="layouts"]').getByRole('button', { name: '编辑', exact: true }).click()
  await page.locator('[data-test="layout-name"]').fill('另一账号的本地编辑')
  await expect(page.locator('[data-test="layout-local-saved"]')).toBeVisible()
  await expect.poll(() => cacheKeys(page).then(keys => keys.length)).toBe(2)
  await page.evaluate(admin => localStorage.setItem('scada_user', JSON.stringify(admin)), user())
  await page.reload()
  await expect(page.locator('[data-test="layout-local-drafts"]')).toContainText('管理员的本地编辑')
  await expect(page.locator('[data-test="layout-local-drafts"]')).not.toContainText('另一账号的本地编辑')
})

test('storage quota failures leave editing and manual server saves available', async ({ page }) => {
  const state = await fixture(page)
  await page.addInitScript(() => {
    const write = Storage.prototype.setItem
    Storage.prototype.setItem = function (key, value) { if (key.startsWith('scada_layout_recovery:')) throw new DOMException('full', 'QuotaExceededError'); write.call(this, key, value) }
  })
  await openEditor(page); await page.locator('[data-test="layout-name"]').fill('手动保存仍可用')
  await expect(page.locator('[data-test="layout-recovery-error"]')).toContainText('无法读写')
  await expect(page.locator('[data-test="layout-local-saved"]')).toHaveCount(0)
  await page.locator('[data-test="layout-save"]').click(); await expect(page.locator('[data-test="layout-dialog"]')).toBeHidden()
  expect(state.layoutWrites[0].body.name).toBe('手动保存仍可用')
})

test('corrupt local data is retained and cannot crash the catalog or create an unsafe editor', async ({ page }) => {
  await fixture(page); await openEditor(page); await cacheEdit(page)
  await page.evaluate(() => {
    const key = Object.keys(localStorage).find(key => key.startsWith('scada_layout_recovery:'))!
    const record = JSON.parse(localStorage.getItem(key)!)
    record.content.document.monitors[0].columns = 1e12
    localStorage.setItem(key, JSON.stringify(record))
    localStorage.setItem('scada_layout_recovery:v1:1%3Aadmin:broken', '{invalid')
  })
  await page.reload()
  await expect(page.locator('[data-test="layout-recovery-error"]')).toBeVisible()
  await expect(page.locator('[data-test="layout-more-501"]')).toBeVisible()
  await expect(page.locator('[data-test="layout-recover"]')).toHaveCount(0)
  expect(await cacheKeys(page)).toHaveLength(2)
})

for (const operation of ['publish', 'rollback'] as const) {
  test(`${operation} shows current assignments and rechecks them before committing`, async ({ page }) => {
    const state = await fixture(page); await page.goto('/config/layouts')
    if (operation === 'publish') await page.locator('[data-test="layout-publish-501"]').click()
    else {
      await page.locator('[data-test="layout-more-501"]').click(); await page.locator('[data-test="layout-history-501"]').click()
      await page.locator('[data-test="layout-version-2"]').click(); await page.locator('[data-test="layout-rollback-start"]').click()
      await page.locator('[data-test="layout-rollback-ack"]').click()
    }
    const impact = page.locator('[data-test="layout-impact"]')
    await expect(impact).toContainText('生产运行布局 · V4')
    await expect(impact).toContainText('已分配 2 个工作站')
    await expect(impact).toContainText('主控室'); await expect(impact).toContainText('观察室')
    await expect(impact).toContainText('本机'); await expect(impact).not.toContainText('OP-C')
    await expect(impact).toContainText('未回传各工位实际应用的版本')
    await page.locator('[data-test="layout-lifecycle-note"] textarea').fill('保留审阅说明')
    state.stations[0].allowControl = true; state.stations[0].updatedTs++
    await page.locator('[data-test="layout-lifecycle-submit"]').click()
    await expect(page.locator('[data-test="layout-impact-changed"]')).toBeVisible()
    expect(state.publishAttempts + state.rollbackAttempts).toBe(0)
    await expect(page.locator('[data-test="layout-lifecycle-note"] textarea')).toHaveValue('保留审阅说明')
    await page.locator('[data-test="layout-lifecycle-submit"]').click()
    await expect(page.locator('[data-test="layout-lifecycle-dialog"]')).toBeHidden()
    expect(operation === 'publish' ? state.publishAttempts : state.rollbackAttempts).toBe(1)
  })
}

test('unknown workstation impact blocks publication and retry preserves the review note', async ({ page }) => {
  const state = await fixture(page); state.failStations = true
  await page.goto('/config/layouts'); await page.locator('[data-test="layout-publish-501"]').click()
  await expect(page.locator('[data-test="layout-impact-error"]')).toContainText('读取失败')
  await page.locator('[data-test="layout-lifecycle-note"] textarea').fill('等待工位恢复读取')
  await expect(page.locator('[data-test="layout-lifecycle-submit"]')).toBeDisabled()
  await expect(page.locator('[data-test="layout-impact-empty"]')).toHaveCount(0)
  state.failStations = false; await page.locator('[data-test="layout-impact-refresh"]').click()
  await expect(page.locator('[data-test="layout-impact-error"]')).toHaveCount(0)
  await expect(page.locator('[data-test="layout-impact-changed"]')).toHaveCount(0)
  await expect(page.locator('[data-test="layout-lifecycle-note"] textarea')).toHaveValue('等待工位恢复读取')
  await page.setViewportSize({ width: 390, height: 844 })
  await page.screenshot({ path: test.info().outputPath('publication-impact-mobile.png'), animations: 'disabled' })
  await page.locator('[data-test="layout-lifecycle-submit"]').click()
  await expect(page.locator('[data-test="layout-lifecycle-dialog"]')).toBeHidden(); expect(state.publishAttempts).toBe(1)
})

test('two editor windows keep independent recovery copies and saving one preserves the other', async ({ page }) => {
  await fixture(page); await openEditor(page); await cacheEdit(page, '第一个窗口的编辑')
  const other = await page.context().newPage()
  await fixture(other); await openEditor(other)
  await other.locator('[data-test="layout-name"]').fill('第二个窗口的编辑')
  await expect(other.locator('[data-test="layout-local-saved"]')).toBeVisible()
  await expect.poll(() => cacheKeys(page).then(keys => keys.length)).toBe(2)
  await other.close(); await page.reload()
  await expect(page.locator('[data-test="layout-recovery-item"]')).toHaveCount(2)
  await page.locator('[data-test="layout-recovery-item"]').filter({ hasText: '第一个窗口的编辑' }).locator('[data-test="layout-recover"]').click()
  await page.locator('[data-test="layout-save"]').click(); await expect(page.locator('[data-test="layout-dialog"]')).toBeHidden()
  expect(await cacheKeys(page)).toHaveLength(1)
  await expect(page.locator('[data-test="layout-local-drafts"]')).toContainText('第二个窗口的编辑')
})

test('an impact read failure during submission retains the note and prevents any publication', async ({ page }) => {
  const state = await fixture(page); await page.goto('/config/layouts')
  await page.locator('[data-test="layout-publish-501"]').click()
  await page.locator('[data-test="layout-lifecycle-note"] textarea').fill('提交前断线也需保留')
  state.failStations = true; await page.locator('[data-test="layout-lifecycle-submit"]').click()
  await expect(page.locator('[data-test="layout-impact-error"]')).toBeVisible()
  await expect(page.locator('[data-test="layout-lifecycle-submit"]')).toBeDisabled()
  await expect(page.locator('[data-test="layout-lifecycle-note"] textarea')).toHaveValue('提交前断线也需保留')
  expect(state.publishAttempts).toBe(0)
  state.failStations = false; await page.locator('[data-test="layout-impact-refresh"]').click()
  await expect(page.locator('[data-test="layout-lifecycle-error"]')).toHaveCount(0)
  await page.locator('[data-test="layout-lifecycle-submit"]').click()
  await expect(page.locator('[data-test="layout-lifecycle-dialog"]')).toBeHidden(); expect(state.publishAttempts).toBe(1)
})
