import { test, expect, applySession, loginApi, type APIRequestContext } from './fixtures'
import type { ScreenDocument, ScreenNode, ScreenSummary } from '../src/screens/model'

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

test('workspace keeps per-tab history, tab actions, cache and restore', async ({ page, request }) => {
  const auth = await loginApi(request)
  const suffix = `${Date.now()}`
  const target = await create(request, auth.headers, `工作台目标${suffix}`)
  const quality = await create(request, auth.headers, `工作台质量${suffix}`)
  const source = await create(request, auth.headers, `工作台入口${suffix}`, {
    schemaVersion: 1, width: 1200, height: 720, background: '#101b2d', nodes: [
      node('label', 'text', { text: `工作台入口${suffix}`, width: 420 }),
      node('jump', 'button', { y: 160, text: '进入目标', action: { type: 'navigate', screenId: target.id, tagId: null, value: 1 } }),
      node('popup', 'button', { y: 260, text: '打开弹窗', action: { type: 'popup', screenId: target.id, tagId: null, value: 1 } })
    ]
  })
  await applySession(page, auth)
  await page.goto('/workspace')
  await expect(page.locator('[data-test="workspace-empty"]')).toBeVisible()

  const tab = (id: number) => page.locator(`[data-screen-id="${id}"]`)
  const activePane = page.locator('.ws-pane:visible')

  await page.locator('[data-test="workspace-open"]').click()
  await page.locator('[data-test="workspace-search"]').fill(suffix)
  await page.locator(`[data-test="workspace-catalog-${source.id}"]`).click()
  await expect(tab(source.id)).toHaveClass(/active/)
  await expect(activePane.locator('[data-testid="screen-runtime"]')).toBeVisible()

  await activePane.locator('[data-node-id="jump"]').click()
  await expect(tab(target.id)).toHaveClass(/active/)
  await expect(page.locator('[data-test="workspace-tabs"] .ws-tab')).toHaveCount(1)
  await expect(page.locator('[data-test="workspace-trail"]')).toContainText(source.name)
  await expect(page.locator('[data-test="workspace-trail"]')).toContainText(target.name)

  await page.locator('[data-test="workspace-back"]').click()
  await expect(tab(source.id)).toHaveClass(/active/)
  await activePane.locator('[data-node-id="popup"]').click()
  await expect(page.locator('[data-test="runtime-popup"]')).toBeVisible()
  await page.locator('[data-test="runtime-popup"] .runtime-popup-close').click()
  await expect(page.locator('[data-test="runtime-popup"]')).toHaveCount(0)

  await page.locator('[data-test="workspace-open"]').click()
  await page.locator('[data-test="workspace-search"]').fill(suffix)
  await page.locator(`[data-test="workspace-catalog-${quality.id}"]`).click()
  await expect(tab(quality.id)).toHaveClass(/active/)
  await expect(page.locator('[data-testid="screen-runtime"]')).toHaveCount(2)

  await tab(source.id).click()
  await activePane.locator('[data-node-id="popup"]').click()
  await expect(page.locator('[data-test="runtime-popup"]')).toBeVisible()
  await tab(quality.id).click()
  await expect(page.locator('[data-test="runtime-popup"]')).toHaveCount(0)

  await page.locator('[data-test="workspace-cache"]').click()
  await expect(page.locator('[data-test="workspace-cache"] input')).not.toBeChecked()
  await tab(source.id).click()
  await expect(page.locator('[data-testid="screen-runtime"]')).toHaveCount(1)
  await tab(quality.id).click()
  await expect(page.locator('[data-testid="screen-runtime"]')).toHaveCount(1)

  await page.reload()
  await expect(page.locator('[data-test="workspace-tabs"] .ws-tab')).toHaveCount(2)
  await expect(tab(quality.id)).toHaveClass(/active/)
  await tab(source.id).click()
  await expect(activePane.locator('[data-node-id="popup"]')).toBeVisible()
  await expect(page.locator('[data-test="workspace-forward"]')).toBeEnabled()
  await page.locator('[data-test="workspace-forward"]').click()
  await expect(tab(target.id)).toHaveClass(/active/)
  await page.locator('[data-test="workspace-back"]').click()
  await expect(activePane.locator('[data-node-id="jump"]')).toBeVisible()

  await page.locator('.ws-tab.active').click({ button: 'right' })
  await expect(page.locator('[data-test="workspace-context-menu"]')).toBeVisible()
  await page.locator('[data-test="workspace-menu-duplicate"]').click()
  await expect(page.locator('[data-test="workspace-tabs"] .ws-tab')).toHaveCount(3)
  await page.locator('.ws-tab.active').click({ button: 'right' })
  await page.locator('[data-test="workspace-menu-pin"]').click()
  await expect(page.locator('.ws-tab.active [data-test="workspace-pin-icon"]')).toBeVisible()
  await expect(page.locator('.ws-tab.active .ws-tab-close')).toHaveCount(0)

  await tab(quality.id).click()
  await page.locator('[data-test="workspace-tab-actions"]').click()
  await page.locator('[data-test="workspace-close-others"]').click()
  await expect(page.locator('[data-test="workspace-tabs"] .ws-tab')).toHaveCount(2)
  await page.locator('[data-test="workspace-tab-actions"]').click()
  await page.locator('[data-test="workspace-close-all"]').click()
  await expect(page.locator('[data-test="workspace-tabs"] .ws-tab')).toHaveCount(1)

  await page.locator('.ws-tab:has([data-test="workspace-pin-icon"])').click({ button: 'right' })
  await page.locator('[data-test="workspace-menu-pin"]').click()
  await page.locator('[data-test="workspace-tab-actions"]').click()
  await page.locator('[data-test="workspace-close-all"]').click()
  await expect(page.locator('[data-test="workspace-empty"]')).toBeVisible()

  await page.locator('[data-test="workspace-tab-actions"]').click()
  await page.locator('[data-test="workspace-reopen"]').click()
  await expect(page.locator('[data-test="workspace-tabs"] .ws-tab')).toHaveCount(1)
  await page.locator('[data-test="workspace-tab-actions"]').click()
  await page.locator('[data-test="workspace-close-all"]').click()
  await expect(page.locator('[data-test="workspace-empty"]')).toBeVisible()
})
test('workspace copies a shareable tab group link', async ({ page, context, request }) => {
  const auth = await loginApi(request)
  const suffix = `${Date.now()}`
  const first = await create(request, auth.headers, `链接甲${suffix}`)
  const second = await create(request, auth.headers, `链接乙${suffix}`)
  await applySession(page, auth)
  await page.goto(`/workspace?screens=${first.id},${second.id}&active=1`)
  const tab = (id: number) => page.locator(`[data-screen-id="${id}"]`)
  await expect(page.locator('[data-test="workspace-tabs"] .ws-tab')).toHaveCount(2)
  await expect(tab(second.id)).toHaveClass(/active/)

  await context.grantPermissions(['clipboard-read', 'clipboard-write'])
  await page.locator('[data-test="workspace-tab-actions"]').click()
  await page.locator('[data-test="workspace-copy-link"]').click()
  await expect(page.getByText('已复制标签组链接')).toBeVisible()
  const copied = await page.evaluate(() => navigator.clipboard.readText())
  expect(copied).toContain(`/workspace?screens=${first.id},${second.id}&active=1`)
})

test('switching back to an inactive tab marks live data as refreshing', async ({ page, request }) => {
  const auth = await loginApi(request)
  const suffix = `${Date.now()}`
  const first = await create(request, auth.headers, `刷新甲${suffix}`)
  const second = await create(request, auth.headers, `刷新乙${suffix}`)
  await applySession(page, auth)
  await page.goto(`/workspace?screens=${first.id},${second.id}&active=0`)
  const tab = (id: number) => page.locator(`[data-screen-id="${id}"]`)
  await expect(page.locator('[data-test="workspace-tabs"] .ws-tab')).toHaveCount(2)
  await expect(page.locator('.ws-pane:visible [data-testid="screen-runtime"]')).toBeVisible()

  // 切回时 load() 会重拉画面详情；延迟它才能稳定观察到"刷新中"标记。
  await page.route(url => url.pathname === `/api/screens/${first.id}`, async route => {
    await new Promise(resolve => setTimeout(resolve, 800))
    return route.continue()
  })

  await tab(second.id).click()
  await expect(tab(second.id)).toHaveClass(/active/)
  await tab(first.id).click()
  await expect(page.locator('[data-test="runtime-resuming"]')).toBeVisible()
  await expect(page.locator('[data-test="runtime-resuming"]')).toHaveCount(0, { timeout: 5000 })
})
test('workspace marks inactive tabs with unacknowledged alarms and lists all tabs', async ({ page, request }) => {
  const auth = await loginApi(request)
  const suffix = `${Date.now()}`
  // 这里刻意用 mock 画面：发布校验会拒绝不存在的 tagId，而本用例要验证的正是 tagId 与报警的匹配。
  const summary = (id: number, name: string): ScreenSummary => ({
    id, name, areaId: null, revision: 1, publishedRevision: 1,
    updatedTs: Date.now(), updatedBy: 'admin', hasDraftChanges: false
  })
  const first = summary(9001, `报警甲${suffix}`)
  const second = summary(9002, `报警乙${suffix}`)
  const firstDoc: ScreenDocument = {
    schemaVersion: 1, width: 1200, height: 720, background: '#101b2d',
    nodes: [node('label', 'text', { text: first.name, tagId: 5001 })]
  }
  const secondDoc: ScreenDocument = {
    schemaVersion: 1, width: 1200, height: 720, background: '#101b2d',
    nodes: [node('label', 'text', { text: second.name, tagId: 5002 })]
  }
  await applySession(page, auth)
  await page.route(url => url.pathname === '/api/screens', route => route.fulfill({ json: [first, second] }))
  await page.route(url => url.pathname === '/api/screens/9001' || url.pathname === '/api/screens/9002', route => {
    const id = Number(new URL(route.request().url()).pathname.split('/').pop())
    return route.fulfill({ json: { ...(id === 9001 ? first : second), document: id === 9001 ? firstDoc : secondDoc } })
  })
  const alarm = (id: number, tagId: number, level: string) => ({
    id, ruleId: 1, tagId, deviceId: null, level, message: `报警${id}`, value: 1, state: 'Unack',
    startTs: Date.now(), endTs: null, ackTs: null, ackUser: null, firstOut: false, chattering: false, shelved: false, suppressed: false
  })
  await page.route(url => url.pathname === '/api/alarms/page', route => route.fulfill({
    json: { items: [alarm(1, 5001, 'Warning'), alarm(2, 5002, 'Critical')], total: 2, offset: 0, limit: 500, capped: false }
  }))
  await page.goto(`/workspace?screens=${first.id},${second.id}&active=0`)
  const tab = (id: number) => page.locator(`[data-screen-id="${id}"]`)

  // 激活时也会拉一次全局未确认报警：非活动标签同样要亮出红点。
  await expect(tab(first.id).locator('[data-test="workspace-tab-alarm"]')).toHaveText('1')
  await expect(tab(second.id).locator('[data-test="workspace-tab-alarm"]')).toHaveText('1')

  await page.locator('[data-test="workspace-all-tabs"]').click()
  const allTabs = page.locator('.ws-all-tabs-popper .el-dropdown-menu__item')
  await expect(allTabs).toHaveCount(2)
  await expect(allTabs.nth(0).locator('[data-test="workspace-all-alarm"]')).toHaveText('1')
  await allTabs.nth(1).click()
  await expect(tab(second.id)).toHaveClass(/active/)
})

test('opening a ninth screen replaces the least recently used tab after confirmation', async ({ page, request }) => {
  const auth = await loginApi(request)
  const suffix = `${Date.now()}`
  const created = []
  for (let index = 0; index < 9; index++) created.push(await create(request, auth.headers, `上限${index}·${suffix}`))
  await applySession(page, auth)
  await page.goto(`/workspace?screens=${created.slice(0, 8).map(screen => screen.id).join(',')}&active=0`)
  await expect(page.locator('[data-test="workspace-tabs"] .ws-tab')).toHaveCount(8)

  await page.locator('[data-test="workspace-open"]').click()
  await page.locator('[data-test="workspace-search"]').fill(created[8].name)
  await page.locator(`[data-test="workspace-catalog-${created[8].id}"]`).click()

  // 第 1 个是当前激活标签，不能被自动替换；最旧的非活动标签是第 2 个。
  await expect(page.locator('.el-message-box')).toContainText(created[1].name)
  await page.getByRole('button', { name: '关闭并打开' }).click()
  await expect(page.locator('[data-test="workspace-tabs"] .ws-tab')).toHaveCount(8)
  await expect(page.locator(`[data-screen-id="${created[1].id}"]`)).toHaveCount(0)
  await expect(page.locator(`[data-screen-id="${created[8].id}"]`)).toHaveCount(1)
})

test('two workspace windows keep independent tab groups', async ({ page, context, request }) => {
  const auth = await loginApi(request)
  const suffix = `${Date.now()}`
  const first = await create(request, auth.headers, `窗口甲${suffix}`)
  const second = await create(request, auth.headers, `窗口乙${suffix}`)
  await applySession(page, auth)
  await page.goto(`/workspace?screens=${first.id}&active=0`)
  await expect(page.locator('[data-test="workspace-tabs"] .ws-tab')).toHaveCount(1)

  const other = await context.newPage()
  await applySession(other, auth)
  await other.goto(`/workspace?screens=${second.id}&active=0`)
  await expect(other.locator('[data-test="workspace-tabs"] .ws-tab')).toHaveCount(1)

  // sessionStorage 按窗口隔离：一个窗口刷新不能把另一个窗口的标签组覆盖掉。
  await page.reload()
  await expect(page.locator('[data-test="workspace-tabs"] .ws-tab')).toHaveCount(1)
  await expect(page.locator(`[data-screen-id="${first.id}"]`)).toHaveCount(1)
  await other.reload()
  await expect(other.locator('[data-test="workspace-tabs"] .ws-tab')).toHaveCount(1)
  await expect(other.locator(`[data-screen-id="${second.id}"]`)).toHaveCount(1)
  await other.close()
})

test('adds several catalog screens at once from the workspace picker', async ({ page, request }) => {
  const auth = await loginApi(request)
  const suffix = `${Date.now()}`
  const first = await create(request, auth.headers, `批量甲${suffix}`)
  const second = await create(request, auth.headers, `批量乙${suffix}`)
  await applySession(page, auth)
  await page.goto('/workspace')
  await expect(page.locator('[data-test="workspace-empty"]')).toBeVisible()

  await page.locator('[data-test="workspace-open"]').click()
  await page.locator('[data-test="workspace-search"]').fill(suffix)
  await page.locator(`[data-test="workspace-catalog-check-${first.id}"]`).click()
  await page.locator(`[data-test="workspace-catalog-check-${second.id}"]`).click()
  await page.locator('[data-test="workspace-catalog-add"]').click()

  await expect(page.locator('[data-test="workspace-tabs"] .ws-tab')).toHaveCount(2)
  await expect(page.locator(`[data-screen-id="${first.id}"]`)).toHaveCount(1)
  await expect(page.locator(`[data-screen-id="${second.id}"]`)).toHaveCount(1)
})
test('grid layouts show several screens at once and survive a refresh', async ({ page, request }) => {
  const auth = await loginApi(request)
  const suffix = `${Date.now()}`
  const first = await create(request, auth.headers, `平铺甲${suffix}`)
  const second = await create(request, auth.headers, `平铺乙${suffix}`)
  await applySession(page, auth)
  await page.goto(`/workspace?screens=${first.id},${second.id}&active=0`)
  await expect(page.locator('[data-test="workspace-tabs"] .ws-tab')).toHaveCount(2)

  await page.locator('[data-test="workspace-layout-grid2"]').click()
  await expect(page.locator('[data-test="workspace-grid"]')).toHaveClass(/is-grid2/)
  await expect(page.locator('[data-test="workspace-cell"]')).toHaveCount(2)
  await expect(page.locator('[data-test="workspace-grid-empty"]')).toBeVisible()
  // 平铺不暂停非活动页：两格都应挂载运行时（与标签切换的 suspended 行为不同）。
  await expect(page.locator('[data-test="workspace-cell"] [data-testid="screen-runtime"]')).toHaveCount(2)

  await page.locator('[data-test="workspace-cell"]').nth(1).locator('.ws-cell-head').click()
  await expect(page.locator('[data-test="workspace-cell"]').nth(1)).toHaveClass(/active/)

  // 布局模式随窗口快照保存，刷新后仍是 2×2。
  await page.reload()
  await expect(page.locator('[data-test="workspace-grid"]')).toHaveClass(/is-grid2/)

  await page.locator('[data-test="workspace-layout-grid3"]').click()
  await expect(page.locator('[data-test="workspace-grid"]')).toHaveClass(/is-grid3/)
  await expect(page.locator('[data-test="workspace-cell"]')).toHaveCount(2)
})
test('workspace keyboard shortcuts switch and close tabs', async ({ page, request }) => {
  const auth = await loginApi(request)
  const suffix = `${Date.now()}`
  const first = await create(request, auth.headers, `键甲${suffix}`)
  const second = await create(request, auth.headers, `键乙${suffix}`)
  await applySession(page, auth)
  await page.goto(`/workspace?screens=${first.id},${second.id}&active=0`)
  const tab = (id: number) => page.locator(`[data-screen-id="${id}"]`)
  await expect(tab(first.id)).toHaveClass(/active/)

  await page.keyboard.press('Control+Tab')
  await expect(tab(second.id)).toHaveClass(/active/)
  await page.keyboard.press('Control+Tab')
  await expect(tab(first.id)).toHaveClass(/active/)

  await page.keyboard.press('Control+w')
  await expect(page.locator('[data-test="workspace-tabs"] .ws-tab')).toHaveCount(1)
  await expect(tab(first.id)).toHaveCount(0)
})

test('pinned tabs survive closing the tabs to their right', async ({ page, request }) => {
  const auth = await loginApi(request)
  const suffix = `${Date.now()}`
  const created = []
  for (let index = 0; index < 3; index++) created.push(await create(request, auth.headers, `右关${index}${suffix}`))
  await applySession(page, auth)
  await page.goto(`/workspace?screens=${created.map(screen => screen.id).join(',')}&active=0`)
  await expect(page.locator('[data-test="workspace-tabs"] .ws-tab')).toHaveCount(3)

  await page.locator('.ws-tab.active').click({ button: 'right' })
  await page.locator('[data-test="workspace-menu-pin"]').click()
  await expect(page.locator('.ws-tab.active [data-test="workspace-pin-icon"]')).toBeVisible()

  await page.locator('.ws-tab.active').click({ button: 'right' })
  await page.locator('[data-test="workspace-menu-close-right"]').click()
  // 关闭右侧只移除未固定的标签，固定的当前标签必须留下。
  await expect(page.locator('[data-test="workspace-tabs"] .ws-tab')).toHaveCount(1)
  await expect(page.locator(`[data-screen-id="${created[0].id}"]`)).toHaveCount(1)
})
