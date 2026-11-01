import { test, expect, type APIRequestContext, type Page } from './fixtures'
import type { Recipe, RecipeCheck, RecipeTemplate } from '../src/api/recipes'

// control-failure.spec.ts 固化了实时页写值的失败措辞；这里覆盖其余会改动现场的入口：
// 配方下发、画面上的写值与点动。没收到回执时一律不能提示「稍后重试」——命令可能已经生效。

async function login(request: APIRequestContext) {
  const response = await request.post('/api/auth/login', { data: { userName: 'admin', password: 'admin123' } })
  expect(response.ok()).toBeTruthy()
  const body = await response.json()
  return { token: body.token as string, user: body.user, headers: { Authorization: `Bearer ${body.token}` } }
}

async function sign(page: Page, session: { token: string; user: unknown }) {
  await page.addInitScript((data) => {
    localStorage.setItem('scada_token', data.token)
    localStorage.setItem('scada_user', JSON.stringify(data.user))
  }, session)
}

const template: RecipeTemplate = {
  id: 901, name: 'Pump A', areaId: 1, description: null, dualApproval: false, revision: 1, updatedTs: 1, updatedBy: 'admin',
  parameters: [{ key: 'pressure', name: '出口压力', tagId: 921, unit: 'MPa', min: 0, max: 50, defaultValue: 10 }]
}
const recipe: Recipe = {
  id: 911, templateId: 901, templateName: 'Pump A', areaId: 1, name: 'Batch A', description: null, version: 2,
  updatedTs: 2, updatedBy: 'admin', values: [{ key: 'pressure', value: 12 }], note: null, source: 'manual'
}
const recipeCheck: RecipeCheck = {
  recipeId: 911, version: 2, dualApproval: false, ok: true, refusals: [],
  parameters: [{ key: 'pressure', name: '出口压力', tagId: 921, tagName: '出口压力', unit: 'MPa', target: 12, current: 0, refusals: [], dualApproval: false }]
}

test('配方下发超时：提示结果未知、作废预检查，并刷新下发记录供核对', async ({ page, request }) => {
  await sign(page, await login(request))
  await page.route(url => url.pathname === '/api/areas', route => route.fulfill({ json: [{ id: 1, name: '一车间' }] }))
  await page.route(url => url.pathname === '/api/devices', route => route.fulfill({ json: [{ id: 931, name: '泵站', areaId: 1 }] }))
  await page.route(url => url.pathname === '/api/tags', route => route.fulfill({ json: [{ id: 921, name: '出口压力', deviceId: 931, writable: true }] }))
  await page.route(url => url.pathname === '/api/recipes/templates', route => route.fulfill({ json: [template] }))
  await page.route(url => url.pathname === '/api/recipes', route => route.fulfill({ json: [recipe] }))
  let downloadListCalls = 0
  await page.route(url => url.pathname === '/api/recipes/downloads', route => { downloadListCalls++; return route.fulfill({ json: [] }) })
  await page.route('**/api/recipes/911/check', route => route.fulfill({ json: recipeCheck }))
  let downloads = 0
  await page.route('**/api/recipes/911/download', route => { downloads++; return route.abort('timedout') })

  await page.goto('/recipes')
  await page.locator('[data-test="recipe-download-911"]').click()
  const confirm = page.locator('[data-test="recipe-download-confirm"]')
  await expect(confirm).toBeEnabled()
  const listedBefore = downloadListCalls
  await confirm.click()

  const error = page.locator('[data-test="recipe-download-error"]')
  await expect(error).toContainText('配方下发结果未知')
  await expect(error).toContainText('下发记录')
  await expect(error).not.toContainText('稍后重试')
  // 不能一键重发：必须先重新检查
  await expect(confirm).toBeDisabled()
  await expect(page.locator('[data-test="recipe-check-retry"]')).toBeVisible()
  await expect.poll(() => downloadListCalls).toBeGreaterThan(listedBefore)
  expect(downloads).toBe(1)
})

async function publishScreen(request: APIRequestContext, headers: Record<string, string>, nodes: unknown[]) {
  const suffix = Date.now()
  const created = await request.post('/api/screens', { headers, data: { name: `现场动作${suffix}` } })
  expect(created.status()).toBe(201)
  const screen = await created.json()
  const saved = await request.put(`/api/screens/${screen.id}`, {
    headers, data: { name: `现场动作${suffix}`, revision: screen.revision, document: { schemaVersion: 1, width: 800, height: 480, background: '#101b2d', nodes } }
  })
  expect(saved.ok(), await saved.text()).toBeTruthy()
  const published = await request.post(`/api/screens/${screen.id}/publish`, { headers, data: { revision: (await saved.json()).revision } })
  expect(published.ok(), await published.text()).toBeTruthy()
  return screen.id as number
}

async function writableTag(request: APIRequestContext, headers: Record<string, string>, address: number) {
  const suffix = Date.now()
  const device = await request.post('/api/devices', {
    headers, data: { name: `FA-D-${suffix}`, protocol: 'ModbusTcp', host: '127.0.0.1', port: 1502, stationNumber: 1, byteOrder: 'ABCD', pollIntervalMs: 1000, timeoutMs: 1000, enabled: false, areaId: 1 }
  })
  expect(device.ok(), await device.text()).toBeTruthy()
  const tag = await request.post('/api/tags', {
    headers, data: { deviceId: (await device.json()).id, name: `FA-T-${suffix}`, area: 'HoldingRegister', address, dataType: 'Float', scale: 1, offset: 0, writable: true, archiveMode: 'None', deadband: 0, archivePeriodSec: 60 }
  })
  expect(tag.ok(), await tag.text()).toBeTruthy()
  return (await tag.json()).id as number
}

const button = (tagId: number) => ({
  id: 'cmd-1', type: 'button', x: 40, y: 40, width: 160, height: 48, rotation: 0, text: '开泵', color: '#7394b8', activeColor: '#34d399',
  unit: '', decimals: 1, min: 0, max: 100, activeThreshold: 0.5, tagId: null, action: { type: 'command', tagId, screenId: null, value: 1 }
})

async function mockCheck(page: Page, tagId: number) {
  await page.route(url => url.pathname === '/api/control/check', route => route.fulfill({
    json: { tagId, tagName: 'FA', current: 0, refusals: [], requireConfirm: false, dualApproval: false, limitMin: null, limitMax: null, maxStep: null, feedback: null, mode: 'Remote', lockouts: [] }
  }))
}

test('画面写值超时：提示结果未知，不提示「稍后重试」', async ({ page, request }) => {
  test.setTimeout(60000)
  const session = await login(request)
  const tagId = await writableTag(request, session.headers, 11)
  const screenId = await publishScreen(request, session.headers, [button(tagId)])
  await sign(page, session)
  await mockCheck(page, tagId)
  await page.route(url => /\/api\/screens\/\d+\/actions\//.test(url.pathname), route => route.abort('timedout'))

  await page.goto(`/screens/${screenId}`)
  await page.locator('[data-node-id="cmd-1"]').click()
  const error = page.locator('[data-test="runtime-command-error"]')
  await expect(error).toContainText('操作结果未知')
  await expect(error).not.toContainText('稍后重试')
})

test('画面写值预检查失败：明确说明命令没有下发', async ({ page, request }) => {
  test.setTimeout(60000)
  const session = await login(request)
  const tagId = await writableTag(request, session.headers, 12)
  const screenId = await publishScreen(request, session.headers, [button(tagId)])
  await sign(page, session)
  await page.route(url => url.pathname === '/api/control/check', route => route.fulfill({ status: 500, json: { message: '控制服务繁忙' } }))
  let sent = 0
  await page.route(url => /\/api\/screens\/\d+\/actions\//.test(url.pathname), route => { sent++; return route.abort('timedout') })

  await page.goto(`/screens/${screenId}`)
  await page.locator('[data-node-id="cmd-1"]').click()
  const error = page.locator('[data-test="runtime-command-error"]')
  await expect(error).toContainText('命令未下发')
  await expect(error).toContainText('控制服务繁忙')
  expect(sent).toBe(0)
})

test('点动松开超时：提示设备可能仍在动作，而不是「稍后重试」', async ({ page, request }) => {
  test.setTimeout(60000)
  const session = await login(request)
  const tagId = await writableTag(request, session.headers, 13)
  const jog = {
    id: 'jog-1', type: 'jog', x: 40, y: 40, width: 160, height: 48, rotation: 0, text: '点动', color: '#7394b8', activeColor: '#34d399',
    unit: '', decimals: 0, min: 0, max: 1, activeThreshold: 0.5, tagId: null, releaseValue: 0, maxHoldMs: 5000,
    action: { type: 'command', tagId, screenId: null, value: 1 }
  }
  const screenId = await publishScreen(request, session.headers, [jog])
  await sign(page, session)
  await mockCheck(page, tagId)
  // 点动要求控制点当前值质量良好；测试设备是停用的，真实推送会用坏质量覆盖快照，
  // 所以接管 WebSocket 只完成握手、不推数据（page.route 拦不到 WebSocket）。
  await page.routeWebSocket(/\/hubs\/realtime(?:\?|$)/, socket => {
    socket.onMessage(message => {
      for (const part of String(message).split('\x1e').filter(Boolean)) {
        const frame = JSON.parse(part) as { protocol?: string; type?: number; invocationId?: string }
        if (frame.protocol) socket.send('{}\x1e')
        else if (frame.type === 1 && frame.invocationId) socket.send(JSON.stringify({ type: 3, invocationId: frame.invocationId }) + '\x1e')
      }
    })
  })
  await page.route(url => url.pathname === '/api/realtime', route => route.fulfill({ json: [{ tagId, name: 'FA', deviceId: 1, value: 0, ts: Date.now(), quality: 'Good' }] }))
  const phases: string[] = []
  await page.route(url => /\/api\/screens\/\d+\/actions\//.test(url.pathname), route => {
    const phase = (route.request().postDataJSON() as { phase: string }).phase
    phases.push(phase)
    if (phase === 'release') return route.abort('timedout')
    return route.fulfill({ json: { success: true, pending: false, readBack: 1, message: '已按下', commandId: null } })
  })

  await page.goto(`/screens/${screenId}`)
  const node = page.locator('[data-node-id="jog-1"]')
  await expect(node).toBeVisible()
  // 权限、测点目录和实时快照到齐前点动是禁用的，此时按下会被静默忽略
  await expect(node).toHaveAttribute('aria-disabled', 'false')
  // 画布加载后会重新适配缩放，hover 等位置稳定且确认命中图元后再按下
  await node.hover()
  await page.mouse.down()
  await expect.poll(() => phases).toContain('press')
  await page.mouse.up()

  const error = page.locator('[data-test="runtime-command-error"]')
  await expect(error).toContainText('设备可能仍在动作')
  await expect(error).toContainText('最长保持时间')
  await expect(error).not.toContainText('稍后重试')
})
