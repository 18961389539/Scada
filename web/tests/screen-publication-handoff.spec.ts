import { test, expect, type APIRequestContext, type Page } from './fixtures'
import { openRuntimeMenu } from './runtime-menu'

async function login(request: APIRequestContext) {
  const response = await request.post('/api/auth/login', { data: { userName: 'admin', password: 'admin123' } })
  expect(response.ok(), await response.text()).toBeTruthy()
  return await response.json()
}
async function sign(page: Page, auth: { token: string; user: unknown }): Promise<void> {
  await page.addInitScript(({ token, user }) => {
    localStorage.setItem('scada_token', token)
    localStorage.setItem('scada_user', JSON.stringify(user))
    localStorage.setItem('scada_locale', 'zh-CN')
  }, auth)
}
function document(text: string) {
  return { schemaVersion: 1, width: 1200, height: 720, background: '#101b2d', nodes: [{
    id: 'label', type: 'text', x: 40, y: 40, width: 400, height: 72, rotation: 0, text,
    color: '#7394b8', activeColor: '#34d399', unit: '', decimals: 1, min: 0, max: 100,
    activeThreshold: .5, tagId: null, action: { type: 'none', tagId: null, screenId: null, value: 1 }
  }] }
}

// 画面发布后，已打开的运行端通过 SignalR 立即感知；交互画面先暂停控制，刷新后才恢复。
test('runtime pauses on a newly published screen and reloads on demand', async ({ page, request }) => {
  const auth = await login(request)
  const headers = { Authorization: `Bearer ${auth.token}` }
  const name = `发布接管${Date.now()}`
  const created = await request.post('/api/screens', { headers, data: { name } })
  expect(created.status()).toBe(201)
  const screen = await created.json()
  const saved = await request.put(`/api/screens/${screen.id}`, { headers, data: { name, revision: screen.revision, document: document(name) } })
  expect(saved.ok(), await saved.text()).toBeTruthy()
  const draft = await saved.json()
  const published = await request.post(`/api/screens/${screen.id}/publish`, { headers, data: { revision: draft.revision } })
  expect(published.ok(), await published.text()).toBeTruthy()
  const first = await published.json()

  await sign(page, auth)
  await page.goto(`/screens/${screen.id}`)
  await expect(page.locator('.runtime-live')).toBeVisible()

  await openRuntimeMenu(page)
  await expect(page.locator('[data-test="runtime-version"]')).toContainText(String(first.publishedRevision))
  await page.locator('.runtime-toolbar > strong').click()

  const current = await (await request.get(`/api/screens/${screen.id}?draft=true`, { headers })).json()
  const updated = await request.put(`/api/screens/${screen.id}`, { headers, data: { name, revision: current.revision, document: document(name + ' v2') } })
  expect(updated.ok(), await updated.text()).toBeTruthy()
  const nextDraft = await updated.json()
  const next = await request.post(`/api/screens/${screen.id}/publish`, { headers, data: { revision: nextDraft.revision } })
  expect(next.ok(), await next.text()).toBeTruthy()
  const second = await next.json()

  await expect(page.locator('[data-test="runtime-new-version"]')).toBeVisible({ timeout: 25000 })
  await expect(page.locator('[data-test="runtime-new-version"]')).toContainText(`r${second.publishedRevision}`)
  await page.locator('[data-test="runtime-reload-version"]').click()
  await openRuntimeMenu(page)
  await expect(page.locator('[data-test="runtime-version"]')).toContainText(String(second.publishedRevision))
})
