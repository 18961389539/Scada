import { test, expect, type APIRequestContext, type Page } from '@playwright/test'
import type { ScreenDocument, ScreenNode } from '../src/screens/model'

async function login(request: APIRequestContext) {
  const response = await request.post('/api/auth/login', { data: { userName: 'admin', password: 'admin123' } })
  expect(response.ok()).toBeTruthy()
  const result = await response.json()
  return { ...result, headers: { Authorization: `Bearer ${result.token}` } }
}

function node(partial: Partial<ScreenNode> & Pick<ScreenNode, 'id' | 'type'>): ScreenNode {
  return {
    x: 40, y: 40, width: 140, height: 112, rotation: 0, text: '图元', color: '#7394b8', activeColor: '#34d399',
    unit: '', decimals: 1, min: 0, max: 100, activeThreshold: .5, tagId: null,
    action: { type: 'none', tagId: null, screenId: null, value: 1 }, ...partial
  }
}

async function createScreen(request: APIRequestContext, headers: Record<string, string>, name: string, document: ScreenDocument) {
  const created = await (await request.post('/api/screens', { headers, data: { name } })).json()
  const saved = await (await request.put(`/api/screens/${created.id}`, { headers, data: { name, revision: created.revision, document } })).json()
  expect((await request.post(`/api/screens/${created.id}/publish`, { headers, data: { revision: saved.revision } })).ok()).toBeTruthy()
  return created.id as number
}

async function sign(page: Page, auth: { token: string; user: unknown }): Promise<void> {
  await page.addInitScript(({ token, user }) => {
    localStorage.setItem('scada_token', token)
    localStorage.setItem('scada_user', JSON.stringify(user))
  }, auth)
}

test('designer keeps a library load failure instead of an empty template list', async ({ page, request }) => {
  const auth = await login(request)
  const suffix = `${Date.now()}`
  const created = await (await request.post('/api/screens', { headers: auth.headers, data: { name: `图库失败${suffix}` } })).json()
  await sign(page, auth)
  let fail = true
  await page.route((url) => url.pathname === '/api/hmi/library', (route) => {
    if (fail && route.request().method() === 'GET') {
      return route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message: '图库不可用' }) })
    }
    return route.continue()
  })
  await page.goto(`/config/screens/${created.id}`)
  await expect(page.locator('[data-test="designer-library-error"]')).toContainText('图库不可用')
  await expect(page.getByText('选中图元后点“存为模板”创建。')).toHaveCount(0)
  await expect(page.locator('[data-testid="designer-stage"]')).toBeVisible()
  fail = false
  await page.locator('[data-test="designer-library-retry"]').click()
  await expect(page.locator('[data-test="designer-library-error"]')).toHaveCount(0)
})

test('running screen keeps a library load failure instead of a missing template', async ({ page, request }) => {
  const auth = await login(request)
  const suffix = `${Date.now()}`
  const key = `pump${suffix}`
  const symbol = node({ id: 'sym-1', type: 'symbol', symbol: 'isa:pump', x: 10, y: 10, width: 100, height: 100, text: '泵' })
  const library = await request.post('/api/hmi/library', {
    headers: auth.headers,
    data: {
      key, name: `泵模板${suffix}`, category: '面板', kind: 'template', width: 160, height: 140, placeholders: [],
      document: { schemaVersion: 1, width: 320, height: 200, background: '#101b2d', nodes: [symbol] }, revision: 0
    }
  })
  expect(library.ok(), await library.text()).toBeTruthy()
  const screenId = await createScreen(request, auth.headers, `运行图库${suffix}`, {
    schemaVersion: 1, width: 1200, height: 720, background: '#101b2d',
    nodes: [node({ id: 'tpl-1', type: 'template', templateKey: key, width: 160, height: 140, text: `泵模板${suffix}`, bindings: [] })]
  })
  await sign(page, auth)
  let fail = true
  await page.route((url) => url.pathname === '/api/hmi/library', (route) => {
    if (fail && route.request().method() === 'GET') {
      return route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message: '图库不可用' }) })
    }
    return route.continue()
  })
  await page.goto(`/screens/${screenId}`)
  const runtime = page.locator('[data-testid="screen-runtime"]')
  await expect(page.locator('[data-test="runtime-library-error"]')).toContainText('图库不可用')
  await expect(runtime.getByText(`模板 ${key} 不存在`)).toHaveCount(0)
  await expect(runtime.getByText('图库未加载')).toBeVisible()
  await expect(page.locator('[data-test="runtime-stage"]')).toBeVisible()
  fail = false
  await runtime.getByRole('button', { name: '刷新', exact: true }).click()
  await expect(page.locator('[data-test="runtime-library-error"]')).toHaveCount(0)
  await expect(runtime.locator('[data-symbol="isa:pump"]')).toBeVisible()
})
