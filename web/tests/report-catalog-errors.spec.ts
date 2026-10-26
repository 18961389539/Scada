import { test, expect, type APIRequestContext, type Page } from '@playwright/test'

async function signIn(page: Page): Promise<void> {
  await page.goto('/login')
  await page.getByPlaceholder(/用户名/).fill('admin')
  await page.getByPlaceholder(/密码/).fill('admin123')
  await page.keyboard.press('Enter')
  await expect(page).toHaveURL(/overview/)
}

async function login(request: APIRequestContext) {
  const response = await request.post('/api/auth/login', { data: { userName: 'admin', password: 'admin123' } })
  expect(response.ok()).toBeTruthy()
  return { Authorization: `Bearer ${(await response.json()).token}` }
}

test('report editor keeps tag, job, and channel catalog failures', async ({ page, request }) => {
  const headers = await login(request)
  const suffix = Date.now()
  const tagName = `RPT-T-${suffix}`
  const jobName = `RPT-J-${suffix}`
  const channelName = `RPT-MAIL-${suffix}`
  const device = await request.post('/api/devices', {
    headers,
    data: {
      name: `RPT-D-${suffix}`, protocol: 'ModbusTcp', host: '127.0.0.1', port: 30000 + (suffix % 20000), stationNumber: 1,
      byteOrder: 'ABCD', pollIntervalMs: 1000, timeoutMs: 1000, enabled: false, areaId: 1
    }
  })
  expect(device.ok(), await device.text()).toBeTruthy()
  const tag = await request.post('/api/tags', {
    headers,
    data: {
      deviceId: (await device.json()).id, name: tagName, area: 'HoldingRegister', address: 0, dataType: 'Float',
      scale: 1, offset: 0, writable: false, archiveMode: 'None', archivePeriodSec: 60, deadband: 0
    }
  })
  expect(tag.ok(), await tag.text()).toBeTruthy()
  const job = await request.post('/api/calc-jobs', {
    headers,
    data: { name: jobName, kind: 'Accumulator', areaId: 1, inputTagId: (await tag.json()).id, mode: 'Flow', factor: 1, resetPeriod: 'Day' }
  })
  expect(job.ok(), await job.text()).toBeTruthy()
  const channel = await request.post('/api/notification-channels', {
    headers,
    data: {
      name: channelName, type: 'Email', enabled: true,
      settings: { host: '127.0.0.1', port: 25, useSsl: false, from: 'scada@plant.local', timeoutMs: 5000 }, secret: null
    }
  })
  expect(channel.ok(), await channel.text()).toBeTruthy()

  await signIn(page)
  let fail = true
  const messages: Record<string, string> = {
    '/api/tags': '测点列表不可用',
    '/api/calc-jobs': '计算任务不可用',
    '/api/notification-channels': '通知通道不可用'
  }
  await page.route((url) => url.pathname in messages, (route) => {
    if (!fail || route.request().method() !== 'GET') return route.continue()
    const message = messages[new URL(route.request().url()).pathname]
    return route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message }) })
  })
  await page.goto('/report-center')
  await expect(page.locator('[data-test="report-tags-error"]')).toContainText('测点列表不可用')
  await expect(page.locator('[data-test="report-jobs-error"]')).toContainText('计算任务不可用')
  await expect(page.locator('[data-test="report-channels-error"]')).toContainText('通知通道不可用')
  await expect(page.locator('[data-test="report-template-new"]')).toBeVisible()

  await page.locator('[data-test="report-template-new"]').click()
  const dialog = page.locator('.el-dialog:visible')
  await expect(dialog.locator('[data-test="report-editor-tags-error"]')).toContainText('测点列表不可用')
  await expect(dialog.locator('[data-test="report-editor-jobs-error"]')).toContainText('计算任务不可用')
  await expect(dialog.locator('[data-test="report-editor-channels-error"]')).toContainText('通知通道不可用')
  await dialog.locator('[data-test="report-add-column"]').click()
  await dialog.locator('[data-test="report-column-tag-0"]').click()
  await expect(page.getByRole('option', { name: new RegExp(tagName) })).toHaveCount(0)
  await page.keyboard.press('Escape')
  await dialog.locator('[data-test="report-column-kind-0"]').click()
  await page.getByRole('option', { name: '计算快照' }).click()
  await dialog.locator('[data-test="report-column-job-0"]').click()
  await expect(page.getByRole('option', { name: jobName })).toHaveCount(0)
  await page.keyboard.press('Escape')
  await dialog.locator('[data-test="report-email-channel"]').click()
  await expect(page.getByRole('option', { name: channelName })).toHaveCount(0)
  await page.keyboard.press('Escape')
  await dialog.getByRole('button', { name: '取消' }).click()
  await expect(dialog).not.toBeVisible()

  fail = false
  await page.getByRole('button', { name: '刷新', exact: true }).click()
  await expect(page.locator('[data-test="report-tags-error"]')).toHaveCount(0)
  await expect(page.locator('[data-test="report-jobs-error"]')).toHaveCount(0)
  await expect(page.locator('[data-test="report-channels-error"]')).toHaveCount(0)
  await page.locator('[data-test="report-template-new"]').click()
  const restored = page.locator('.el-dialog:visible')
  await restored.locator('[data-test="report-add-column"]').click()
  await restored.locator('[data-test="report-column-tag-0"]').click()
  await expect(page.getByRole('option', { name: new RegExp(tagName) })).toBeVisible()
  await page.keyboard.press('Escape')
  await restored.locator('[data-test="report-column-kind-0"]').click()
  await page.getByRole('option', { name: '计算快照' }).click()
  await restored.locator('[data-test="report-column-job-0"]').click()
  await expect(page.getByRole('option', { name: jobName })).toBeVisible()
  await page.keyboard.press('Escape')
  await restored.locator('[data-test="report-email-channel"]').click()
  await expect(page.getByRole('option', { name: channelName })).toBeVisible()
})
