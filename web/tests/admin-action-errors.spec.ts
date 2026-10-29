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

test('user save, password, delete and grant failures stay visible', async ({ page, request }) => {
  const session = await login(request)
  const name = `u${Date.now()}`
  const created = await request.post('/api/users', {
    headers: session.headers,
    data: { userName: name, password: 'old-password', displayName: '操作员', role: 'Viewer', enabled: true }
  })
  expect(created.ok(), await created.text()).toBeTruthy()
  const userId = (await created.json()).id as number
  await sign(page, session)
  await page.route((url) => url.pathname === '/api/users', (route) => {
    if (route.request().method() !== 'POST') return route.continue()
    return route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message: '保存不可用' }) })
  })
  await page.route((url) => url.pathname === `/api/users/${userId}/password`, (route) =>
    route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message: '重置不可用' }) }))
  await page.route((url) => url.pathname === `/api/users/${userId}`, (route) => {
    if (route.request().method() !== 'DELETE') return route.continue()
    return route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message: '删除不可用' }) })
  })
  await page.route((url) => url.pathname === `/api/users/${userId}/assignments`, (route) => {
    if (route.request().method() !== 'PUT') return route.continue()
    return route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message: '授权不可用' }) })
  })
  await page.goto('/system')
  await page.getByRole('button', { name: '新建用户', exact: true }).click()
  const userDialog = page.getByRole('dialog', { name: '新建用户' })
  await userDialog.locator('[data-test="user-name"]').fill(`n${Date.now()}`)
  await userDialog.locator('[data-test="user-password"]').fill('secret123')
  await userDialog.locator('[data-test="user-display"]').fill('新人')
  await userDialog.getByRole('button', { name: '保存', exact: true }).click()
  await expect(userDialog.locator('[data-test="user-save-error"]')).toContainText('保存不可用')
  await expect(userDialog).toBeVisible()
  await userDialog.getByRole('button', { name: '取消', exact: true }).click()
  await expect(userDialog).not.toBeVisible()

  const row = page.getByRole('row').filter({ hasText: name })
  await row.getByRole('button', { name: '重置密码', exact: true }).click()
  const passwordDialog = page.getByRole('dialog', { name: '重置密码' })
  await passwordDialog.locator('input[type="password"]').fill('Newpass1')
  await passwordDialog.getByRole('button', { name: '确定', exact: true }).click()
  await expect(passwordDialog.locator('[data-test="password-error"]')).toContainText('重置不可用')
  await expect(passwordDialog).toBeVisible()
  await passwordDialog.getByRole('button', { name: '取消', exact: true }).click()

  await row.getByRole('button', { name: '删除', exact: true }).click()
  await page.getByRole('dialog', { name: '删除用户' }).getByRole('button', { name: '确定' }).click()
  await expect(page.locator('[data-test="users-action-error"]')).toContainText('删除不可用')
  await expect(row).toBeVisible()

  await row.locator('[data-test="user-assign"]').click()
  const grant = page.getByRole('dialog', { name: `区域授权：${name}` })
  await grant.locator('[data-test="assignment-save"]').click()
  await expect(grant.locator('[data-test="assignment-save-error"]')).toContainText('授权不可用')
  await expect(grant).toBeVisible()
})

test('switchover, backup and restore-drill failures stay on the ops page', async ({ page, request }) => {
  const session = await login(request)
  await sign(page, session)
  await page.route((url) => url.pathname === '/api/ha/status', (route) => route.fulfill({
    status: 200, contentType: 'application/json',
    body: JSON.stringify({
      enabled: true, nodeId: 'node-a', role: 'Primary', term: 2, generation: 1, selfUrl: null, peerUrl: 'http://127.0.0.1:9',
      peer: null, peerSeenTs: 0, witness: null, lease: null, witnessError: null, autoFailover: true, promotedTs: 0,
      configSyncTs: 0, historySyncTs: 0, historyRowsBehind: 0, configApplied: 0, historyRowsApplied: 0, replicationError: null, switchovers: 1,
      events: []
    })
  }))
  await page.route((url) => url.pathname === '/api/ha/switchover', (route) =>
    route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message: '切换不可用' }) }))
  await page.route((url) => url.pathname === '/api/maintenance/backups/run', (route) =>
    route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message: '备份不可用' }) }))
  await page.route((url) => url.pathname === '/api/maintenance/backups', (route) => {
    if (route.request().method() !== 'GET') return route.continue()
    return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify([{ fileName: 'demo.db', bytes: 10, createdAt: new Date().toISOString() }]) })
  })
  await page.route((url) => url.pathname === '/api/maintenance/backups/demo.db/drill', (route) =>
    route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message: '演练不可用' }) }))
  await page.goto('/ops')
  await page.locator('[data-test="ha-switchover"]').click()
  const prompt = page.getByRole('dialog', { name: '切换到备机' })
  await prompt.getByRole('textbox').fill('演练')
  await prompt.getByRole('button', { name: '确定' }).click()
  await expect(page.locator('[data-test="ops-action-error"]')).toContainText('切换不可用')
  await expect(page.locator('[data-test="ha-role"]')).toContainText('主机')

  await page.locator('[data-test="backup-run"]').click()
  await expect(page.locator('[data-test="ops-action-error"]')).toContainText('备份不可用')
  const backupRow = page.getByRole('row').filter({ hasText: 'demo.db' })
  await expect(backupRow).toBeVisible()
  await backupRow.getByRole('button', { name: '恢复演练', exact: true }).click()
  await expect(page.locator('[data-test="ops-action-error"]')).toContainText('演练不可用')
  await expect(backupRow).toBeVisible()
})

test('object template create, open and delete failures stay on the page', async ({ page, request }) => {
  const session = await login(request)
  const suffix = `${Date.now()}`
  const pump = await request.get('/api/object-templates/examples/pump', { headers: session.headers })
  expect(pump.ok()).toBeTruthy()
  const created = await request.post('/api/object-templates', {
    headers: session.headers,
    data: { name: `模板${suffix}`, revision: 0, document: await pump.json() }
  })
  expect(created.ok(), await created.text()).toBeTruthy()
  const template = await created.json()
  expect((await request.post(`/api/object-templates/${template.id}/publish`, { headers: session.headers, data: { revision: template.revision } })).ok()).toBeTruthy()
  await sign(page, session)
  await page.route((url) => url.pathname === `/api/object-templates/${template.id}`, (route) =>
    route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message: route.request().method() === 'DELETE' ? '删除不可用' : '打开不可用' }) }))
  await page.route((url) => url.pathname === '/api/object-templates', (route) => {
    if (route.request().method() !== 'POST') return route.continue()
    return route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message: '创建不可用' }) })
  })
  await page.goto('/config/object-templates')
  const card = page.locator('.template-card').filter({ hasText: `模板${suffix}` })
  await expect(card.getByRole('button', { name: '批量生成对象', exact: true })).toBeEnabled()
  await card.getByRole('button', { name: '批量生成对象', exact: true }).click()
  await expect(page.locator('[data-test="object-action-error"]')).toContainText('打开不可用')
  await expect(page.getByRole('dialog', { name: /批量生成/ })).not.toBeVisible()

  await page.getByRole('button', { name: '新建模板', exact: true }).click()
  const prompt = page.getByRole('dialog', { name: '新建模板' })
  await prompt.getByRole('textbox').fill(`失败模板${suffix}`)
  await prompt.getByRole('button', { name: '确定' }).click()
  await expect(page.locator('[data-test="object-action-error"]')).toContainText('创建不可用')
  await expect(page).toHaveURL(/\/config\/object-templates$/)

  await card.getByRole('button', { name: '删除', exact: true }).click()
  await page.getByRole('dialog', { name: '删除模板' }).getByRole('button', { name: '确定' }).click()
  await expect(page.locator('[data-test="object-action-error"]')).toContainText('删除不可用')
  await expect(card).toBeVisible()
})
