import { test, expect, type Page } from './fixtures'

const alarm = (id: number, message: string, level = 'Critical', tagId: number | null = 41) => ({
  id, message, level, tagId, deviceId: tagId ? 101 : null, ruleId: id, areas: [1], value: 12,
  startTs: Date.now() - 60_000, endTs: null, ackTs: null, ackUser: null, state: 'Unack',
  shelved: false, suppressed: false
})

async function signIn(page: Page): Promise<void> {
  const response = await page.request.post('/api/auth/login', { data: { userName: 'admin', password: 'admin123' } })
  expect(response.ok()).toBeTruthy()
  await page.addInitScript(({ token, user }) => {
    localStorage.setItem('scada_token', token)
    localStorage.setItem('scada_user', JSON.stringify(user))
  }, await response.json())
  await page.route(url => url.pathname === '/api/devices', route => route.fulfill({ json: [{ id: 101, name: '锅炉甲', areaId: 1 }] }))
  await page.route(url => url.pathname === '/api/tags', route => route.fulfill({ json: [{ id: 41, deviceId: 101, name: '出口温度', unit: '℃', dataType: 'Float' }] }))
}

test('first-out, shelved time, silence and screen lookup stay on the alarm page', async ({ page }) => {
  await signIn(page)
  const live = [alarm(101, '锅炉首出'), alarm(102, '出口预警', 'Warning')]
  await page.route(url => url.pathname === '/api/alarms/page', route => route.fulfill({ json: { items: live, total: live.length, offset: 0, limit: 100, capped: false } }))
  await page.route(url => url.pathname === '/api/alarms/summary', route => route.fulfill({ json: {
    unack: 2, ackableUnack: 2, acked: 0, rtnUnack: 0, shelved: 1, suppressed: 0, outOfServiceRules: 0,
    floodGuard: {
      autoSuppress: false, firstOut: true, suppressMaxLevel: 'Critical', anyInFlood: true,
      areas: [{ areaId: 1, areaName: '锅炉', count: 12, threshold: 10, exitThreshold: 8, inFlood: true, windowStartTs: Date.now() - 60_000, windowEndTs: Date.now(), floodStartTs: Date.now() - 60_000, peak: 12, firstOutEventId: 101, suppressedCount: 0 }]
    }
  } }))
  await page.route(url => url.pathname === '/api/alarm-shelves', route => route.fulfill({ json: [{
    id: 1, ruleId: 9, tagId: 41, reason: '仪表校验', user: 'admin', startTs: Date.now() - 60_000, untilTs: Date.now() + 45 * 60_000
  }] }))
  await page.route(url => url.pathname === '/api/annunciators', route => route.fulfill({ json: [{ id: 3, name: '一号声光', areaId: 1, enabled: true }] }))
  await page.route(url => url.pathname === '/api/annunciators/3/silence', route => route.fulfill({ json: { panelId: 3, silenced: true, silenceUntilTs: Date.parse('2026-11-02T18:30:00') } }))
  await page.route(url => url.pathname === '/api/screens', route => route.fulfill({ json: [
    { id: 7, name: '锅炉总览', revision: 2, publishedRevision: 2 },
    { id: 8, name: '未发布草稿', revision: 1, publishedRevision: null }
  ] }))
  await page.route(url => url.pathname === '/api/screens/7', route => route.fulfill({ json: {
    id: 7, name: '锅炉总览', revision: 2, publishedRevision: 2,
    document: { nodes: [{ id: 'n1', tagId: 41, action: { tagId: null } }] }
  } }))

  await page.goto('/alarms')
  await page.locator('[data-test="flood-first-out"]').click()
  await expect(page.getByRole('row').filter({ hasText: '锅炉首出' }).locator('[data-test="ack"]')).toBeFocused()

  await page.getByRole('tab', { name: /暂挂/ }).click()
  const shelf = page.getByRole('row').filter({ hasText: '仪表校验' })
  await expect(shelf).toContainText('锅炉甲 / 出口温度')
  await expect(shelf.locator('[data-test="shelf-remaining"]')).toContainText(/剩余 \d+ 分钟/)

  await page.getByRole('tab', { name: '实时报警', exact: true }).click()
  await page.locator('[data-test="alarm-more"]').click()
  await page.locator('[data-test="silence-ann"]').click()
  await page.getByRole('dialog', { name: '请确认' }).getByRole('button', { name: '声光消音' }).click()
  await expect(page.locator('[data-test="alarm-silence-until"]')).toContainText('消音至')

  const warning = page.getByRole('row').filter({ hasText: '出口预警' })
  await warning.locator('[data-test="alarm-row-more"]').click()
  await warning.locator('[data-test="alarm-open-screen"]').click()
  await expect(page).toHaveURL(/\/screens\/7/)
})

test('an alarm without a published screen says so instead of leaving the list', async ({ page }) => {
  await signIn(page)
  await page.route(url => url.pathname === '/api/alarms/page', route => route.fulfill({ json: { items: [alarm(201, '孤立测点', 'Warning', 77)], total: 1, offset: 0, limit: 100, capped: false } }))
  await page.route(url => url.pathname === '/api/alarms/summary', route => route.fulfill({ json: { unack: 1, ackableUnack: 1, acked: 0, rtnUnack: 0, shelved: 0, suppressed: 0, outOfServiceRules: 0 } }))
  await page.route(url => url.pathname === '/api/alarm-shelves', route => route.fulfill({ json: [] }))
  await page.route(url => url.pathname === '/api/screens', route => route.fulfill({ json: [] }))
  await page.goto('/alarms')
  const isolated = page.getByRole('row').filter({ hasText: '孤立测点' })
  await isolated.locator('[data-test="alarm-row-more"]').click()
  await isolated.locator('[data-test="alarm-open-screen"]').click()
  await expect(page.locator('[data-test="alarms-action-error"]')).toContainText('没有包含这个测点的已发布画面')
  await expect(page).toHaveURL(/\/alarms/)
})
