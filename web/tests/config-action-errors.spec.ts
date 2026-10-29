import { test, expect, type APIRequestContext, type Page } from './fixtures'

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

test('external key catalog, create, copy, revoke and toggle failures stay visible', async ({ page, request }) => {
  const session = await login(request)
  const suffix = `${Date.now()}`
  const created = await request.post('/api/external/keys', {
    headers: session.headers,
    data: { name: `密钥${suffix}`, scopes: ['ReadTags'], rateLimitPerMinute: 120 }
  })
  expect(created.ok()).toBeTruthy()
  const keyId = (await created.json()).key.id as number
  await sign(page, session)
  await page.addInitScript(() => {
    Object.defineProperty(navigator, 'clipboard', { configurable: true, value: { writeText: () => Promise.reject(new Error('denied')) } })
  })
  await page.route((url) => url.pathname === '/api/areas', (route) => {
    if (route.request().method() !== 'GET') return route.continue()
    return route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message: '区域不可用' }) })
  })
  await page.route((url) => url.pathname === `/api/external/keys/${keyId}/revoke`, (route) =>
    route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message: '吊销不可用' }) }))
  await page.route((url) => url.pathname === `/api/external/keys/${keyId}/enabled`, (route) =>
    route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message: '启停不可用' }) }))
  await page.goto('/config/external-api')
  await expect(page.locator('[data-test="ext-areas-error"]')).toContainText('区域不可用')
  await expect(page.locator('[data-test="external-load-error"]')).toHaveCount(0)
  const row = page.getByRole('row').filter({ hasText: `密钥${suffix}` })
  await expect(row).toBeVisible()

  await page.locator('[data-test="ext-add"]').click()
  const createDialog = page.getByRole('dialog', { name: '新建 API 密钥' })
  await createDialog.locator('[data-test="ext-name"]').fill(`复制${suffix}`)
  await createDialog.locator('[data-test="ext-save"]').click()
  const secretDialog = page.getByRole('dialog', { name: '请立即复制密钥' })
  await secretDialog.locator('[data-test="ext-copy"]').click()
  await expect(secretDialog.locator('[data-test="ext-copy-error"]')).toContainText('复制失败')
  await secretDialog.getByRole('button', { name: '已保存' }).click()
  await expect(secretDialog).not.toBeVisible()

  await page.route((url) => url.pathname === '/api/external/keys', (route) => {
    if (route.request().method() !== 'POST') return route.continue()
    return route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message: '创建不可用' }) })
  })
  await page.locator('[data-test="ext-add"]').click()
  await createDialog.locator('[data-test="ext-name"]').fill(`失败${suffix}`)
  await createDialog.locator('[data-test="ext-save"]').click()
  await expect(createDialog.locator('[data-test="ext-save-error"]')).toContainText('创建不可用')
  await expect(createDialog).toBeVisible()
  await createDialog.getByRole('button', { name: '取消', exact: true }).click()
  await expect(createDialog).not.toBeVisible()

  await row.locator('.el-switch').click()
  await expect(page.locator('[data-test="ext-action-error"]')).toContainText('启停不可用')
  await expect(row).toBeVisible()
  await row.locator('[data-test="ext-revoke"]').click()
  await page.locator('.el-popconfirm').getByRole('button', { name: '确定' }).click()
  await expect(page.locator('[data-test="ext-action-error"]')).toContainText('吊销不可用')
  await expect(row).toBeVisible()
})

test('screen create and delete failures stay on the page', async ({ page, request }) => {
  const session = await login(request)
  const suffix = `${Date.now()}`
  const created = await request.post('/api/screens', { headers: session.headers, data: { name: `画面${suffix}` } })
  expect(created.ok()).toBeTruthy()
  const screen = await created.json()
  await sign(page, session)
  await page.route((url) => url.pathname === '/api/screens', (route) => {
    if (route.request().method() !== 'POST') return route.continue()
    return route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message: '创建不可用' }) })
  })
  await page.route((url) => url.pathname === `/api/screens/${screen.id}`, (route) => {
    if (route.request().method() !== 'DELETE') return route.continue()
    return route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message: '删除不可用' }) })
  })
  await page.goto('/screens')
  const card = page.locator('.screen-card').filter({ hasText: `画面${suffix}` })
  await expect(card).toBeVisible()
  await page.getByRole('button', { name: '新建画面', exact: true }).first().click()
  const prompt = page.getByRole('dialog', { name: '新建工艺画面' })
  await prompt.getByRole('textbox').fill(`失败画面${suffix}`)
  await prompt.getByRole('button', { name: '确定' }).click()
  await expect(page.locator('[data-test="screens-action-error"]')).toContainText('创建不可用')
  await expect(page).toHaveURL(/\/screens$/)
  await card.getByRole('button', { name: '删除', exact: true }).click()
  const confirm = page.getByRole('dialog', { name: '删除画面' })
  await confirm.getByRole('button', { name: '确定' }).click()
  await expect(page.locator('[data-test="screens-action-error"]')).toContainText('删除不可用')
  await expect(card).toBeVisible()
})

test('area and role save failures stay in the dialog and delete failures stay on the page', async ({ page, request }) => {
  const session = await login(request)
  const suffix = `${Date.now()}`
  const area = await request.post('/api/areas', { headers: session.headers, data: { name: `区域${suffix}` } })
  expect(area.ok()).toBeTruthy()
  const areaId = (await area.json()).id as number
  const role = await request.post('/api/roles', { headers: session.headers, data: { name: `角色${suffix}`, permissions: ['View'] } })
  expect(role.ok()).toBeTruthy()
  const roleId = (await role.json()).id as number
  await sign(page, session)
  await page.route((url) => url.pathname === '/api/areas', (route) => {
    if (route.request().method() !== 'POST') return route.continue()
    return route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message: '区域保存不可用' }) })
  })
  await page.route((url) => url.pathname === `/api/areas/${areaId}`, (route) => {
    if (route.request().method() !== 'DELETE') return route.continue()
    return route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message: '区域删除不可用' }) })
  })
  await page.route((url) => url.pathname === '/api/roles', (route) => {
    if (route.request().method() !== 'POST') return route.continue()
    return route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message: '角色保存不可用' }) })
  })
  await page.route((url) => url.pathname === `/api/roles/${roleId}`, (route) => {
    if (route.request().method() !== 'DELETE') return route.continue()
    return route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message: '角色删除不可用' }) })
  })
  await page.goto('/system')
  await page.getByRole('tab', { name: '区域与角色' }).click()
  await page.locator('[data-test="area-create"]').click()
  const areaDialog = page.getByRole('dialog', { name: '新建区域' })
  await areaDialog.locator('[data-test="area-name"]').fill(`新区${suffix}`)
  await areaDialog.locator('[data-test="area-save"]').click()
  await expect(areaDialog.locator('[data-test="area-save-error"]')).toContainText('区域保存不可用')
  await expect(areaDialog).toBeVisible()
  await areaDialog.getByRole('button', { name: '取消', exact: true }).click()
  await expect(areaDialog).not.toBeVisible()
  const areaRow = page.getByRole('row').filter({ hasText: `区域${suffix}` })
  await areaRow.getByRole('button', { name: '删除', exact: true }).click()
  await page.getByRole('dialog', { name: '确认' }).getByRole('button', { name: '确定' }).click()
  await expect(page.locator('[data-test="access-action-error"]')).toContainText('区域删除不可用')
  await expect(areaRow).toBeVisible()

  await page.getByRole('tab', { name: '角色', exact: true }).click()
  await page.locator('[data-test="role-create"]').click()
  const roleDialog = page.getByRole('dialog', { name: '新建角色' })
  await roleDialog.locator('[data-test="role-name"]').fill(`新角色${suffix}`)
  await roleDialog.locator('[data-test="role-save"]').click()
  await expect(roleDialog.locator('[data-test="role-save-error"]')).toContainText('角色保存不可用')
  await expect(roleDialog).toBeVisible()
  await roleDialog.getByRole('button', { name: '取消', exact: true }).click()
  const roleRow = page.getByRole('row').filter({ hasText: `角色${suffix}` })
  await roleRow.getByRole('button', { name: '删除', exact: true }).click()
  await page.getByRole('dialog', { name: '确认' }).getByRole('button', { name: '确定' }).click()
  await expect(page.locator('[data-test="access-action-error"]')).toContainText('角色删除不可用')
  await expect(roleRow).toBeVisible()
})
