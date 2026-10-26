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

test('notification save, delete, test and acknowledge failures stay on the page', async ({ page, request }) => {
  const session = await login(request)
  const suffix = `${Date.now()}`
  const channel = await request.post('/api/notification-channels', {
    headers: session.headers,
    data: { name: `通道${suffix}`, type: 'Webhook', enabled: true, settings: { url: 'http://127.0.0.1:9/hook', port: 25, useSsl: false, timeoutMs: 5000 }, secret: null }
  })
  expect(channel.ok()).toBeTruthy()
  const channelId = (await channel.json()).id
  const route = await request.post('/api/notification-routes', {
    headers: session.headers,
    data: { name: `路由${suffix}`, enabled: true, channelId, areaId: null, minLevel: 'Warning', shiftId: null, recipients: '', onRaise: true, onRecover: false, escalateAfterMinutes: null }
  })
  expect(route.ok()).toBeTruthy()
  const routeId = (await route.json()).id
  await sign(page, session)
  await page.route((url) => url.pathname === '/api/notification-routes', (route) => {
    if (route.request().method() !== 'POST') return route.continue()
    return route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message: '路由不可用' }) })
  })
  await page.route((url) => url.pathname === `/api/notification-routes/${routeId}`, (route) => {
    if (route.request().method() !== 'DELETE') return route.continue()
    return route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message: '删除不可用' }) })
  })
  await page.route((url) => url.pathname === '/api/notification-channels', (route) => {
    if (route.request().method() !== 'POST') return route.continue()
    return route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message: '通道不可用' }) })
  })
  await page.route((url) => url.pathname === `/api/notification-channels/${channelId}/test`, (route) =>
    route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message: '测试不可用' }) }))
  await page.route((url) => url.pathname === '/api/duty-shifts', (route) => {
    if (route.request().method() !== 'POST') return route.continue()
    return route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message: '班次不可用' }) })
  })
  await page.route((url) => url.pathname === '/api/notification-outbox/failed', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify([{
      id: 4, alarmEventId: 8, alarmMessage: '泵超限', routeId, channelId, action: 'raise', attempts: 3, lastError: '超时', finishedTs: Date.now()
    }]) }))
  await page.route((url) => url.pathname === '/api/notification-outbox/acknowledge', (route) =>
    route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message: '已知悉不可用' }) }))
  await page.goto('/config/notifications')
  await page.locator('[data-test="route-add"]').click()
  const routeDialog = page.getByRole('dialog', { name: '新增通知路由' })
  await routeDialog.getByRole('textbox', { name: '名称' }).fill(`失败路由${suffix}`)
  await routeDialog.locator('[data-test="route-save"]').click()
  await expect(routeDialog.locator('[data-test="route-save-error"]')).toContainText('路由不可用')
  await expect(routeDialog).toBeVisible()
  await routeDialog.getByRole('button', { name: '取消', exact: true }).click()
  await expect(routeDialog).not.toBeVisible()
  const routeRow = page.getByRole('row').filter({ hasText: `路由${suffix}` })
  await routeRow.getByRole('button', { name: '删除', exact: true }).click()
  await page.locator('.el-popconfirm').getByRole('button', { name: '确定' }).click()
  await expect(page.locator('[data-test="notifications-action-error"]')).toContainText('删除不可用')
  await expect(routeRow).toBeVisible()
  await page.getByRole('tab', { name: '通知通道' }).click()
  await page.getByRole('row').filter({ hasText: `通道${suffix}` }).getByRole('button', { name: '测试', exact: true }).click()
  await expect(page.locator('[data-test="notifications-action-error"]')).toContainText('测试不可用')
  await page.locator('[data-test="channel-add"]').click()
  const channelDialog = page.getByRole('dialog', { name: '新增通道' })
  await channelDialog.locator('[data-test="channel-name"]').fill(`新通道${suffix}`)
  await channelDialog.locator('[data-test="channel-url"]').fill('http://127.0.0.1:9/new')
  await channelDialog.locator('[data-test="channel-save"]').click()
  await expect(channelDialog.locator('[data-test="channel-save-error"]')).toContainText('通道不可用')
  await expect(channelDialog).toBeVisible()
  await channelDialog.getByRole('button', { name: '取消', exact: true }).click()
  await page.getByRole('tab', { name: '班次', exact: true }).click()
  await page.getByRole('button', { name: '新增班次', exact: true }).click()
  const shiftDialog = page.getByRole('dialog', { name: '新增班次' })
  await shiftDialog.getByRole('textbox', { name: '名称' }).fill(`夜班${suffix}`)
  await shiftDialog.getByRole('button', { name: '保存', exact: true }).click()
  await expect(shiftDialog.locator('[data-test="shift-save-error"]')).toContainText('班次不可用')
  await expect(shiftDialog).toBeVisible()
  await shiftDialog.getByRole('button', { name: '取消', exact: true }).click()
  await page.getByRole('tab', { name: /投递失败/ }).click()
  await page.locator('[data-test="outbox-ack-all"]').click()
  await expect(page.locator('[data-test="notifications-ack-error"]')).toContainText('已知悉不可用')
  await expect(page.locator('[data-test="outbox-failed-table"]')).toContainText('泵超限')
})

test('annunciator catalog, save, silence and test failures stay on the page', async ({ page, request }) => {
  const session = await login(request)
  const suffix = `${Date.now()}`
  const created = await request.post('/api/annunciators', {
    headers: session.headers,
    data: {
      name: `声光${suffix}`, areaId: null, enabled: true, latchMode: 'Follow', silenceMinutes: 5,
      flashOnMs: 400, flashOffMs: 400, criticalFlashOnMs: 200, criticalFlashOffMs: 200, browserSound: true,
      outputs: [], levels: []
    }
  })
  expect(created.ok(), await created.text()).toBeTruthy()
  const panel = await created.json()
  await sign(page, session)
  await page.route((url) => url.pathname === '/api/areas', (route) => {
    if (route.request().method() !== 'GET') return route.continue()
    return route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message: '区域不可用' }) })
  })
  await page.route((url) => url.pathname === '/api/annunciators', (route) => {
    if (route.request().method() !== 'POST') return route.continue()
    return route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message: '保存不可用' }) })
  })
  await page.route((url) => url.pathname === `/api/annunciators/${panel.id}/silence`, (route) =>
    route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message: '消音不可用' }) }))
  await page.route((url) => url.pathname === `/api/annunciators/${panel.id}/test`, (route) =>
    route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message: '试验不可用' }) }))
  await page.goto('/config/annunciators')
  await expect(page.locator('[data-test="ann-catalog-error"]')).toContainText('区域不可用')
  await expect(page.getByRole('row').filter({ hasText: `声光${suffix}` })).toBeVisible()
  await page.locator('[data-test="ann-add"]').click()
  const dialog = page.getByRole('dialog', { name: '新建声光面板' })
  await dialog.getByRole('textbox', { name: '名称' }).fill(`新面板${suffix}`)
  await dialog.locator('[data-test="ann-save"]').click()
  await expect(dialog.locator('[data-test="ann-save-error"]')).toContainText('保存不可用')
  await expect(dialog).toBeVisible()
  await dialog.getByRole('button', { name: '取消', exact: true }).click()
  await expect(dialog).not.toBeVisible()
  const row = page.getByRole('row').filter({ hasText: `声光${suffix}` })
  await row.getByRole('button', { name: '消音', exact: true }).click()
  await expect(page.locator('[data-test="ann-action-error"]')).toContainText('消音不可用')
  await row.getByRole('button', { name: '试验', exact: true }).click()
  await expect(page.locator('[data-test="ann-action-error"]')).toContainText('试验不可用')
  await expect(row).toContainText(`声光${suffix}`)
})
