import { test, expect, type APIRequestContext, type Page } from './fixtures'
import type { ObjectTemplateDocument } from '../src/api/objectTemplates'

async function setup(request: APIRequestContext) {
  const auth = await (await request.post('/api/auth/login', { data: { userName: 'admin', password: 'admin123' } })).json()
  const headers = { Authorization: `Bearer ${auth.token}` }
  const suffix = `${Date.now()}-${Math.floor(Math.random() * 10000)}`
  const deviceResponse = await request.post('/api/devices', {
    headers,
    data: { name: `PLC-${suffix}`, protocol: 'ModbusTcp', host: '127.0.0.1', port: 1502, stationNumber: 1, byteOrder: 'ABCD', pollIntervalMs: 1000, timeoutMs: 1000, enabled: false, areaId: 1 }
  })
  expect(deviceResponse.ok(), await deviceResponse.text()).toBeTruthy()
  const device = await deviceResponse.json()
  const document = await (await request.get('/api/object-templates/examples/pump', { headers })).json() as ObjectTemplateDocument
  const templateResponse = await request.post('/api/object-templates', { headers, data: { name: `模板-${suffix}`, revision: 0, document } })
  expect(templateResponse.status(), await templateResponse.text()).toBe(201)
  const template = await templateResponse.json()
  const publish = await request.post(`/api/object-templates/${template.id}/publish`, { headers, data: { revision: template.revision } })
  expect(publish.ok(), await publish.text()).toBeTruthy()
  return { ...auth, headers, suffix, device, document, template, published: await publish.json() }
}

async function sign(page: Page, session: { token: string; user: unknown }): Promise<void> {
  await page.addInitScript((data) => {
    localStorage.setItem('scada_token', data.token)
    localStorage.setItem('scada_user', JSON.stringify(data.user))
  }, session)
}

test('object template version, preview, and create failures stay in the dialog', async ({ page, request }) => {
  const fixture = await setup(request)
  await sign(page, fixture)
  let versionFail = true
  let previewFail = false
  let applyFail = false
  await page.route((url) => url.pathname === `/api/object-templates/${fixture.template.id}/versions/1`, (route) => {
    if (versionFail && route.request().method() === 'GET') return route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message: '版本不可用' }) })
    return route.continue()
  })
  await page.route((url) => url.pathname === `/api/object-templates/${fixture.template.id}/instantiate/preview`, (route) => {
    if (previewFail) return route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message: '预览不可用' }) })
    return route.continue()
  })
  await page.route((url) => url.pathname === `/api/object-templates/${fixture.template.id}/instantiate`, (route) => {
    if (applyFail && route.request().method() === 'POST') return route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message: '生成不可用' }) })
    return route.continue()
  })
  await page.goto('/config/object-templates')
  const card = page.locator('.template-card').filter({ hasText: `模板-${fixture.suffix}` })
  await card.getByRole('button', { name: '批量生成对象', exact: true }).click()
  const dialog = page.getByRole('dialog')
  await expect(dialog.locator('[data-test="object-document-error"]')).toContainText('版本不可用')
  await expect(dialog.getByRole('button', { name: '预览生成', exact: true })).toBeDisabled()
  await dialog.getByRole('button', { name: '取消', exact: true }).click()
  await expect(dialog).not.toBeVisible()
  versionFail = false
  previewFail = true
  await card.getByRole('button', { name: '批量生成对象', exact: true }).click()
  await expect(dialog.locator('[data-test="object-document-error"]')).toHaveCount(0)
  await dialog.getByRole('button', { name: '预览生成', exact: true }).click()
  await expect(dialog.locator('[data-test="object-create-error"]')).toContainText('预览不可用')
  await expect(dialog.getByText('可生成', { exact: true })).toHaveCount(0)
  await expect(dialog.getByRole('button', { name: '确认生成', exact: true })).toBeDisabled()
  previewFail = false
  await dialog.getByRole('button', { name: '预览生成', exact: true }).click()
  await expect(dialog.getByText('可生成', { exact: true })).toHaveCount(1)
  await expect(dialog.locator('[data-test="object-create-error"]')).toHaveCount(0)
  applyFail = true
  await dialog.getByRole('button', { name: '确认生成', exact: true }).click()
  await expect(dialog.locator('[data-test="object-create-error"]')).toContainText('生成不可用')
  await expect(dialog.getByText('可生成', { exact: true })).toHaveCount(1)
  await expect(dialog.getByRole('button', { name: '确认生成', exact: true })).toBeEnabled()
})

test('object template upgrade preview and apply failures stay in the dialog', async ({ page, request }) => {
  const fixture = await setup(request)
  const created = await request.post(`/api/object-templates/${fixture.template.id}/instantiate`, {
    headers: fixture.headers,
    data: { version: 1, instances: [{ name: `升-${fixture.suffix}`, deviceId: fixture.device.id, baseAddress: 20 }] }
  })
  expect(created.ok(), await created.text()).toBeTruthy()
  const current = await (await request.get(`/api/object-templates/${fixture.template.id}`, { headers: fixture.headers })).json()
  fixture.document.tags[0].properties.description = '新版描述'
  const save = await request.put(`/api/object-templates/${fixture.template.id}`, {
    headers: fixture.headers,
    data: { name: current.name, revision: current.revision, document: fixture.document }
  })
  expect(save.ok(), await save.text()).toBeTruthy()
  const saved = await save.json()
  const publish = await request.post(`/api/object-templates/${fixture.template.id}/publish`, { headers: fixture.headers, data: { revision: saved.revision } })
  expect(publish.ok(), await publish.text()).toBeTruthy()
  await sign(page, fixture)
  let previewFail = true
  let applyFail = false
  await page.route((url) => /\/api\/object-instances\/\d+\/upgrade\/preview$/.test(url.pathname), (route) => {
    if (previewFail) return route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message: '升级预览不可用' }) })
    return route.continue()
  })
  await page.route((url) => url.pathname === '/api/object-instances/upgrade', (route) => {
    if (applyFail) return route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message: '升级不可用' }) })
    return route.continue()
  })
  await page.goto('/config/object-templates')
  await page.getByRole('tab', { name: '对象实例', exact: true }).click()
  await page.getByRole('row').filter({ hasText: `升-${fixture.suffix}` }).getByRole('button', { name: '预览升级', exact: true }).click()
  const dialog = page.getByRole('dialog')
  await dialog.getByRole('button', { name: '预览升级', exact: true }).click()
  await expect(dialog.locator('[data-test="object-upgrade-error"]')).toContainText('升级预览不可用')
  await expect(dialog.getByText('可升级', { exact: true })).toHaveCount(0)
  await expect(dialog.getByRole('button', { name: '确认升级', exact: true })).toBeDisabled()
  previewFail = false
  await dialog.getByRole('button', { name: '预览升级', exact: true }).click()
  await expect(dialog.getByText('可升级', { exact: true })).toBeVisible()
  await expect(dialog.locator('[data-test="object-upgrade-error"]')).toHaveCount(0)
  await expect(dialog.getByRole('button', { name: '确认升级', exact: true })).toBeEnabled()
  applyFail = true
  await dialog.getByRole('button', { name: '确认升级', exact: true }).click()
  await expect(dialog.locator('[data-test="object-upgrade-error"]')).toContainText('升级不可用')
  await expect(dialog.getByText('可升级', { exact: true })).toBeVisible()
  await expect(dialog.getByRole('button', { name: '确认升级', exact: true })).toBeDisabled()
})
