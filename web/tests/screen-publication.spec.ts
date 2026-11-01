import { test, expect, type APIRequestContext, type Page } from './fixtures'
import type { ScreenDocument, ScreenNode } from '../src/screens/model'

async function login(request: APIRequestContext, page: Page) {
  const response = await request.post('/api/auth/login', { data: { userName: 'admin', password: 'admin123' } })
  expect(response.ok()).toBeTruthy()
  const auth = await response.json()
  await page.addInitScript(({ token, user }) => {
    localStorage.setItem('scada_token', token)
    localStorage.setItem('scada_user', JSON.stringify(user))
  }, auth)
  return { Authorization: `Bearer ${auth.token}` }
}

function node(id: string, type: ScreenNode['type'], extras: Partial<ScreenNode> = {}): ScreenNode {
  return { id, type, x: 0, y: 0, width: 100, height: 100, rotation: 0, text: '', color: '#7394b8', activeColor: '#34d399',
    unit: '', decimals: 1, min: 0, max: 100, activeThreshold: .5, tagId: null,
    action: { type: 'none', tagId: null, screenId: null, value: 1 }, ...extras }
}

async function create(request: APIRequestContext, headers: Record<string, string>, templateKey: string, name: string, directSymbol?: string) {
  const createdResponse = await request.post('/api/screens', { headers, data: { name } })
  expect(createdResponse.ok(), await createdResponse.text()).toBeTruthy()
  const created = await createdResponse.json()
  const document: ScreenDocument = { schemaVersion: 1, width: 1200, height: 720, background: '#101b2d', nodes: [
    node('instance', 'template', { templateKey, x: 20, y: 20, width: 200, height: 200, bindings: [] }),
    ...(directSymbol ? [node('direct', 'symbol', { symbol: directSymbol, x: 300, y: 20 })] : [])
  ] }
  const savedResponse = await request.put(`/api/screens/${created.id}`, { headers, data: { name, revision: created.revision, document } })
  expect(savedResponse.ok(), await savedResponse.text()).toBeTruthy()
  const saved = await savedResponse.json()
  const publishedResponse = await request.post(`/api/screens/${created.id}/publish`, { headers, data: { revision: saved.revision } })
  expect(publishedResponse.ok(), await publishedResponse.text()).toBeTruthy()
  return { id: created.id as number, document, publication: await publishedResponse.json() }
}

test('published templates and direct user symbols render frozen SVG without the live library', async ({ request, page }) => {
  const headers = await login(request, page), suffix = `${Date.now()}`
  const templateKey = `template${suffix}`, symbolKey = `symbol${suffix}`, symbol = `user:${symbolKey}`
  const svg = (version: string) => `<svg viewBox="0 0 100 100"><rect data-snapshot="${version}" width="80" height="80" fill="currentColor"/></svg>`
  const symbolResponse = await request.post('/api/hmi/library', { headers, data: { key: symbolKey, name: symbolKey, kind: 'symbol', width: 100, height: 100, svg: svg('A') } })
  expect(symbolResponse.ok(), await symbolResponse.text()).toBeTruthy()
  const symbolItem = (await symbolResponse.json()).item
  const templateDocument: ScreenDocument = { schemaVersion: 1, width: 320, height: 200, background: '#101b2d', nodes: [node('svg', 'symbol', { symbol })] }
  const templateResponse = await request.post('/api/hmi/library', { headers, data: { key: templateKey, name: templateKey, kind: 'template', width: 200, height: 200, placeholders: [], document: templateDocument } })
  expect(templateResponse.ok(), await templateResponse.text()).toBeTruthy()
  const screen = await create(request, headers, templateKey, `冻结资源${suffix}`, symbol)
  let libraryRequests = 0
  await page.route(url => url.pathname === '/api/hmi/library', route => {
    libraryRequests++
    return route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message: '图库离线' }) })
  })
  await page.goto(`/screens/${screen.id}`)
  const runtime = page.locator('[data-testid="screen-runtime"]')
  await expect(runtime.locator('[data-snapshot="A"]')).toHaveCount(2)
  await expect(runtime.locator('[data-test="runtime-library-error"]')).toHaveCount(0)
  const revision = await runtime.locator('[data-test="runtime-version"]').textContent()
  const updated = await request.put(`/api/hmi/library/${symbolItem.id}`, { headers, data: { ...symbolItem, svg: svg('B') } })
  expect(updated.ok(), await updated.text()).toBeTruthy()
  await page.reload()
  await expect(runtime.locator('[data-snapshot="A"]')).toHaveCount(2)
  await expect(runtime.locator('[data-snapshot="B"]')).toHaveCount(0)
  await expect(runtime.locator('[data-test="runtime-version"]')).toHaveText(revision!)
  const republished = await request.post(`/api/screens/${screen.id}/publish`, { headers, data: { revision: screen.publication.revision } })
  expect(republished.ok(), await republished.text()).toBeTruthy()
  await runtime.getByRole('button', { name: '刷新', exact: true }).click()
  await expect(runtime.locator('[data-snapshot="B"]')).toHaveCount(2)
  await expect(runtime.locator('[data-snapshot="A"]')).toHaveCount(0)
  await expect(runtime.locator('[data-test="runtime-version"]')).not.toHaveText(revision!)
  expect(libraryRequests).toBe(0)
})

test('legacy library publication explains republishing and configuration can repair it', async ({ request, page }) => {
  const headers = await login(request, page), suffix = `${Date.now()}`, templateKey = `legacy${suffix}`
  const templateResponse = await request.post('/api/hmi/library', { headers, data: { key: templateKey, name: templateKey, kind: 'template', width: 200, height: 200, placeholders: [],
    document: { schemaVersion: 1, width: 320, height: 200, background: '#101b2d', nodes: [node('pump', 'symbol', { symbol: 'isa:pump' })] } } })
  expect(templateResponse.ok(), await templateResponse.text()).toBeTruthy()
  const name = `旧发布${suffix}`, screen = await create(request, headers, templateKey, name)
  const exported = await request.get('/api/project/export', { headers })
  expect(exported.ok()).toBeTruthy()
  const project = await exported.json()
  const entry = project.sections.screens.find((item: { key: string }) => item.key === name)
  expect(entry).toBeTruthy()
  entry.data.published = screen.document
  const previewResponse = await request.post('/api/project/import/preview', { headers, data: { package: project, resolution: 'Overwrite' } })
  expect(previewResponse.ok(), await previewResponse.text()).toBeTruthy()
  const preview = await previewResponse.json()
  expect(preview.errors).toEqual([])
  const imported = await request.post('/api/project/import', { headers, data: { package: project, resolution: 'Overwrite', baseSha: preview.baseSha } })
  expect(imported.ok(), await imported.text()).toBeTruthy()
  const blocked = await request.get(`/api/screens/${screen.id}`, { headers })
  expect(blocked.status()).toBe(200)
  expect((await blocked.json()).requiresRepublish).toBe(true)
  await page.goto(`/screens/${screen.id}`)
  await expect(page.locator('[data-test="runtime-screen-error"]')).toContainText('重新发布')
  await expect(page.locator('[data-test="runtime-stage"]')).toHaveCount(0)
  await page.locator('[data-test="runtime-edit"]').click()
  await expect(page).toHaveURL(new RegExp(`/config/screens/${screen.id}`))
  await page.getByRole('button', { name: '发布', exact: true }).click()
  await expect(page.getByText('已发布，运行画面已更新')).toBeVisible()
  await page.goto(`/screens/${screen.id}`)
  await expect(page.locator('[data-test="runtime-screen-error"]')).toHaveCount(0)
  await expect(page.locator('[data-symbol="isa:pump"]')).toHaveCount(1)
})
