import { test, expect, type APIRequestContext, type Page } from '@playwright/test'

async function login(request: APIRequestContext) {
  const response = await request.post('/api/auth/login', { data: { userName: 'admin', password: 'admin123' } })
  expect(response.ok()).toBeTruthy()
  const body = await response.json()
  return { token: body.token as string, user: body.user, headers: { Authorization: `Bearer ${body.token}` } }
}

async function sign(page: Page, session: { token: string; user: unknown }): Promise<void> {
  await page.addInitScript((data) => {
    localStorage.setItem('scada_token', data.token)
    localStorage.setItem('scada_user', JSON.stringify(data.user))
  }, session)
}

async function deviceAndTag(request: APIRequestContext, headers: Record<string, string>, name: string) {
  const device = await request.post('/api/devices', {
    headers,
    data: { name, protocol: 'ModbusTcp', host: '127.0.0.1', port: 1502, stationNumber: 1, byteOrder: 'ABCD', pollIntervalMs: 1000, timeoutMs: 1000, enabled: false, areaId: 1 }
  })
  expect(device.ok(), await device.text()).toBeTruthy()
  const deviceId = (await device.json()).id as number
  const tag = await request.post('/api/tags', {
    headers,
    data: {
      deviceId, name: `${name}点`, area: 'HoldingRegister', address: 0, dataType: 'UInt16',
      scale: 1, offset: 0, writable: true, archiveMode: 'None', archivePeriodSec: 60, deadband: 0
    }
  })
  expect(tag.ok(), await tag.text()).toBeTruthy()
  return { deviceId, tagId: (await tag.json()).id as number }
}

test('recipe template save and deletes stay visible', async ({ page, request }) => {
  const session = await login(request)
  const suffix = `${Date.now()}`
  const { tagId } = await deviceAndTag(request, session.headers, `配方${suffix}`)
  const template = await request.post('/api/recipes/templates', {
    headers: session.headers,
    data: { name: `模板${suffix}`, areaId: 1, dualApproval: false, parameters: [{ key: 'temp', name: '温度', tagId, unit: '℃', min: 0, max: 100 }] }
  })
  expect(template.ok(), await template.text()).toBeTruthy()
  const templateId = (await template.json()).id as number
  const recipe = await request.post('/api/recipes', {
    headers: session.headers,
    data: { templateId, name: `配方值${suffix}`, values: [{ key: 'temp', value: 10 }], note: '初版' }
  })
  expect(recipe.ok(), await recipe.text()).toBeTruthy()
  const recipeId = (await recipe.json()).recipe.id as number
  await sign(page, session)
  await page.route((url) => url.pathname === `/api/recipes/${recipeId}`, (route) => {
    if (route.request().method() !== 'DELETE') return route.continue()
    return route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message: '删除配方不可用' }) })
  })
  await page.route((url) => url.pathname === `/api/recipes/templates/${templateId}`, (route) => {
    if (route.request().method() !== 'DELETE') return route.continue()
    return route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message: '删除模板不可用' }) })
  })
  await page.goto('/recipes')
  const recipeRow = page.locator('[data-test="recipes"]').getByRole('row').filter({ hasText: `配方值${suffix}` })
  await recipeRow.getByRole('button', { name: '删除', exact: true }).click()
  await page.getByRole('dialog', { name: '删除' }).getByRole('button', { name: '确定' }).click()
  await expect(page.locator('[data-test="recipes-action-error"]')).toContainText('删除配方不可用')
  await expect(recipeRow).toBeVisible()
  await page.getByRole('tab', { name: '模板' }).click()
  await page.locator('[data-test="template-new"]').click()
  const dialog = page.getByRole('dialog', { name: '新建配方模板' })
  await dialog.locator('[data-test="template-name"]').fill(`新模板${suffix}`)
  await dialog.locator('[data-test="template-save"]').click()
  await expect(dialog.locator('[data-test="recipe-template-error"]')).toContainText('参数须为')
  await expect(dialog).toBeVisible()
  await dialog.getByRole('button', { name: '取消', exact: true }).click()
  const templateRow = page.locator('[data-test="recipe-templates"]').getByRole('row').filter({ hasText: `模板${suffix}` })
  await templateRow.getByRole('button', { name: '删除', exact: true }).click()
  await page.getByRole('dialog', { name: '删除' }).getByRole('button', { name: '确定' }).click()
  await expect(page.locator('[data-test="recipes-action-error"]')).toContainText('删除模板不可用')
  await expect(templateRow).toBeVisible()
})

test('sequence save, delete and lock failures stay visible', async ({ page, request }) => {
  const session = await login(request)
  const suffix = `${Date.now()}`
  const { deviceId } = await deviceAndTag(request, session.headers, `顺控${suffix}`)
  const sequence = await request.post('/api/control/sequences', {
    headers: session.headers,
    data: { name: `顺控${suffix}`, areaId: 1, dualApproval: false, steps: [{ id: 's1', type: 'end', name: '结束' }] }
  })
  expect(sequence.ok(), await sequence.text()).toBeTruthy()
  const sequenceId = (await sequence.json()).id as number
  const device = await (await request.get('/api/devices', { headers: session.headers })).json()
  const deviceName = device.find((x: { id: number; name: string }) => x.id === deviceId).name as string
  await sign(page, session)
  await page.route((url) => url.pathname === `/api/control/sequences/${sequenceId}`, (route) => {
    if (route.request().method() !== 'DELETE') return route.continue()
    return route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message: '删除顺控不可用' }) })
  })
  await page.route((url) => url.pathname === '/api/control/lockouts', (route) => {
    if (route.request().method() !== 'POST') return route.continue()
    return route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message: '挂牌不可用' }) })
  })
  await page.goto('/control')
  await page.getByRole('tab', { name: '顺控' }).click()
  await page.locator('[data-test="sequence-new"]').click()
  const dialog = page.getByRole('dialog', { name: '新建顺控' })
  await dialog.locator('[data-test="sequence-save"]').click()
  await expect(dialog.locator('[data-test="sequence-save-error"]')).toContainText('名称须为')
  await expect(dialog).toBeVisible()
  await dialog.getByRole('button', { name: '取消', exact: true }).click()
  const row = page.locator('[data-test="sequences"]').getByRole('row').filter({ hasText: `顺控${suffix}` })
  await row.getByRole('button', { name: '删除', exact: true }).click()
  await page.getByRole('dialog', { name: '删除' }).getByRole('button', { name: '确定' }).click()
  await expect(page.locator('[data-test="control-action-error"]')).toContainText('删除顺控不可用')
  await expect(row).toBeVisible()
  await page.getByRole('tab', { name: '挂牌' }).click()
  await page.locator('[data-test="lock-target"]').click()
  await page.getByRole('option', { name: deviceName, exact: true }).click()
  await page.locator('[data-test="lock-reason"]').fill(`检修${suffix}`)
  await page.locator('[data-test="lock-place"]').click()
  await expect(page.locator('[data-test="control-action-error"]')).toContainText('挂牌不可用')
  await expect(page.locator('[data-test="lockouts"]')).not.toContainText(`检修${suffix}`)
})

test('tag save, delete, export and device catalog failures stay visible', async ({ page, request }) => {
  const session = await login(request)
  const suffix = `${Date.now()}`
  const created = await deviceAndTag(request, session.headers, `测点${suffix}`)
  await sign(page, session)
  await page.route((url) => url.pathname === '/api/tags/export', (route) =>
    route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message: '导出不可用' }) }))
  await page.route((url) => url.pathname === `/api/tags/${created.tagId}`, (route) => {
    const method = route.request().method()
    if (method !== 'PUT' && method !== 'DELETE') return route.continue()
    const message = method === 'DELETE' ? '删除不可用' : '保存不可用'
    return route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message }) })
  })
  await page.goto('/config/tags')
  await page.getByRole('button', { name: '导出 Excel', exact: true }).click()
  await expect(page.locator('[data-test="tags-action-error"]')).toContainText('导出不可用')
  const row = page.getByRole('row').filter({ hasText: `测点${suffix}点` })
  await row.getByRole('button', { name: '编辑', exact: true }).click()
  const dialog = page.getByRole('dialog', { name: '编辑测点' })
  await dialog.locator('[data-test="tag-save"]').click()
  await expect(dialog.locator('[data-test="tag-form-error"]')).toContainText('保存不可用')
  await expect(dialog).toBeVisible()
  await dialog.getByRole('button', { name: '取消', exact: true }).click()
  await row.getByRole('button', { name: '删除', exact: true }).click()
  await page.locator('.el-popconfirm').getByRole('button', { name: '确定' }).click()
  await expect(page.locator('[data-test="tags-action-error"]')).toContainText('删除不可用')
  await expect(row).toBeVisible()
})

test('tag page keeps a device catalog failure', async ({ page, request }) => {
  const session = await login(request)
  await sign(page, session)
  await page.route((url) => url.pathname === '/api/devices', (route) => {
    if (route.request().method() !== 'GET') return route.continue()
    return route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message: '设备目录不可用' }) })
  })
  await page.goto('/config/tags')
  await expect(page.locator('[data-test="tags-devices-error"]')).toContainText('设备目录不可用')
  await expect(page.locator('[data-test="tags-load-error"]')).toHaveCount(0)
})

test('device save and delete failures stay visible', async ({ page, request }) => {
  const session = await login(request)
  const name = `设备${Date.now()}`
  const created = await request.post('/api/devices', {
    headers: session.headers,
    data: { name, protocol: 'ModbusTcp', host: '127.0.0.1', port: 1502, stationNumber: 1, byteOrder: 'ABCD', pollIntervalMs: 1000, timeoutMs: 1000, enabled: false, areaId: 1 }
  })
  expect(created.ok(), await created.text()).toBeTruthy()
  const id = (await created.json()).id as number
  await sign(page, session)
  await page.route((url) => url.pathname === `/api/devices/${id}`, (route) => {
    const method = route.request().method()
    if (method !== 'PUT' && method !== 'DELETE') return route.continue()
    const message = method === 'DELETE' ? '删除不可用' : '保存不可用'
    return route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message }) })
  })
  await page.goto('/config/devices')
  const row = page.getByRole('row').filter({ hasText: name })
  await row.getByRole('button', { name: '编辑', exact: true }).click()
  const dialog = page.getByRole('dialog', { name: '编辑设备' })
  await dialog.locator('[data-test="device-save"]').click()
  await expect(dialog.locator('[data-test="device-form-error"]')).toContainText('保存不可用')
  await expect(dialog).toBeVisible()
  await dialog.getByRole('button', { name: '取消', exact: true }).click()
  await row.getByRole('button', { name: '删除', exact: true }).click()
  await page.locator('.el-popconfirm').getByRole('button', { name: '确定' }).click()
  await expect(page.locator('[data-test="devices-action-error"]')).toContainText('删除不可用')
  await expect(row).toBeVisible()
})
