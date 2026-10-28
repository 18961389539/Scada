import { test, expect, type Page } from '@playwright/test'

const alarm = (id: number, message: string, tagId: number | null, deviceId: number | null, level = 'Warning') => ({
  id, message, tagId, deviceId, level, ruleId: null, areas: [1], value: 12,
  startTs: Date.now() - id * 1000, endTs: null, ackTs: null, ackUser: null, state: 'Unack'
})
const devices = [{ id: 101, name: '锅炉甲', areaId: 1 }, { id: 102, name: '空压机乙', areaId: 1 }]
const tags = [{ id: 41, deviceId: 101, name: '出口温度', unit: '℃', dataType: 'Float' }]

async function signIn(page: Page): Promise<void> {
  const response = await page.request.post('/api/auth/login', { data: { userName: 'admin', password: 'admin123' } })
  expect(response.ok()).toBeTruthy()
  await page.addInitScript(({ token, user }) => {
    localStorage.setItem('scada_token', token)
    localStorage.setItem('scada_user', JSON.stringify(user))
  }, await response.json())
  await page.route(url => url.pathname === '/api/devices', route => route.fulfill({ json: devices }))
  await page.route(url => url.pathname === '/api/tags', route => route.fulfill({ json: tags }))
}

test('alarm rows identify tag, device-only and system sources and open the selected tag trend', async ({ page }) => {
  await signIn(page)
  const items = [alarm(1, '温度过高', 41, null), alarm(2, '通信中断', null, 102), alarm(3, '系统事件测试', null, null), alarm(4, '已删除测点', 999, 888)]
  await page.route(url => url.pathname === '/api/alarms/page', route => route.fulfill({ json: { items, total: items.length, offset: 0, limit: 100, capped: false } }))
  await page.goto('/alarms')
  const temperature = page.getByRole('row').filter({ hasText: '温度过高' })
  await expect(temperature.locator('[data-test="alarm-source"]')).toContainText('锅炉甲 / 出口温度')
  await expect(page.getByRole('row').filter({ hasText: '通信中断' }).locator('[data-test="alarm-source"]')).toContainText('空压机乙')
  await expect(page.getByRole('row').filter({ hasText: '系统事件测试' }).locator('[data-test="alarm-source"]')).toContainText('系统事件')
  await expect(page.getByRole('row').filter({ hasText: '已删除测点' }).locator('[data-test="alarm-source"]')).toContainText('设备 #888 / 测点 #999')
  await expect(page.getByRole('row').filter({ hasText: '通信中断' }).locator('[data-test="alarm-trend"]')).toHaveCount(0)
  await temperature.locator('[data-test="alarm-trend"]').click()
  await expect(page).toHaveURL(/\/trends\?tagId=41$/)
})

test('catalog failures preserve alarm rows, expose ID fallbacks and recover source names on retry', async ({ page }) => {
  await signIn(page)
  let catalogFails = true
  await page.route(url => url.pathname === '/api/devices', route => catalogFails
    ? route.fulfill({ status: 500, json: { message: '设备目录暂不可用' } }) : route.fulfill({ json: devices }))
  await page.route(url => url.pathname === '/api/tags', route => catalogFails
    ? route.fulfill({ status: 500, json: { message: '测点目录暂不可用' } }) : route.fulfill({ json: tags }))
  const items = [alarm(1, '目录异常时保留报警', 41, 101)]
  await page.route(url => url.pathname === '/api/alarms/page', route => route.fulfill({ json: { items, total: 1, offset: 0, limit: 100, capped: false } }))
  await page.goto('/alarms')
  const row = page.getByRole('row').filter({ hasText: '目录异常时保留报警' })
  await expect(row.locator('[data-test="alarm-source"]')).toContainText('设备 #101 / 测点 #41')
  await expect(page.locator('[data-test="alarm-source-error"]')).toBeVisible()
  await expect(row.locator('[data-test="alarm-trend"]')).toHaveAttribute('href', '/trends?tagId=41')
  catalogFails = false
  await page.locator('[data-test="alarm-source-retry"]').click()
  await expect(row.locator('[data-test="alarm-source"]')).toContainText('锅炉甲 / 出口温度')
  await expect(page.locator('[data-test="alarm-source-error"]')).toBeHidden()
})

test('live severity selection requests server sorting and history keeps chronological queries', async ({ page }) => {
  await signIn(page)
  const critical = alarm(10, '较早严重报警', 41, 101, 'Critical')
  const info = alarm(1, '最新提示报警', 41, 101, 'Info')
  const queries: URLSearchParams[] = []
  await page.route(url => url.pathname === '/api/alarms/page', route => {
    const params = new URL(route.request().url()).searchParams
    queries.push(params)
    const items = params.get('live') === 'true' && params.get('priorityFirst') === 'true' ? [critical, info] : [info, critical]
    return route.fulfill({ json: { items, total: 2, offset: 0, limit: 100, capped: false } })
  })
  await page.goto('/alarms')
  const firstMessage = page.locator('.el-table__body-wrapper .alarm-message').first()
  await expect(firstMessage).toHaveText('较早严重报警')
  await page.locator('[data-test="alarm-sort"]').click()
  await page.getByRole('option', { name: '最新发生优先' }).click()
  await expect(firstMessage).toHaveText('最新提示报警')
  expect(queries.at(-1)?.get('priorityFirst')).toBe('false')
  await page.getByRole('tab', { name: '历史报警' }).click()
  await expect(firstMessage).toHaveText('最新提示报警')
  await expect.poll(() => queries.at(-1)?.has('live')).toBe(false)
  expect(queries.at(-1)?.has('priorityFirst')).toBe(false)
  await expect(page.locator('[data-test="alarm-sort"]')).toBeHidden()
})
