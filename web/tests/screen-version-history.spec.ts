import { test, expect, type APIRequestContext, type Page, type Route } from './fixtures'
import { refreshRuntime } from './runtime-menu'
import type { ScreenDocument, ScreenNode, ScreenSummary } from '../src/screens/model'
import type { ScreenVersion, ScreenVersionSummary } from '../src/api/screens'

const screenId = 91001
const screenPath = `/api/screens/${screenId}`
const dialogSelector = '[data-test="screen-versions-dialog"]'
const historyButton = '[data-test="designer-version-history"]'

type RollbackRequest = { revision: number; targetRevision: number; note: string }
type SaveRequest = { revision: number; name: string; document: ScreenDocument }

function node(id: string, type: ScreenNode['type'], extras: Partial<ScreenNode> = {}): ScreenNode {
  return { id, type, x: 40, y: 40, width: 240, height: 70, rotation: 0, text: id, color: '#7394b8', activeColor: '#34d399',
    unit: '', decimals: 1, min: 0, max: 100, activeThreshold: .5, tagId: null,
    action: { type: 'none', tagId: null, screenId: null, value: 1 }, ...extras }
}
function document(text: string): ScreenDocument {
  return { schemaVersion: 1, width: 1200, height: 720, background: '#101b2d', nodes: [node('label', 'text', { text })] }
}
function summary(revision: number): ScreenVersionSummary {
  return { revision, name: `历史画面${revision}`, publishedBy: `operator-${revision}`, publishedTs: 1700000000000 + revision,
    note: `发布说明${revision}`, kind: 'publish', restoredFromRevision: null }
}
function version(revision: number): ScreenVersion {
  return { ...summary(revision), document: document(`历史内容${revision}`), requiresRepublish: false }
}
function deferred() {
  let resolve!: () => void
  const promise = new Promise<void>(done => { resolve = done })
  return { promise, resolve }
}
async function json(route: Route, body: unknown, status = 200) {
  await route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) })
}
async function flushBrowser(page: Page) {
  await page.evaluate(() => new Promise<void>(resolve => requestAnimationFrame(() => { requestAnimationFrame(() => resolve()) })))
}
async function setup(page: Page, request: APIRequestContext) {
  const response = await request.post('/api/auth/login', { data: { userName: 'admin', password: 'admin123' } })
  expect(response.ok()).toBeTruthy()
  const auth: { token: string; user: { id: number } } = await response.json()
  const state = { revision: 30, publishedRevision: 20, name: '当前草稿名称', document: document('当前草稿内容'), draftReads: 0,
    rollbacks: [] as RollbackRequest[], saves: [] as SaveRequest[], publishes: [] as { revision: number }[], commands: 0 }
  const backupKey = `scada:screen-draft:v1:${auth.user.id}:${screenId}:other-tab`
  const backupRaw = JSON.stringify({ version: 1, baseRevision: 30, savedAt: Date.now(),
    baseSnapshot: JSON.stringify({ name: state.name, document: state.document }), snapshot: JSON.stringify({ name: '其他标签页编辑', document: state.document }) })
  await page.addInitScript(({ auth, backupKey, backupRaw }) => {
    localStorage.setItem('scada_token', auth.token)
    localStorage.setItem('scada_user', JSON.stringify(auth.user))
    localStorage.setItem('scada_locale', 'zh-CN')
    localStorage.setItem(backupKey, backupRaw)
  }, { auth, backupKey, backupRaw })
  await page.route(url => url.pathname === '/api/screens' || url.pathname.startsWith(screenPath), async route => {
    const url = new URL(route.request().url()), method = route.request().method()
    if (url.pathname === '/api/screens') return json(route, [{ id: screenId, name: state.name, revision: state.revision, publishedRevision: state.publishedRevision }])
    if (url.pathname === screenPath && method === 'GET') {
      state.draftReads++
      return json(route, { id: screenId, ...state, updatedBy: 'admin', updatedTs: Date.now() })
    }
    if (url.pathname === screenPath && method === 'PUT') {
      const body: SaveRequest = route.request().postDataJSON()
      state.saves.push(body); state.name = body.name; state.document = body.document
      return json(route, { revision: ++state.revision })
    }
    if (url.pathname === `${screenPath}/versions`) {
      expect(url.searchParams.get('limit')).toBe('50')
      return json(route, [summary(20), { ...summary(10), kind: 'rollback', restoredFromRevision: 5 }])
    }
    const match = url.pathname.match(/\/versions\/(\d+)$/)
    if (match) return json(route, version(Number(match[1])))
    if (url.pathname === `${screenPath}/rollback`) {
      state.rollbacks.push(route.request().postDataJSON()); state.publishedRevision = ++state.revision
      return json(route, { revision: state.revision, publishedRevision: state.publishedRevision, warning: '快照备份警告' })
    }
    if (url.pathname === `${screenPath}/publish`) {
      state.publishes.push(route.request().postDataJSON()); state.publishedRevision = ++state.revision
      return json(route, { revision: state.revision, publishedRevision: state.publishedRevision })
    }
    if (url.pathname.includes('/actions/')) { state.commands++; return json(route, { success: true }) }
    return route.continue()
  })
  await page.goto(`/config/screens/${screenId}`)
  await expect(page.locator(historyButton)).toBeVisible()
  return { state, backupKey, backupRaw }
}
async function open(page: Page, revision = 10) {
  await page.locator(historyButton).click()
  const dialog = page.locator(dialogSelector)
  await dialog.locator(`[data-test="screen-version-${revision}"]`).click()
  await expect(dialog.locator('[data-test="screen-version-detail-heading"]')).toContainText(`r${revision}`)
  return dialog
}
async function acknowledge(page: Page, note = ' 恢复已验收运行画面 ') {
  const dialog = page.locator(dialogSelector)
  await dialog.getByRole('textbox', { name: '回退说明', exact: true }).fill(note)
  await dialog.locator('[data-test="screen-rollback-ack"]').click()
}

test('historical preview uses frozen content and cannot execute commands', async ({ page, request }) => {
  let libraryRequests = 0
  await page.route('**/api/hmi/library**', route => { libraryRequests++; return json(route, { message: '当前图库不可用' }, 503) })
  const { state } = await setup(page, request)
  const frozen = version(10)
  frozen.document.nodes.push(node('command', 'button', { y: 160, text: '历史控制', action: { type: 'command', tagId: 1, screenId: null, value: 1 } }),
    node('input', 'input', { y: 260, text: '写入', action: { type: 'command', tagId: 1, screenId: null, value: 1 } }),
    node('symbol', 'symbol', { x: 400, symbol: 'user:history-test' }))
  frozen.document.symbolAssets = { 'user:history-test': '<svg viewBox="0 0 100 100"><rect data-history-frozen="yes" width="80" height="80" fill="currentColor"/></svg>' }
  await page.route(`**${screenPath}/versions/10`, route => json(route, frozen))
  const dialog = await open(page)
  await expect(dialog.locator('[data-test="screen-version-current"]')).toHaveCount(1)
  await expect(dialog.locator('[data-test="screen-version-10"]')).toContainText('回退来源：r5')
  const requestsBeforePreview = libraryRequests
  await dialog.locator('[data-test="screen-version-preview"]').click()
  const preview = dialog.locator('[data-test="screen-version-preview-content"]')
  await expect(preview.locator('[data-node-id="label"]')).toContainText('历史内容10')
  await expect(preview.locator('[data-history-frozen="yes"]')).toBeVisible()
  await expect(preview.locator('[data-test="runtime-library-error"]')).toHaveCount(0)
  await expect(preview.locator('[data-node-id="input"] input')).toBeDisabled()
  await expect(preview.locator('[data-test="runtime-edit"]')).toHaveCount(0)
  await preview.locator('[data-node-id="command"]').click()
  await expect(preview.locator('[data-test="runtime-command-error"]')).toBeVisible()
  await expect(page.getByRole('dialog')).toHaveCount(1)
  expect(state.commands).toBe(0)
  expect(state.rollbacks).toEqual([])
  await refreshRuntime(preview)
  await expect(preview.locator('[data-history-frozen="yes"]')).toBeVisible()
  expect(libraryRequests).toBe(requestsBeforePreview)
})

test('rollback adopts the new revision while preserving draft name content undo saved state and other-tab backup', async ({ page, request }) => {
  const { state, backupKey, backupRaw } = await setup(page, request)
  const name = page.getByRole('textbox', { name: '画面名称', exact: true })
  await name.fill('需保留的草稿名称')
  await page.getByRole('button', { name: '保存草稿', exact: true }).click()
  await expect(page.locator('.revision')).toContainText('31')
  const draft = structuredClone(state.document)
  const reads = state.draftReads
  await open(page)
  await acknowledge(page)
  await page.locator('[data-test="screen-rollback-submit"]').click()
  await expect(page.locator(dialogSelector)).not.toBeVisible()
  await expect(page.locator('[data-test="designer-rollback-success"]')).toHaveText('已回退运行版本，草稿未更改')
  await expect(page.locator('[data-test="designer-rollback-warning"]')).toContainText('快照备份警告')
  await expect(name).toHaveValue('需保留的草稿名称')
  await expect(page.locator('[data-testid="designer-stage"] [data-node-id="label"]')).toContainText('当前草稿内容')
  await expect(page.locator('.revision')).toContainText('r32')
  await expect(page.locator('.designer-toolbar .el-tag')).toHaveText('已保存')
  expect(state.rollbacks).toEqual([{ revision: 31, targetRevision: 10, note: '恢复已验收运行画面' }])
  expect(state.document).toEqual(draft)
  expect(state.draftReads).toBe(reads)
  expect(await page.evaluate(key => localStorage.getItem(key), backupKey)).toBe(backupRaw)
  await page.getByRole('button', { name: '撤销', exact: true }).click()
  await expect(name).toHaveValue('当前草稿名称')
  await expect(page.locator('.designer-toolbar .el-tag')).toHaveText('未保存')
  await page.getByRole('button', { name: '重做', exact: true }).click()
  await expect(name).toHaveValue('需保留的草稿名称')
  await expect(page.locator('.designer-toolbar .el-tag')).toHaveText('已保存')
  await page.getByRole('button', { name: '发布', exact: true }).click()
  await expect.poll(() => state.publishes.length).toBe(1)
  expect(state.publishes[0]).toEqual({ revision: 32 })
  expect(state.saves).toHaveLength(1)
  await expect(page.locator('[data-test="designer-rollback-success"]')).toHaveCount(0)
  await expect(page.locator('[data-test="designer-rollback-warning"]')).toHaveCount(0)
})

test('rollback requires a trimmed 1–500 character note and explicit confirmation and locks submission', async ({ page, request }) => {
  const { state } = await setup(page, request)
  const gate = deferred(), started = deferred()
  await page.route(`**${screenPath}/rollback`, async route => {
    state.rollbacks.push(route.request().postDataJSON()); started.resolve()
    await gate.promise
    await json(route, { revision: 31, publishedRevision: 31 })
  })
  const dialog = await open(page)
  const note = dialog.getByRole('textbox', { name: '回退说明', exact: true }), submit = dialog.locator('[data-test="screen-rollback-submit"]')
  await dialog.locator('[data-test="screen-rollback-ack"]').click()
  await expect(submit).toBeDisabled()
  await note.fill('   ')
  await expect(submit).toBeDisabled()
  await expect(dialog).toContainText('请输入非空的回退说明')
  await note.fill('x'.repeat(501))
  await expect(submit).toBeDisabled()
  await expect(dialog).toContainText('回退说明不能超过 500 字')
  await note.fill(' 有效说明 ')
  await dialog.locator('[data-test="screen-rollback-ack"]').click()
  await expect(submit).toBeDisabled()
  await dialog.locator('[data-test="screen-rollback-ack"]').click()
  try {
    await submit.click()
    await started.promise
    await expect(note).toBeDisabled()
    await expect(submit).toBeDisabled()
    await expect(dialog.locator('[data-test="screen-versions-close"]')).toBeDisabled()
    await expect(dialog.locator('[data-test="screen-version-20"]')).toBeDisabled()
    await expect(page.locator(historyButton)).toBeDisabled()
    await page.keyboard.press('Escape')
    await expect(dialog).toBeVisible()
    await submit.evaluate(element => { if (element instanceof HTMLButtonElement) { element.click(); element.click() } })
    expect(state.rollbacks).toEqual([{ revision: 30, targetRevision: 10, note: '有效说明' }])
  } finally { gate.resolve() }
  await expect(dialog).not.toBeVisible()
})

test('unsaved draft edits block rollback before opening and remain live while history is open', async ({ page, request }) => {
  const { state } = await setup(page, request)
  const name = page.getByRole('textbox', { name: '画面名称', exact: true })
  await name.fill('未保存编辑')
  const dialog = await open(page)
  await expect(dialog.locator('[data-test="screen-versions-dirty"]')).toContainText('不能回退')
  await dialog.locator('[data-test="screen-version-preview"]').click()
  await expect(dialog.locator('[data-test="screen-version-preview-content"]')).toBeVisible()
  await dialog.locator('[data-test="screen-versions-close"]').click()
  await name.fill('当前草稿名称')
  await open(page)
  await acknowledge(page)
  await expect(dialog.locator('[data-test="screen-rollback-submit"]')).toBeEnabled()
  await name.evaluate(element => {
    if (!(element instanceof HTMLInputElement)) throw new Error('Expected designer name input')
    element.value = '历史打开后产生的编辑'; element.dispatchEvent(new Event('input', { bubbles: true }))
  })
  await expect(dialog.locator('[data-test="screen-versions-dirty"]')).toBeVisible()
  await expect(dialog.locator('[data-test="screen-rollback-submit"]')).toBeDisabled()
  await dialog.locator('[data-test="screen-versions-close"]').click()
  await expect(name).toHaveValue('历史打开后产生的编辑')
  expect(state.rollbacks).toEqual([])
  expect(state.saves).toEqual([])
})

test('legacy versions explicitly block preview and rollback without expanding the current library', async ({ page, request }) => {
  const { state } = await setup(page, request)
  const legacy = version(10)
  legacy.requiresRepublish = true
  legacy.document.nodes = [node('unfrozen-template', 'template', { templateKey: 'current-library-must-not-be-used', bindings: [] })]
  await page.route(`**${screenPath}/versions/10`, route => json(route, legacy))
  const dialog = await open(page)
  await expect(dialog.locator('[data-test="screen-version-legacy"]')).toContainText('不会使用当前图库猜测历史内容')
  await expect(dialog.locator('[data-test="screen-version-preview"]')).toBeDisabled()
  await expect(dialog.getByRole('textbox', { name: '回退说明', exact: true })).toBeDisabled()
  await expect(dialog.locator('[data-test="screen-rollback-submit"]')).toBeDisabled()
  await expect(dialog.locator('[data-testid="screen-runtime"]')).toHaveCount(0)
  expect(state.rollbacks).toEqual([])
})

test('list and older-page failures remain visible and retry with the correct revision cursor', async ({ page, request }) => {
  await setup(page, request)
  let firstFailure = true, olderFailure = true
  const cursors: (string | null)[] = []
  await page.route(url => url.pathname === `${screenPath}/versions`, route => {
    const url = new URL(route.request().url()), before = url.searchParams.get('beforeRevision')
    expect(url.searchParams.get('limit')).toBe('50'); cursors.push(before)
    if (firstFailure) { firstFailure = false; return json(route, { message: '历史服务暂不可用' }, 500) }
    if (before && olderFailure) { olderFailure = false; return json(route, { message: '旧版本分页读取失败' }, 503) }
    return json(route, before ? [summary(50)] : Array.from({ length: 50 }, (_, index) => summary(100 - index)))
  })
  await page.locator(historyButton).click()
  const dialog = page.locator(dialogSelector)
  await expect(dialog.locator('[data-test="screen-versions-list-error"]')).toContainText('历史服务暂不可用')
  await expect(dialog.locator('[data-test="screen-versions-empty"]')).toHaveCount(0)
  await dialog.locator('[data-test="screen-versions-list-retry"]').click()
  await expect(dialog.locator('[data-test="screen-version-100"]')).toBeVisible()
  await dialog.locator('[data-test="screen-versions-more"]').click()
  await expect(dialog.locator('[data-test="screen-versions-list-error"]')).toContainText('旧版本分页读取失败')
  await expect(dialog.locator('[data-test="screen-version-100"]')).toBeVisible()
  await dialog.locator('[data-test="screen-versions-list-retry"]').click()
  await expect(dialog.locator('[data-test="screen-version-50"]')).toHaveCount(1)
  await expect(dialog.locator('[data-test="screen-versions-more"]')).toHaveCount(0)
  expect(cursors).toEqual([null, null, '51', '51'])
})

test('detail failures and rollback validation issues can be reviewed and explicitly retried', async ({ page, request }) => {
  const { state } = await setup(page, request)
  let detailFailure = true, rollbackFailure = true
  await page.route(`**${screenPath}/versions/10`, route => {
    if (detailFailure) { detailFailure = false; return json(route, { message: '版本详情读取失败' }, 500) }
    return json(route, version(10))
  })
  await page.route(`**${screenPath}/rollback`, route => {
    state.rollbacks.push(route.request().postDataJSON())
    if (rollbackFailure) { rollbackFailure = false; return json(route, { message: '当前引用校验失败', issues: [{ nodeId: 'old-command', message: '测点不再可写' }] }, 400) }
    return json(route, { revision: 31, publishedRevision: 31 })
  })
  await page.locator(historyButton).click()
  const dialog = page.locator(dialogSelector)
  await dialog.locator('[data-test="screen-version-10"]').click()
  await expect(dialog.locator('[data-test="screen-version-detail-error"]')).toContainText('版本详情读取失败')
  await expect(dialog.locator('[data-test="screen-rollback-submit"]')).toBeDisabled()
  await dialog.locator('[data-test="screen-version-detail-retry"]').click()
  await expect(dialog.locator('[data-test="screen-version-detail-heading"]')).toContainText('r10')
  await acknowledge(page)
  await dialog.locator('[data-test="screen-rollback-submit"]').click()
  await expect(dialog.locator('[data-test="screen-rollback-error"]')).toContainText('当前引用校验失败')
  await expect(dialog.locator('[data-test="screen-rollback-issues"]')).toContainText('old-command: 测点不再可写')
  await expect(dialog.getByRole('textbox', { name: '回退说明', exact: true })).toHaveValue(' 恢复已验收运行画面 ')
  await expect(dialog.locator('[data-test="screen-rollback-submit"]')).toBeDisabled()
  await dialog.locator('[data-test="screen-rollback-ack"]').click()
  await dialog.locator('[data-test="screen-rollback-submit"]').click()
  await expect(dialog).not.toBeVisible()
  expect(state.rollbacks).toHaveLength(2)
})

test('rollback conflict never retries automatically or hides the required designer reload', async ({ page, request }) => {
  const { state } = await setup(page, request)
  await page.route(`**${screenPath}/rollback`, route => {
    state.rollbacks.push(route.request().postDataJSON())
    return json(route, { message: '服务器草稿修订已改变' }, 409)
  })
  const dialog = await open(page)
  await acknowledge(page)
  await dialog.locator('[data-test="screen-rollback-submit"]').click()
  await expect(dialog.locator('[data-test="screen-versions-conflict"]')).toContainText('不会自动重试或覆盖本地编辑')
  await expect(dialog.locator('[data-test="screen-rollback-error"]')).toContainText('服务器草稿修订已改变')
  await dialog.locator('[data-test="screen-versions-refresh"]').click()
  await expect(dialog.locator('[data-test="screen-version-detail-heading"]')).toBeVisible()
  await expect(dialog.locator('[data-test="screen-rollback-submit"]')).toBeDisabled()
  await dialog.locator('[data-test="screen-versions-close"]').click()
  await open(page)
  await expect(dialog.locator('[data-test="screen-versions-conflict"]')).toBeVisible()
  expect(state.rollbacks).toHaveLength(1)
  expect(state.draftReads).toBe(1)
})

test('a missing rollback target is shown without discarding the draft and can be reselected', async ({ page, request }) => {
  const { state } = await setup(page, request)
  await page.route(`**${screenPath}/rollback`, route => {
    state.rollbacks.push(route.request().postDataJSON())
    return json(route, { message: '目标历史版本已不存在' }, 404)
  })
  const dialog = await open(page)
  await acknowledge(page)
  await dialog.locator('[data-test="screen-rollback-submit"]').click()
  await expect(dialog.locator('[data-test="screen-rollback-error"]')).toContainText('目标历史版本已不存在')
  await dialog.locator('[data-test="screen-version-20"]').click()
  await expect(dialog.locator('[data-test="screen-version-detail-heading"]')).toContainText('r20')
  await dialog.locator('[data-test="screen-versions-close"]').click()
  await expect(page.getByRole('textbox', { name: '画面名称', exact: true })).toHaveValue('当前草稿名称')
  expect(state.rollbacks).toHaveLength(1)
})

test('fast version switching ignores an older detail response', async ({ page, request }) => {
  await setup(page, request)
  const gate = deferred(), started = deferred()
  await page.route(`**${screenPath}/versions/20`, async route => {
    started.resolve(); await gate.promise
    await json(route, { ...version(20), name: '不应覆盖当前选择的旧响应' })
  })
  try {
    await page.locator(historyButton).click()
    await started.promise
    const dialog = page.locator(dialogSelector)
    await dialog.locator('[data-test="screen-version-10"]').click()
    await expect(dialog.locator('[data-test="screen-version-detail-heading"]')).toContainText('r10')
    const stale = page.waitForResponse(response => new URL(response.url()).pathname === `${screenPath}/versions/20`)
    gate.resolve(); await (await stale).finished(); await flushBrowser(page)
    await expect(dialog.locator('[data-test="screen-version-10"]')).toHaveAttribute('aria-pressed', 'true')
    await dialog.locator('[data-test="screen-version-preview"]').click()
    await expect(dialog.locator('[data-node-id="label"]')).toContainText('历史内容10')
    await expect(dialog.locator('[data-test="screen-version-detail-heading"]')).not.toContainText('旧响应')
  } finally { gate.resolve() }
})

test('closing and reopening history invalidates outstanding detail successes and failures', async ({ page, request }) => {
  await setup(page, request)
  const gates = [deferred(), deferred()], starts = [deferred(), deferred()]
  let pending = 0
  await page.route(`**${screenPath}/versions/20`, async route => {
    const index = pending++
    if (index >= gates.length) return json(route, version(20))
    starts[index].resolve(); await gates[index].promise
    if (index === 0) await json(route, { ...version(20), name: '上次打开的旧详情' })
    else await json(route, { message: '关闭后的旧错误' }, 500)
  })
  try {
    await page.locator(historyButton).click(); await starts[0].promise
    const dialog = page.locator(dialogSelector)
    await dialog.locator('[data-test="screen-versions-close"]').click()
    await page.locator(historyButton).click(); await starts[1].promise
    await dialog.locator('[data-test="screen-versions-close"]').click()
    await open(page)
    const success = page.waitForResponse(response => new URL(response.url()).pathname === `${screenPath}/versions/20` && response.status() === 200)
    const failure = page.waitForResponse(response => new URL(response.url()).pathname === `${screenPath}/versions/20` && response.status() === 500)
    gates.forEach(gate => gate.resolve())
    await Promise.all([success, failure].map(async pending => (await pending).finished())); await flushBrowser(page)
    await expect(dialog.locator('[data-test="screen-version-detail-heading"]')).toContainText('r10')
    await expect(dialog.locator('[data-test="screen-version-detail-heading"]')).not.toContainText('旧详情')
    await expect(dialog.locator('[data-test="screen-version-detail-error"]')).toHaveCount(0)
  } finally { gates.forEach(gate => gate.resolve()) }
})

test('historical preview navigation and popups read published targets and remain read only', async ({ page, request }) => {
  const { state } = await setup(page, request)
  const frozen = version(10)
  frozen.document.nodes.push(node('navigate', 'button', { y: 180, action: { type: 'navigate', screenId: 91002, tagId: null, value: 1 } }),
    node('popup', 'button', { y: 280, action: { type: 'popup', screenId: 91003, tagId: null, value: 1 } }))
  await page.route(`**${screenPath}/versions/10`, route => json(route, frozen))
  const reads: { id: number; draft: string | null }[] = []
  await page.route(url => /^\/api\/screens\/9100[23]$/.test(url.pathname), route => {
    const url = new URL(route.request().url()), id = Number(url.pathname.split('/').at(-1))
    reads.push({ id, draft: url.searchParams.get('draft') })
    const target = document(`目标发布内容${id}`)
    target.nodes.push(node('target-command', 'button', { y: 180, action: { type: 'command', tagId: 1, screenId: null, value: 1 } }))
    return json(route, { id, name: `目标${id}`, revision: 3, publishedRevision: 3, document: target })
  })
  const dialog = await open(page)
  await dialog.locator('[data-test="screen-version-preview"]').click()
  const preview = dialog.locator('[data-test="screen-version-preview-content"]')
  await preview.locator('[data-node-id="navigate"]').click()
  await expect(preview.locator('[data-node-id="label"]')).toContainText('目标发布内容91002')
  await preview.locator('[data-node-id="target-command"]').click()
  await expect(preview.locator('[data-test="runtime-command-error"]')).toBeVisible()
  await preview.getByRole('button', { name: '返回预览首页', exact: true }).click()
  await preview.locator('[data-node-id="popup"]').click()
  const popup = page.getByRole('dialog').filter({ has: page.locator('[data-node-id="target-command"]') })
  await expect(popup.locator('[data-node-id="label"]')).toContainText('目标发布内容91003')
  await popup.locator('[data-node-id="target-command"]').click()
  await expect(popup.locator('[data-test="runtime-command-error"]')).toBeVisible()
  expect(reads).toEqual([{ id: 91002, draft: 'false' }, { id: 91003, draft: 'false' }])
  expect(state.commands).toBe(0)
})

test('real publication history rollback changes runtime while preserving the persisted draft', async ({ page, request }) => {
  const login = await request.post('/api/auth/login', { data: { userName: 'admin', password: 'admin123' } })
  expect(login.ok()).toBeTruthy()
  const auth = await login.json()
  const headers = { Authorization: `Bearer ${auth.token}` }
  await page.addInitScript(auth => {
    localStorage.setItem('scada_token', auth.token); localStorage.setItem('scada_user', JSON.stringify(auth.user)); localStorage.setItem('scada_locale', 'zh-CN')
  }, auth)
  const created = await request.post('/api/screens', { headers, data: { name: '历史集成首版' } })
  expect(created.ok()).toBeTruthy()
  const id = (await created.json()).id
  let revision = 1, firstRevision = 0
  for (const [index, name] of ['历史集成首版', '历史集成第二版'].entries()) {
    const saved = await request.put(`/api/screens/${id}`, { headers, data: { revision, name, document: document(name) } })
    expect(saved.ok()).toBeTruthy()
    const published = await request.post(`/api/screens/${id}/publish`, { headers, data: { revision: (await saved.json()).revision } })
    expect(published.ok()).toBeTruthy()
    revision = (await published.json()).revision
    if (index === 0) firstRevision = revision
  }
  const pending = document('尚未发布的集成草稿')
  const saved = await request.put(`/api/screens/${id}`, { headers, data: { revision, name: '保留草稿名称', document: pending } })
  expect(saved.ok()).toBeTruthy()
  revision = (await saved.json()).revision
  await page.goto(`/config/screens/${id}`)
  await page.locator(historyButton).click()
  const dialog = page.locator(dialogSelector)
  await dialog.locator(`[data-test="screen-version-${firstRevision}"]`).click()
  await dialog.locator('[data-test="screen-version-preview"]').click()
  await expect(dialog.locator('[data-node-id="label"]')).toContainText('历史集成首版')
  await acknowledge(page)
  await dialog.locator('[data-test="screen-rollback-submit"]').click()
  await expect(page.locator('[data-test="designer-rollback-success"]')).toBeVisible()
  await expect(page.getByRole('textbox', { name: '画面名称', exact: true })).toHaveValue('保留草稿名称')
  const draftResponse = await request.get(`/api/screens/${id}?draft=true`, { headers })
  expect(draftResponse.ok()).toBeTruthy()
  const draft = await draftResponse.json()
  expect(draft.name).toBe('保留草稿名称')
  expect(draft.document.nodes[0].text).toBe('尚未发布的集成草稿')
  expect(draft.revision).toBe(revision + 1)
  const catalogResponse = await request.get('/api/screens?draft=true', { headers })
  expect(catalogResponse.ok()).toBeTruthy()
  const catalog: ScreenSummary[] = await catalogResponse.json()
  expect(catalog.find(screen => screen.id === id)?.hasDraftChanges).toBe(true)
  const runningResponse = await request.get(`/api/screens/${id}`, { headers })
  expect(runningResponse.ok()).toBeTruthy()
  const running = await runningResponse.json()
  expect(running.name).toBe('历史集成首版')
  expect(running.document.nodes[0].text).toBe('历史集成首版')
  expect(running.publishedRevision).toBe(revision + 1)
  const historyResponse = await request.get(`/api/screens/${id}/versions`, { headers })
  expect(historyResponse.ok()).toBeTruthy()
  const history: ScreenVersionSummary[] = await historyResponse.json()
  expect(history).toHaveLength(3)
  expect(history[0]).toMatchObject({ kind: 'rollback', restoredFromRevision: firstRevision, revision: revision + 1 })
})
