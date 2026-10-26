import { test, expect, type APIRequestContext, type Page } from '@playwright/test'
import type { AlarmEvent } from '../src/types'

const alarm = (id: number, message: string, level: AlarmEvent['level'] = 'Critical', patch: Partial<AlarmEvent> = {}): AlarmEvent => ({
  id, message, level, ruleId: id, tagId: id + 1000, deviceId: null, areas: [1], state: 'Unack',
  startTs: Date.now() - 60000, endTs: null, ackTs: null, ackUser: null, value: 10,
  shelved: false, suppressed: false, chattering: false, gatewayId: null, ...patch
})
const rows = (page: Page) => page.locator('.el-table__body-wrapper .el-table__row')
const row = (page: Page, message: string) => page.getByRole('row').filter({ has: page.getByText(message, { exact: true }) })

async function prepare(page: Page, request: APIRequestContext, restricted = false) {
  const response = await request.post('/api/auth/login', { data: { userName: 'admin', password: 'admin123' } })
  expect(response.ok()).toBeTruthy()
  const auth = await response.json()
  if (restricted) auth.user.access = { global: ['View'], areas: [{ areaId: 1, permissions: ['AckAlarm'] }] }
  await page.addInitScript(({ token, user }) => {
    localStorage.setItem('scada_token', token)
    localStorage.setItem('scada_user', JSON.stringify(user))
  }, auth)
  const live = [
    alarm(101, 'Boiler High'),
    alarm(102, 'Tank High', 'Warning', { areas: [2] }),
    alarm(103, 'Boiler Info', 'Info'),
    alarm(104, 'Boiler Cleared', 'Critical', { state: 'RtnUnack', endTs: Date.now() }),
    alarm(105, 'Boiler Already Ack', 'Critical', { state: 'Acked', ackTs: Date.now(), ackUser: 'other' }),
    alarm(106, 'Boiler Suppressed', 'Critical', { suppressed: true })
  ]
  const fail = new Set<number>(), acknowledgments: number[] = []
  let legacyCalls = 0
  await page.route(url => url.pathname === '/api/alarms', route => {
    const query = new URL(route.request().url()).searchParams
    if (query.get('live') === 'true') return route.fulfill({ json: live })
    const hours = Math.round((Number(query.get('to')) - Number(query.get('from'))) / 3600000)
    return route.fulfill({ json: [
      alarm(901, `History ${hours}h`, 'Critical', { endTs: Date.now(), ackTs: Date.now(), state: 'Normal' }),
      alarm(902, `History Warning ${hours}h`, 'Warning', { endTs: Date.now(), ackTs: Date.now(), state: 'Normal' })
    ] })
  })
  await page.route(url => url.pathname === '/api/alarm-shelves', route => route.fulfill({ json: [] }))
  await page.route(url => url.pathname === '/api/alarms/summary', route => route.fulfill({ json: {
    unack: 90, ackableUnack: 80, acked: 1, rtnUnack: 1, shelved: 0, suppressed: 1, outOfServiceRules: 0,
    flood: { inFlood: false, count: 0, threshold: 10 }
  } }))
  await page.route(url => /^\/api\/alarms\/\d+\/ack$/.test(url.pathname), route => {
    const id = Number(new URL(route.request().url()).pathname.split('/')[3])
    acknowledgments.push(id)
    if (fail.has(id)) return route.fulfill({ status: 500, json: { message: `无法确认 #${id}` } })
    const item = live.find(alarm => alarm.id === id)
    if (item) { item.ackTs = Date.now(); item.ackUser = 'admin'; item.state = 'Acked' }
    return route.fulfill({ json: item ?? {} })
  })
  await page.route(url => url.pathname === '/api/annunciators/ack-visible', route => {
    legacyCalls++
    return route.fulfill({ status: 500, json: { message: 'Unscoped bulk acknowledgment must not be called' } })
  })
  await page.goto('/alarms')
  await expect(rows(page)).toHaveCount(4)
  return { live, fail, acknowledgments, legacyCalls: () => legacyCalls }
}

async function confirm(page: Page, name: '确认可见' | '批量确认'): Promise<void> {
  await page.locator('.el-message-box').getByRole('button', { name, exact: true }).click()
}

test('acknowledge visible only affects the eligible loaded rows matching the current filters', async ({ page, request }) => {
  const state = await prepare(page, request)
  await expect(page.locator('[data-test="ack-visible"]')).toContainText('(3)')
  await page.locator('[data-test="alarm-keyword"]').fill('BOILER HIGH')
  await expect(rows(page)).toHaveCount(1)
  await expect(page.locator('[data-test="ack-visible"]')).toContainText('(1)')
  await page.locator('[data-test="ack-visible"]').click()
  await expect(page.locator('.el-message-box')).toContainText('可确认的 1 条报警')
  await expect(page.locator('.el-message-box')).toContainText('不包含隐藏或未加载')
  await confirm(page, '确认可见')
  await expect(page.locator('[data-test="alarm-batch-result"]')).toContainText('成功 1 条，失败 0 条')
  expect(state.acknowledgments).toEqual([101])
  expect(state.legacyCalls()).toBe(0)
  expect(state.live.filter(item => [102, 103, 104, 106].includes(item.id)).every(item => item.ackTs === null)).toBe(true)
  await expect(page.locator('[data-test="ack-visible"]')).toBeDisabled()
})

test('changing filters clears selection and acknowledged rows cannot be selected', async ({ page, request }) => {
  await prepare(page, request)
  await row(page, 'Boiler High').locator('.el-checkbox').click()
  await expect(page.locator('[data-test="batch-ack"]')).toContainText('(1)')
  await page.locator('[data-test="level-filter"]').click()
  await page.getByRole('option', { name: '警告', exact: true }).click()
  await expect(rows(page)).toHaveCount(1)
  await expect(page.locator('[data-test="batch-ack"]')).toBeDisabled()
  await page.locator('[data-test="alarm-reset-filters"]').click()
  await expect(row(page, 'Boiler High').getByRole('checkbox')).not.toBeChecked()
  await expect(row(page, 'Boiler Already Ack').getByRole('checkbox')).toBeDisabled()
  await row(page, 'Boiler High').locator('.el-checkbox').click()
  await row(page, 'Tank High').locator('.el-checkbox').click()
  await page.locator('[data-test="alarm-clear-selection"]').click()
  await expect(page.locator('[data-test="batch-ack"]')).toBeDisabled()
  await expect(row(page, 'Tank High').getByRole('checkbox')).not.toBeChecked()
})

test('acknowledgment eligibility respects alarm areas and excludes already acknowledged rows', async ({ page, request }) => {
  const state = await prepare(page, request, true)
  await expect(row(page, 'Tank High').getByRole('checkbox')).toBeDisabled()
  await expect(page.locator('[data-test="ack-visible"]')).toContainText('(2)')
  await page.locator('[data-test="ack-visible"]').click()
  await confirm(page, '确认可见')
  await expect(page.locator('[data-test="alarm-batch-result"]')).toContainText('成功 2 条')
  expect(state.acknowledgments).toEqual([101, 103])
})

test('partial failures stay on the page and retry only targets eligible failures in the current list', async ({ page, request }) => {
  const state = await prepare(page, request)
  state.fail.add(102)
  await page.locator('[data-test="alarm-keyword"]').fill('High')
  await page.locator('[data-test="ack-visible"]').click()
  await confirm(page, '确认可见')
  const result = page.locator('[data-test="alarm-batch-result"]')
  await expect(result).toContainText('成功 1 条，失败 1 条')
  await expect(result).toContainText('无法确认 #102')
  const retry = page.locator('[data-test="alarm-retry-failed"]')
  await expect(retry).toContainText('1 条')
  await expect(retry).toBeEnabled()
  await page.screenshot({ path: test.info().outputPath('alarm-partial-failure.png'), animations: 'disabled' })
  await page.locator('[data-test="alarm-keyword"]').fill('Boiler High')
  await expect(retry).toBeDisabled()
  await expect(retry).toContainText('0 条')
  await page.locator('[data-test="alarm-keyword"]').fill('High')
  state.fail.clear()
  await retry.click()
  await expect(page.locator('.el-message-box')).toContainText('选中的 1 条报警')
  await confirm(page, '批量确认')
  await expect(result).toContainText('成功 1 条，失败 0 条')
  expect(state.acknowledgments).toEqual([101, 102, 102])
})

test('confirmation freezes the target IDs and skips rows acknowledged in the meantime', async ({ page, request }) => {
  await page.clock.install()
  const state = await prepare(page, request)
  await page.locator('[data-test="alarm-keyword"]').fill('High')
  await page.locator('[data-test="ack-visible"]').click()
  await expect(page.locator('.el-message-box')).toContainText('可确认的 2 条报警')
  const item = state.live.find(item => item.id === 101)!
  item.ackTs = Date.now(); item.state = 'Acked'
  state.live.push(alarm(107, 'Boiler High New'))
  await page.clock.runFor(31_000)
  await expect(row(page, 'Boiler High New')).toBeVisible()
  await confirm(page, '确认可见')
  await expect(page.locator('[data-test="alarm-batch-result"]')).toContainText('成功 1 条，失败 0 条，状态已变化跳过 1 条')
  expect(state.acknowledgments).toEqual([102])
})

test('a pending batch disables another bulk acknowledgment and writes each target once', async ({ page, request }) => {
  const state = await prepare(page, request)
  await page.locator('[data-test="alarm-keyword"]').fill('High')
  let release!: () => void
  const gate = new Promise<void>(resolve => { release = resolve })
  await page.route(url => url.pathname === '/api/alarms/101/ack', async route => { await gate; await route.fallback() })
  const pending = page.waitForRequest(request => new URL(request.url()).pathname === '/api/alarms/101/ack')
  await page.locator('[data-test="ack-visible"]').click()
  await confirm(page, '确认可见')
  await pending
  try {
    await expect(page.locator('[data-test="ack-visible"]')).toBeDisabled()
    await expect(page.locator('[data-test="batch-ack"]')).toHaveClass(/is-loading/)
    await expect(row(page, 'Boiler High').locator('[data-test="ack"]')).toBeDisabled()
    await page.locator('[data-test="ack-visible"]').dispatchEvent('click')
    release()
    await expect(page.locator('[data-test="alarm-batch-result"]')).toContainText('成功 2 条，失败 0 条')
    expect(state.acknowledgments).toEqual([101, 102])
  } finally { release() }
})

test('late live and history responses cannot overwrite the other tab', async ({ page, request }) => {
  await prepare(page, request)
  let releaseLive!: () => void, releaseHistory!: () => void
  const liveGate = new Promise<void>(resolve => { releaseLive = resolve })
  const historyGate = new Promise<void>(resolve => { releaseHistory = resolve })
  await page.route(url => url.pathname === '/api/alarms' && url.searchParams.get('live') === 'true', async route => { await liveGate; await route.fallback() })
  const livePending = page.waitForRequest(request => new URL(request.url()).pathname === '/api/alarms' && new URL(request.url()).searchParams.get('live') === 'true')
  await page.getByRole('button', { name: '刷新', exact: true }).click()
  await livePending
  try {
    await page.getByRole('tab').last().click()
    await expect(row(page, 'History 24h')).toBeVisible()
    const liveResponse = page.waitForResponse(response => new URL(response.url()).pathname === '/api/alarms' && new URL(response.url()).searchParams.get('live') === 'true')
    releaseLive()
    await (await liveResponse).finished()
    await page.evaluate(() => new Promise(requestAnimationFrame))
    await expect(row(page, 'History 24h')).toBeVisible()

    await page.route(url => url.pathname === '/api/alarms' && !url.searchParams.has('live'), async route => { await historyGate; await route.fallback() })
    const historyPending = page.waitForRequest(request => new URL(request.url()).pathname === '/api/alarms' && !new URL(request.url()).searchParams.has('live'))
    await page.getByRole('button', { name: '查询', exact: true }).click()
    await historyPending
    await page.getByRole('tab').first().click()
    await expect(row(page, 'Boiler High')).toBeVisible()
    const historyResponse = page.waitForResponse(response => new URL(response.url()).pathname === '/api/alarms' && !new URL(response.url()).searchParams.has('live'))
    releaseHistory()
    await (await historyResponse).finished()
    await page.evaluate(() => new Promise(requestAnimationFrame))
    await expect(row(page, 'Boiler High')).toBeVisible()
    await expect(row(page, 'History 24h')).toHaveCount(0)
  } finally { releaseLive(); releaseHistory() }
})

test('new history ranges win over older requests and support severity and keyword filters', async ({ page, request }) => {
  await prepare(page, request)
  await page.getByRole('tab').last().click()
  await expect(row(page, 'History 24h')).toBeVisible()
  let release!: () => void
  const gate = new Promise<void>(resolve => { release = resolve })
  const isHour = (url: URL) => url.pathname === '/api/alarms' && Number(url.searchParams.get('to')) - Number(url.searchParams.get('from')) < 3601000
  await page.route(isHour, async route => { await gate; await route.fallback() })
  const pending = page.waitForRequest(request => isHour(new URL(request.url())))
  const choose = async (label: string) => {
    await page.locator('[data-test="range-preset"]').click()
    await page.getByRole('option', { name: label, exact: true }).click()
  }
  await choose('最近 1 小时')
  await pending
  try {
    await choose('最近 8 小时')
    await expect(row(page, 'History 8h')).toBeVisible()
    const range = await page.locator('[data-test="alarm-history-range"]').textContent()
    const response = page.waitForResponse(response => isHour(new URL(response.url())))
    release()
    await (await response).finished()
    await page.evaluate(() => new Promise(requestAnimationFrame))
    await expect(row(page, 'History 8h')).toBeVisible()
    await expect(page.locator('[data-test="alarm-history-range"]')).toHaveText(range!)
    await page.locator('[data-test="level-filter"]').click()
    await page.getByRole('option', { name: '严重', exact: true }).click()
    await page.locator('[data-test="alarm-keyword"]').fill('HISTORY 8H')
    await expect(rows(page)).toHaveCount(1)
    await page.locator('[data-test="alarm-reset-filters"]').click()
    await expect(rows(page)).toHaveCount(2)
    await expect(page.locator('[data-test="alarm-history-range"]')).toHaveText(range!)
  } finally { release() }
})
