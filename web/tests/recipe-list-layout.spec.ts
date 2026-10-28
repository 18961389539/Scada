import { test, expect, type APIRequestContext, type Page } from '@playwright/test'

async function prepare(page: Page, request: APIRequestContext, readOnly = false) {
  const response = await request.post('/api/auth/login', { data: { userName: 'admin', password: 'admin123' } })
  expect(response.ok()).toBeTruthy()
  const session = await response.json()
  if (readOnly) session.user = { ...session.user, role: 'Viewer', access: { global: ['View'], areas: [] } }
  await page.addInitScript(({ token, user }) => {
    localStorage.setItem('scada_token', token)
    localStorage.setItem('scada_user', JSON.stringify(user))
    localStorage.setItem('scada_sound', 'off')
  }, session)
  const parameters = [
    { key: 'pressure', name: '出口压力', unit: 'MPa', tagId: 821 },
    { key: 'speed', name: '转速', unit: 'rpm', tagId: 822 },
    { key: 'temp', name: '温度', unit: '℃', tagId: 823 },
    { key: 'hold', name: '保压时间', unit: 's', tagId: 824 }
  ].map(parameter => ({ ...parameter, min: null, max: null, defaultValue: null }))
  const template = { id: 811, name: '泵站模板', areaId: 1, parameters, revision: 1, dualApproval: false }
  const recipe = { id: 812, name: '夏季配方', templateId: 811, templateName: '泵站模板', areaId: 1, version: 2, updatedTs: 1, updatedBy: 'admin', description: null, note: null,
    values: [{ key: 'pressure', value: 12.123456 }, { key: 'speed', value: 900 }, { key: 'temp', value: 180 }, { key: 'hold', value: 15 }] }
  const data: Record<string, unknown> = {
    '/api/areas': [{ id: 1, name: '一车间' }], '/api/devices': [{ id: 831, name: '泵站', areaId: 1 }],
    '/api/tags': parameters.map(parameter => ({ id: parameter.tagId, name: parameter.name, deviceId: 831, writable: true })),
    '/api/recipes/templates': [template], '/api/recipes': [recipe], '/api/recipes/downloads': [], '/api/recipes/812/versions': []
  }
  await page.route(url => Object.hasOwn(data, url.pathname), route => route.fulfill({ json: data[new URL(route.request().url()).pathname] }))
  await page.goto('/recipes')
  const row = page.locator('[data-test="recipes"] .el-table__row')
  await expect(row).toHaveCount(1)
  return row
}

for (const width of [390, 1280]) {
  test(`recipe list shows readable parameters and compact actions at ${width}px`, async ({ page, request }) => {
    await page.setViewportSize({ width, height: 900 })
    const row = await prepare(page, request)
    await expect(row.locator('.recipe-actions button')).toHaveCount(3)
    const pressure = row.locator('[data-test="recipe-parameter-pressure"]')
    await expect(pressure).toContainText('出口压力')
    await expect(pressure).toContainText('12.123456')
    await expect(pressure).toContainText('MPa')
    await expect(pressure).toHaveCSS('font-size', '14px')
    const hiddenParameter = row.locator('[data-test="recipe-parameter-hold"]')
    await expect(hiddenParameter).toBeHidden()
    await row.locator('.recipe-parameter-details summary').click()
    await expect(hiddenParameter).toContainText('保压时间')
    await expect(hiddenParameter).toBeVisible()
    await row.locator('[data-test="recipe-more-812"]').click()
    const versions = page.locator('[data-test="recipe-versions-812"]:visible')
    await expect(versions).toBeVisible()
    const bounds = await versions.boundingBox()
    expect(bounds!.x).toBeGreaterThanOrEqual(0)
    expect(bounds!.x + bounds!.width).toBeLessThanOrEqual(width)
    await page.screenshot({ path: test.info().outputPath(`recipe-list-${width}.png`), animations: 'disabled' })
  })
}

test('recipe more actions support keyboard use and retain delete confirmation', async ({ page, request }) => {
  const row = await prepare(page, request)
  await row.locator('[data-test="recipe-more-812"]').focus()
  await page.keyboard.press('Enter')
  const versions = page.locator('[data-test="recipe-versions-812"]:visible')
  await expect(versions).toBeVisible()
  await versions.press('Enter')
  await expect(page.locator('[data-test="recipe-versions"]')).toBeVisible()
  await page.keyboard.press('Escape')
  await row.locator('[data-test="recipe-more-812"]').click()
  await page.locator('[data-test="recipe-delete-812"]:visible').click()
  const confirmation = page.locator('.el-message-box')
  await expect(confirmation).toBeVisible()
  await confirmation.getByRole('button', { name: '取消', exact: true }).click()
  await expect(row).toContainText('夏季配方')
})

test('read-only recipe menus expose versions and comparison without write actions', async ({ page, request }) => {
  const row = await prepare(page, request, true)
  await expect(row.getByRole('button', { name: '下发', exact: true })).toHaveCount(0)
  await expect(row.getByRole('button', { name: '编辑', exact: true })).toHaveCount(0)
  await row.locator('[data-test="recipe-more-812"]').click()
  await expect(page.locator('[data-test="recipe-versions-812"]:visible')).toBeVisible()
  await expect(page.locator('[data-test="recipe-compare-live-812"]:visible')).toBeVisible()
  await expect(page.locator('[data-test="recipe-upload-812"]')).toHaveCount(0)
  await expect(page.locator('[data-test="recipe-delete-812"]')).toHaveCount(0)
})
