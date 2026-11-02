import { test, expect, type APIRequestContext, type Page } from './fixtures'
import type { ScreenDocument, ScreenNode } from '../src/screens/model'

async function login(request: APIRequestContext) {
  const response = await request.post('/api/auth/login', { data: { userName: 'admin', password: 'admin123' } })
  expect(response.ok()).toBeTruthy()
  const result = await response.json()
  return { ...result, headers: { Authorization: `Bearer ${result.token}` } }
}
function node(id: string, type: ScreenNode['type'], extras: Partial<ScreenNode> = {}): ScreenNode {
  return {
    id, type, x: 40, y: 40, width: 160, height: 64, rotation: 0, text: id, color: '#7394b8', activeColor: '#34d399',
    unit: '', decimals: 1, min: 0, max: 100, activeThreshold: .5, tagId: null,
    action: { type: 'none', tagId: null, screenId: null, value: 1 }, ...extras
  }
}
function document(nodes: ScreenNode[], width = 1200, height = 720): ScreenDocument {
  return { schemaVersion: 1, width, height, background: '#101b2d', nodes }
}
async function create(request: APIRequestContext, headers: Record<string, string>, name: string) {
  const response = await request.post('/api/screens', { headers, data: { name } })
  expect(response.status()).toBe(201)
  return response.json()
}
async function save(request: APIRequestContext, headers: Record<string, string>, screen: { id: number; revision: number }, name: string, doc: ScreenDocument) {
  const response = await request.put(`/api/screens/${screen.id}`, { headers, data: { name, revision: screen.revision, document: doc } })
  expect(response.ok(), await response.text()).toBeTruthy()
  return response.json()
}
async function publish(request: APIRequestContext, headers: Record<string, string>, id: number, revision: number) {
  const response = await request.post(`/api/screens/${id}/publish`, { headers, data: { revision } })
  expect(response.ok(), await response.text()).toBeTruthy()
}
async function sign(page: Page, auth: { token: string; user: unknown }) {
  await page.addInitScript(value => {
    localStorage.setItem('scada_token', value.token)
    localStorage.setItem('scada_user', JSON.stringify(value.user))
    localStorage.setItem('scada_locale', 'zh-CN')
  }, auth)
}

test('runtime fills the viewport, keeps a screen trail, and marks one control busy', async ({ page, request }) => {
  test.setTimeout(90000)
  const auth = await login(request)
  const suffix = `${Date.now()}`
  const device = await (await request.post('/api/devices', { headers: auth.headers, data: { name: `ux-${suffix}`, protocol: 'ModbusTcp', host: '127.0.0.1', port: 1502, stationNumber: 1, byteOrder: 'ABCD', pollIntervalMs: 1000, timeoutMs: 1000, enabled: false } })).json()
  const tag = await (await request.post('/api/tags', { headers: auth.headers, data: { deviceId: device.id, name: `ux-tag-${suffix}`, area: 'HoldingRegister', address: 0, dataType: 'UInt16', scale: 1, offset: 0, writable: true, archiveMode: 'None', deadband: 0, archivePeriodSec: 60 } })).json()
  const target = await create(request, auth.headers, `区域${suffix}`)
  const targetSaved = await save(request, auth.headers, target, `区域${suffix}`, document([node('area', 'text', { text: '区域画面' })]))
  await publish(request, auth.headers, target.id, targetSaved.revision)
  const source = await create(request, auth.headers, `总览${suffix}`)
  const sourceSaved = await save(request, auth.headers, source, `总览${suffix}`, document([
    node('jump', 'button', { text: '进入区域', width: 180, height: 48, action: { type: 'navigate', screenId: target.id, tagId: null, value: 1 } })
  ]))
  await publish(request, auth.headers, source.id, sourceSaved.revision)
  const command = await create(request, auth.headers, `控制${suffix}`)
  const commandDoc = document([
    node('cmd-a', 'button', { text: '命令甲', x: 40, y: 40, width: 180, height: 48, tagId: tag.id, action: { type: 'command', tagId: tag.id, screenId: null, value: 1 } }),
    node('cmd-b', 'button', { text: '命令乙', x: 260, y: 40, width: 180, height: 48, tagId: tag.id, action: { type: 'command', tagId: tag.id, screenId: null, value: 1 } })
  ])
  const commandSaved = await save(request, auth.headers, command, `控制${suffix}`, commandDoc)
  await publish(request, auth.headers, command.id, commandSaved.revision)
  const wide = await create(request, auth.headers, `宽画面${suffix}`)
  const wideSaved = await save(request, auth.headers, wide, `宽画面${suffix}`, document([
    node('near', 'text', { text: '近处', x: 40, y: 40 }),
    node('far', 'text', { text: '远处', x: 3600, y: 40 })
  ], 4000, 720))
  await publish(request, auth.headers, wide.id, wideSaved.revision)
  await sign(page, auth)

  await page.goto('/screens/abc')
  await expect(page.locator('[data-test="screen-invalid-id"]')).toBeVisible()
  await expect(page.locator('[data-testid="screen-runtime"]')).toHaveCount(0)

  await page.goto(`/screens/${source.id}?q=${encodeURIComponent(`总览${suffix}`)}&status=published&sort=name`)
  const toolbar = page.locator('.runtime-toolbar')
  await expect(toolbar.locator('[data-test="screen-back"]')).toHaveText('返回画面列表')
  await expect(toolbar.locator('[data-test="screen-new-window"]')).toBeVisible()
  await expect(page.locator('[data-test="runtime-zoom"]')).toContainText('适应')
  const stage = page.locator('[data-test="runtime-stage"]')
  await expect(stage).toHaveClass(/fill/)
  const stageBox = await stage.boundingBox()
  expect(stageBox!.y + stageBox!.height).toBeGreaterThan(920)
  await page.locator('[data-node-id="jump"]').click()
  await expect.poll(() => new URL(page.url()).searchParams.get('trail')).toBe(String(source.id))
  expect(new URL(page.url()).searchParams.get('q')).toBe(`总览${suffix}`)
  await expect(page.locator('[data-test="screen-trail"]')).toContainText(`总览${suffix}`)
  await expect(page.locator('[data-test="screen-back"]')).toHaveText('返回上一画面')
  await page.locator('[data-test="screen-back"]').click()
  await expect.poll(() => new URL(page.url()).pathname).toBe(`/screens/${source.id}`)
  expect(new URL(page.url()).searchParams.get('trail')).toBeNull()
  expect(new URL(page.url()).searchParams.get('q')).toBe(`总览${suffix}`)
  await page.locator('[data-test="screen-back"]').click()
  await expect.poll(() => new URL(page.url()).pathname).toBe('/screens')
  const listQuery = new URL(page.url()).searchParams
  expect(listQuery.get('q')).toBe(`总览${suffix}`)
  expect(listQuery.get('status')).toBe('published')
  expect(listQuery.get('sort')).toBe('name')

  await page.goto(`/screens/${source.id}`)
  await page.locator('[data-test="screen-new-window"]').click()
  const notice = page.locator('[data-test="screen-monitor-notice"]')
  await expect(notice).toContainText('没有获得显示器权限')
  await expect(page.locator('[data-test="screen-open-window"]')).toBeEnabled()
  await page.keyboard.press('Escape')
  await page.evaluate(() => Object.defineProperty(window, 'getScreenDetails', { value: undefined, configurable: true }))
  await page.locator('[data-test="screen-new-window"]').click()
  await expect(notice).toContainText('当前浏览器不能列出显示器')

  let checks = 0
  let releaseFirst: () => void = () => {}
  const firstCheck = new Promise<void>(resolve => { releaseFirst = resolve })
  await page.route('**/api/control/check', async route => {
    checks += 1
    if (checks === 1) await firstCheck
    await route.fulfill({ json: { tagId: tag.id, tagName: 'ux', current: 0, mode: 'Remote', lockouts: [], refusals: ['许可条件不满足'], requireConfirm: false, dualApproval: false } })
  })
  await page.goto(`/screens/${command.id}`)
  const first = page.locator('[data-node-id="cmd-a"]')
  const second = page.locator('[data-node-id="cmd-b"]')
  await first.click()
  await expect(first).toHaveAttribute('data-node-busy', 'true')
  await expect(first.locator('[data-test="node-command-feedback"]')).toHaveAttribute('data-feedback-state', 'pending')
  await expect(second).not.toHaveAttribute('data-node-busy', 'true')
  releaseFirst()
  await expect(first.locator('[data-test="node-command-feedback"]')).toHaveAttribute('data-feedback-state', 'rejected')
  await expect(first.locator('[data-test="node-command-feedback"]')).toContainText('已拒绝')
  await expect(page.locator('[data-test="runtime-status"] [data-test="runtime-command-error"]')).toContainText('许可条件不满足')
  await expect(first.locator('[data-test="node-command-feedback"]')).not.toContainText('许可条件不满足')
  await second.click()
  await expect.poll(() => checks).toBe(2)
  await expect(second.locator('[data-test="node-command-feedback"]')).toHaveAttribute('data-feedback-state', 'rejected')

  await page.goto(`/screens/${wide.id}`)
  await expect(page.locator('[data-node-id="near"]')).toBeVisible()
  await expect(page.locator('[data-node-id="far"]')).toBeVisible()
  await page.locator('[data-test="runtime-zoom"]').click()
  await page.getByRole('option', { name: '100%', exact: true }).click()
  await expect(page.locator('[data-node-id="near"]')).toBeVisible()
  await expect(page.locator('[data-node-id="far"]')).toHaveCount(0)
})

test('designer keeps both panes, diffs a republish, and merges a conflict without dropping either side', async ({ page, request }) => {
  test.setTimeout(180000)
  const auth = await login(request)
  const suffix = `${Date.now()}`
  const symbolKey = `uxsym${suffix}`
  const symbol = await request.post('/api/hmi/library', { headers: auth.headers, data: { key: symbolKey, name: `自定义符号${suffix}`, kind: 'symbol', width: 80, height: 80, svg: '<svg viewBox="0 0 80 80"><rect data-user-symbol="yes" width="80" height="80" fill="currentColor"/></svg>' } })
  expect(symbol.ok(), await symbol.text()).toBeTruthy()
  const screen = await create(request, auth.headers, `组态${suffix}`)
  const shared = node('shared', 'text', { text: '原始文字' })
  const initial = await save(request, auth.headers, screen, `组态${suffix}`, document([shared]))
  const conflictScreen = { id: screen.id, revision: initial.revision }
  const blank = await create(request, auth.headers, `空白${suffix}`)
  const crowded = await create(request, auth.headers, `上限${suffix}`)
  const many = Array.from({ length: 900 }, (_, index) => node(`n${index}`, 'text', { text: '', x: (index % 30) * 20, y: Math.floor(index / 30) * 20, width: 16, height: 16 }))
  await save(request, auth.headers, crowded, `上限${suffix}`, document(many, 800, 800))
  await sign(page, auth)

  await page.goto(`/config/screens/${blank.id}`)
  await expect(page.getByRole('searchbox', { name: '搜索组件' })).toBeVisible()
  await expect(page.getByRole('searchbox', { name: '搜索图层' })).toBeVisible()
  await expect(page.locator('[data-test="canvas-settings"]')).toBeVisible()
  await page.locator('[data-palette="pump"]').click()
  await page.locator('[data-palette="tank"]').click()
  await expect(page.locator('[data-test="canvas-settings"]')).toBeVisible()
  await page.locator('[data-test="canvas-size"]').click()
  await expect(page.locator('[data-test="canvas-settings"]')).toBeInViewport()
  const symbolButton = page.locator(`[data-symbol="user:${symbolKey}"]`)
  await expect(symbolButton).toHaveAttribute('draggable', 'true')
  await page.evaluate(key => {
    const source = document.querySelector(`[data-symbol="user:${key}"]`) as HTMLElement
    const canvas = document.querySelector('[data-testid="designer-stage"] svg') as SVGSVGElement
    const dataTransfer = new DataTransfer()
    source.dispatchEvent(new DragEvent('dragstart', { bubbles: true, cancelable: true, dataTransfer }))
    const rect = canvas.getBoundingClientRect()
    canvas.dispatchEvent(new DragEvent('drop', { bubbles: true, cancelable: true, dataTransfer, clientX: rect.left + 40, clientY: rect.top + 40 }))
  }, symbolKey)
  await expect(page.locator('[data-node-type="symbol"]')).toHaveCount(1)
  const pumpLayer = page.locator('[data-test="designer-layers"] [data-layer-id]').filter({ hasText: '泵' }).first()
  await pumpLayer.locator('span').dblclick()
  const rename = page.locator('[data-test="layer-rename"]')
  await rename.fill('给水泵')
  await rename.press('Enter')
  await expect(page.locator('[data-test="designer-layers"]')).toContainText('给水泵')
  await page.locator('[data-test="designer-tab-layers"]').click()
  await expect(page.getByRole('searchbox', { name: '搜索组件' })).toBeHidden()
  await page.locator('[data-test="designer-tab-layers"]').click()
  await expect(page.getByRole('searchbox', { name: '搜索组件' })).toBeVisible()
  const groupedA = await page.locator('[data-layer-id]').nth(0).getAttribute('data-layer-id')
  const groupedB = await page.locator('[data-layer-id]').nth(1).getAttribute('data-layer-id')
  await page.locator(`[data-layer-id="${groupedA}"]`).click()
  await page.locator(`[data-layer-id="${groupedB}"]`).click({ modifiers: ['Shift'] })
  await page.keyboard.press('Control+g')
  const group = page.locator('[data-group-id]')
  await expect(group).toBeVisible()
  await expect(page.locator('[data-test="designer-layers"] button[data-group-id]')).toHaveCount(0)
  await expect(group).toContainText('编组 · 2')
  await group.click()
  await expect(group).toHaveAttribute('aria-expanded', 'false')
  await expect(page.locator(`[data-layer-id="${groupedA}"]`)).toBeHidden()
  await expect(page.locator(`[data-layer-id="${groupedB}"]`)).toBeHidden()
  await group.click()
  await page.locator(`[data-layer-id="${groupedA}"]`).click()
  await page.keyboard.press('Control+Shift+g')
  await expect(page.locator('[data-group-id]')).toHaveCount(0)
  const lower = page.locator('[data-layer-id]').nth(1)
  const lowerId = await lower.getAttribute('data-layer-id')
  await lower.click()
  await page.keyboard.press(']')
  await expect(page.locator('[data-layer-id]').first()).toHaveAttribute('data-layer-id', lowerId!)
  await page.keyboard.press('Control+d')
  await expect(page.locator('[data-node-type="pump"], [data-node-type="tank"], [data-node-type="symbol"]')).toHaveCount(4)
  await page.locator('[data-test="designer-shortcuts"]').click()
  const help = page.locator('[data-test="shortcut-help"]')
  await expect(help).toContainText('原地复制一份')
  await expect(help).toContainText('将所选图元编组')
  await page.keyboard.press('Escape')

  const blockValidate = (route: { fulfill: (options: { json: unknown }) => Promise<void> }) => route.fulfill({ json: { issues: [{ message: '画布校验未通过' }] } })
  await page.route('**/api/screens/validate**', blockValidate)
  await page.getByRole('button', { name: '发布', exact: true }).click()
  await expect(page.locator('[data-test="designer-issues"]')).toContainText('画布校验未通过')
  await page.keyboard.press('Escape')
  await page.unroute('**/api/screens/validate**', blockValidate)
  await page.getByRole('button', { name: '发布', exact: true }).click()
  await expect(page.locator('.el-message').filter({ hasText: '已发布，运行画面已更新' }).last()).toBeVisible()
  await page.locator('[data-layer-id]').first().click()
  await page.getByRole('textbox', { name: '图元文字', exact: true }).fill('发布后改过')
  await page.getByRole('textbox', { name: '图元文字', exact: true }).blur()
  await page.getByRole('button', { name: '发布', exact: true }).click()
  const diff = page.locator('[data-test="designer-publish-diff"]')
  await expect(diff).toContainText('修改 1')
  await diff.locator('[data-test="publish-diff-confirm"]').click()
  await expect(page.locator('.el-message').filter({ hasText: '已发布，运行画面已更新' }).last()).toBeVisible()

  await page.goto(`/config/screens/${conflictScreen.id}`)
  await expect(page.locator('[data-layer-id="shared"]')).toBeVisible()
  const latest = await (await request.get(`/api/screens/${conflictScreen.id}?draft=true`, { headers: auth.headers })).json()
  const serverNodes = [
    node('shared', 'text', { text: '服务器文字' }),
    ...Array.from({ length: 25 }, (_, index) => node(`srv-${index}`, 'text', { text: `服务器${index}`, x: 40, y: 80 + index * 10, width: 80, height: 16 }))
  ]
  const remote = await request.put(`/api/screens/${conflictScreen.id}`, { headers: auth.headers, data: { name: `组态${suffix}`, revision: latest.revision, document: document(serverNodes) } })
  expect(remote.ok(), await remote.text()).toBeTruthy()
  await page.locator('[data-layer-id="shared"]').click()
  await page.getByRole('textbox', { name: '图元文字', exact: true }).fill('本地修改')
  await page.getByRole('textbox', { name: '图元文字', exact: true }).blur()
  await page.locator('[data-palette="tank"]').click()
  await page.getByRole('button', { name: '保存草稿', exact: true }).click()
  const conflict = page.locator('[data-test="designer-conflict"]')
  await expect(conflict).toBeVisible()
  await expect(conflict.locator('.conflict-list li')).toHaveCount(27)
  await expect(conflict.locator('[data-test="conflict-reload"]')).toBeVisible()
  await expect(conflict.locator('.el-checkbox').first()).toBeChecked()
  const merge = conflict.locator('[data-test="conflict-merge"]')
  await expect(merge).toBeDisabled()
  await expect(conflict.locator('[data-test="conflict-overwrite"]')).not.toHaveClass(/el-button--primary/)
  await expect(merge).toHaveClass(/el-button--primary/)
  await conflict.locator('.el-radio').filter({ hasText: '用本地' }).click()
  await expect(merge).toBeEnabled()
  await merge.click()
  await expect(page.getByText('草稿已保存', { exact: true })).toBeVisible()
  const merged = await (await request.get(`/api/screens/${conflictScreen.id}?draft=true`, { headers: auth.headers })).json()
  const texts = merged.document.nodes.map((item: { text: string }) => item.text)
  expect(texts).toContain('本地修改')
  expect(texts).not.toContain('服务器文字')
  expect(texts.filter((item: string) => item.startsWith('服务器'))).toHaveLength(25)
  expect(merged.document.nodes.some((item: { type: string }) => item.type === 'tank')).toBeTruthy()

  await page.goto(`/config/screens/${crowded.id}`)
  await expect(page.locator('[data-test="designer-node-limit"]')).toContainText('900')
})
