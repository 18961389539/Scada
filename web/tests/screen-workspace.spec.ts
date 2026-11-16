import { test, expect, applySession, loginApi, type APIRequestContext } from './fixtures'
import type { ScreenDocument, ScreenNode } from '../src/screens/model'

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
})
