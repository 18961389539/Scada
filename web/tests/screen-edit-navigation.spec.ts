import { test, expect, type APIRequestContext, type Page } from './fixtures'
import type { ScreenDocument } from '../src/screens/model'

async function login(request: APIRequestContext, userName = 'admin', password = 'admin123') {
  const response = await request.post('/api/auth/login', { data: { userName, password } })
  expect(response.ok(), await response.text()).toBeTruthy()
  const auth = await response.json()
  return { ...auth, headers: { Authorization: `Bearer ${auth.token}` } }
}

async function useSession(page: Page, auth: { token: string; user: unknown }) {
  await page.addInitScript(({ token, user }) => {
    localStorage.setItem('scada_token', token)
    localStorage.setItem('scada_user', JSON.stringify(user))
    localStorage.setItem('scada_locale', 'zh-CN')
  }, auth)
}

async function createScreen(request: APIRequestContext, headers: Record<string, string>, name: string, publish = true) {
  const created = await request.post('/api/screens', { headers, data: { name } })
  expect(created.status()).toBe(201)
  const screen = await created.json()
  const document: ScreenDocument = {
    schemaVersion: 1, width: 1200, height: 720, background: '#101b2d', nodes: [{
      id: 'label', type: 'text', x: 60, y: 60, width: 600, height: 80, rotation: 0,
      text: name, color: '#7394b8', activeColor: '#34d399', unit: '', decimals: 1,
      min: 0, max: 100, activeThreshold: .5, tagId: null,
      action: { type: 'none', screenId: null, tagId: null, value: 0 }
    }]
  }
  const saved = await request.put(`/api/screens/${screen.id}`, { headers, data: { name, revision: screen.revision, document } })
  expect(saved.ok(), await saved.text()).toBeTruthy()
  const result = await saved.json()
  if (publish) {
    const published = await request.post(`/api/screens/${screen.id}/publish`, { headers, data: { revision: result.revision } })
    expect(published.ok(), await published.text()).toBeTruthy()
  }
  return { id: screen.id as number, name }
}

test('a running screen opens its own editor and returning retains the unsaved-change guard', async ({ page, request }) => {
  const auth = await login(request)
  const screen = await createScreen(request, auth.headers, `NAV-锅炉-${Date.now()}`)
  await useSession(page, auth)
  const runningPath = `/screens/${screen.id}?mode=monitor`
  await page.goto(runningPath)
  await page.locator('[data-test="runtime-edit"]').click()
  await expect(page).toHaveURL(url => url.pathname === `/config/screens/${screen.id}` && url.searchParams.get('returnTo') === runningPath)
  const name = page.getByRole('textbox', { name: '画面名称', exact: true })
  await expect(name).toHaveValue(screen.name)
  const returnButton = page.locator('[data-test="designer-open-runtime"]')
  await expect(returnButton).toBeEnabled()
  await name.fill(`${screen.name}-未保存`)
  await returnButton.click()
  const warning = page.getByRole('dialog', { name: '未保存修改', exact: true })
  await expect(warning).toContainText('画面有未保存修改，确认离开？')
  await warning.getByRole('button', { name: '继续编辑', exact: true }).click()
  await expect(name).toHaveValue(`${screen.name}-未保存`)
  await expect(page).toHaveURL(url => url.pathname === `/config/screens/${screen.id}`)
  await returnButton.click()
  await warning.getByRole('button', { name: '离开', exact: true }).click()
  await expect(page).toHaveURL(runningPath)
  await expect(page.locator('.runtime-toolbar strong')).toHaveText(screen.name)
  const draft = await request.get(`/api/screens/${screen.id}?draft=true`, { headers: auth.headers })
  expect((await draft.json()).name).toBe(screen.name)
})

test('editing a navigated console region returns to the same monitor and current screen', async ({ page, request }) => {
  const auth = await login(request), suffix = Date.now()
  const home = await createScreen(request, auth.headers, `NAV-主画面-${suffix}`)
  const detail = await createScreen(request, auth.headers, `NAV-详情画面-${suffix}`)
  const response = await request.post('/api/hmi/layouts', { headers: auth.headers, data: {
    name: `NAV-双屏-${suffix}`, document: { monitors: [
      { name: '主屏', columns: 12, rows: 12, regions: [{ id: 'main', kind: 'screen', screenId: home.id, row: 1, col: 1, rowSpan: 12, colSpan: 12 }] },
      { name: '副屏', columns: 12, rows: 12, regions: [
        { id: 'nav', kind: 'navigation', row: 1, col: 1, rowSpan: 12, colSpan: 2 },
        { id: 'detail', kind: 'screen', screenId: home.id, row: 1, col: 3, rowSpan: 12, colSpan: 10 }
      ] }
    ] }
  } })
  expect(response.ok(), await response.text()).toBeTruthy()
  const layout = await response.json()
  const publication = await request.post(`/api/hmi/layouts/${layout.id}/publish`, { headers: auth.headers, data: { revision: layout.revision, note: '画面导航验收' } })
  expect(publication.ok(), await publication.text()).toBeTruthy()
  await useSession(page, auth)
  await page.goto(`/console/${layout.id}?monitor=1`)
  await page.locator(`[data-test="nav-${detail.id}"]`).click()
  const region = page.locator('[data-test="region-detail"]')
  await expect(region.locator('[data-node-id="label"]')).toContainText(detail.name)
  await region.locator('[data-test="runtime-more"]').click()
  await page.locator('[data-test="runtime-edit"]:visible').click()
  await expect(page).toHaveURL(url => url.pathname === `/config/screens/${detail.id}`)
  const returnPath = new URL(page.url()).searchParams.get('returnTo')
  expect(returnPath).toBeTruthy()
  const returnUrl = new URL(returnPath!, 'http://localhost')
  expect(returnUrl.pathname).toBe(`/console/${layout.id}`)
  expect(returnUrl.searchParams.get('monitor')).toBe('1')
  expect(returnUrl.searchParams.get('runtimeRegion')).toBe('detail')
  expect(returnUrl.searchParams.get('runtimeScreen')).toBe(String(detail.id))
  await page.locator('[data-test="designer-open-runtime"]').click()
  await expect(page).toHaveURL(url => url.pathname === `/console/${layout.id}` && url.searchParams.get('monitor') === '1')
  await expect(region.locator('[data-node-id="label"]')).toContainText(detail.name)
})

test('a viewer can run a published screen without being offered a configuration shortcut', async ({ page, request }) => {
  const admin = await login(request), suffix = Date.now()
  const screen = await createScreen(request, admin.headers, `NAV-只读画面-${suffix}`)
  const created = await request.post('/api/users', { headers: admin.headers, data: {
    userName: `nav-viewer-${suffix}`, password: 'navigation-test-123', displayName: '导航查看员', role: 'Viewer', enabled: true
  } })
  expect(created.ok(), await created.text()).toBeTruthy()
  const user = await created.json()
  const viewer = await login(request, user.userName, 'navigation-test-123')
  await useSession(page, viewer)
  await page.goto(`/screens/${screen.id}`)
  await expect(page.locator('[data-node-id="label"]')).toBeVisible()
  await expect(page.locator('[data-test="runtime-edit"]')).toHaveCount(0)
})

test('unpublished drafts cannot open a published runtime and their previews have no edit shortcut', async ({ page, request }) => {
  const auth = await login(request)
  const screen = await createScreen(request, auth.headers, `NAV-草稿-${Date.now()}`, false)
  await useSession(page, auth)
  await page.goto(`/config/screens/${screen.id}`)
  await expect(page.getByRole('textbox', { name: '画面名称', exact: true })).toHaveValue(screen.name)
  await expect(page.locator('[data-test="designer-open-runtime"]')).toBeDisabled()
  await page.getByRole('button', { name: '预览', exact: true }).click()
  const preview = page.getByRole('dialog')
  await expect(preview.locator('[data-testid="screen-runtime"]')).toBeVisible()
  await expect(preview.locator('[data-node-id="label"]')).toContainText(screen.name)
  await expect(preview.locator('[data-test="runtime-edit"]')).toHaveCount(0)
})

test('invalid return targets fall back to this screen runtime', async ({ page, request }) => {
  const auth = await login(request)
  const screen = await createScreen(request, auth.headers, `NAV-返回地址-${Date.now()}`)
  await useSession(page, auth)
  for (const returnTo of ['https://example.invalid/console/1', '//example.invalid/screens/1', '/config/devices']) {
    await page.goto(`/config/screens/${screen.id}?returnTo=${encodeURIComponent(returnTo)}`)
    const runtime = page.locator('[data-test="designer-open-runtime"]')
    await expect(runtime).toBeEnabled()
    await runtime.click()
    await expect(page).toHaveURL(`/screens/${screen.id}`)
    await expect(page.locator('[data-node-id="label"]')).toBeVisible()
  }
})
