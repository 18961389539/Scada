import { test, expect, type Locator, type Page } from './fixtures'
import type { AlarmEvent, AlarmShelve, AlarmSummary } from '../src/types'
import type { DensityMode, ThemeMode } from '../src/theme'

const now = new Date('2026-10-28T12:00:00Z').getTime()
const tableRows = (page: Page) => page.locator('.alarm-panel .el-table__body-wrapper .el-table__row')

async function prepare(page: Page, options: { theme?: ThemeMode; density?: DensityMode; english?: boolean; readOnly?: boolean } = {}) {
  const response = await page.request.post('/api/auth/login', { data: { userName: 'admin', password: 'admin123' } })
  expect(response.ok()).toBeTruthy()
  const auth = await response.json()
  if (options.readOnly) auth.user.access = { global: ['View'], areas: [] }
  await page.clock.setFixedTime(now)
  await page.addInitScript(({ auth, options }) => {
    localStorage.setItem('scada_token', auth.token)
    localStorage.setItem('scada_user', JSON.stringify(auth.user))
    localStorage.setItem('scada_theme', options.theme ?? 'light')
    localStorage.setItem('scada_density', options.density ?? 'comfortable')
    localStorage.setItem('scada_locale', options.english ? 'en-US' : 'zh-CN')
  }, { auth, options })

  const levels = ['Critical', 'Warning', 'Info'] as const
  const items: AlarmEvent[] = Array.from({ length: 140 }, (_, i) => ({
    id: i + 1, ruleId: i + 1, tagId: 1, deviceId: 1, areas: [1], level: levels[i % 3]!,
    message: i === 0 ? '出口温度过高' : i === 19 ? '长报警内容与设备来源需要完整可读且不能覆盖相邻字段'.repeat(8) : `报警记录 ${i + 1}`,
    value: 123.45, state: 'Unack', startTs: now - (i + 1) * 60000,
    endTs: null, ackTs: null, ackUser: null, firstOut: i === 0, chattering: i === 1,
    shelved: i === 5, suppressed: i === 6
  }))
  Object.assign(items[3]!, { state: 'RtnUnack', endTs: now - 1000 })
  Object.assign(items[4]!, { state: 'Acked', ackTs: now - 1000, ackUser: 'operator' })
  const shelves: AlarmShelve[] = [{ id: 1, ruleId: 6, tagId: 1, reason: '设备检修，等待现场复核', user: 'operator', startTs: now - 60000, untilTs: now + 3600000 }]
  const active = (alarm: AlarmEvent) => alarm.endTs === null && !alarm.shelved && !alarm.suppressed
  const summary: AlarmSummary = {
    unack: items.filter(alarm => active(alarm) && !alarm.ackTs).length,
    acked: 1, rtnUnack: 1, shelved: 1, suppressed: 1, chattering: 1, shelvedRules: 1, outOfServiceRules: 1,
    flood: { inFlood: true, count: 48, threshold: 10, windowStartTs: now - 600000, windowEndTs: now },
    floodGuard: {
      autoSuppress: true, firstOut: true, suppressMaxLevel: 'Warning', anyInFlood: true,
      areas: [{ areaId: 1, areaName: '公用工程区', count: 48, threshold: 10, exitThreshold: 5, inFlood: true,
        windowStartTs: now - 600000, windowEndTs: now, floodStartTs: now - 600000, peak: 48, firstOutEventId: 1, suppressedCount: 1 }]
    }
  }
  await page.route(url => url.pathname === '/api/devices', route => route.fulfill({ json: [{ id: 1, name: '锅炉 1#', areaId: 1 }] }))
  await page.route(url => url.pathname === '/api/tags', route => route.fulfill({ json: [{ id: 1, deviceId: 1, name: '出口温度', dataType: 'Float' }] }))
  await page.route(url => url.pathname === '/api/alarms/summary', route => route.fulfill({ json: summary }))
  await page.route(url => url.pathname === '/api/alarm-shelves', route => route.fulfill({ json: shelves }))
  await page.route(url => url.pathname === '/api/alarms/page', route => {
    const query = new URL(route.request().url()).searchParams
    const state = query.get('state')
    const keyword = (query.get('keyword') ?? '').toLowerCase()
    const filtered = items.filter(alarm =>
      (query.get('live') !== 'true' || (state === 'unack' ? active(alarm) && !alarm.ackTs
        : state === 'acked' ? active(alarm) && !!alarm.ackTs
        : state === 'rtnUnack' ? alarm.endTs !== null && !alarm.ackTs
        : state === 'shelved' ? alarm.shelved : state === 'suppressed' ? alarm.suppressed : active(alarm)))
      && (!query.get('level') || alarm.level === query.get('level'))
      && alarm.message.toLowerCase().includes(keyword))
    const offset = Number(query.get('offset') ?? 0), limit = Number(query.get('limit') ?? 100)
    return route.fulfill({ json: { items: filtered.slice(offset, offset + limit), total: filtered.length, offset, limit, capped: false } })
  })
  return { items, shelves, summary }
}

async function contrast(locator: Locator): Promise<number> {
  await expect.poll(() => locator.evaluate(element => {
    let opacity = 1
    for (let parent: Element | null = element; parent; parent = parent.parentElement) opacity *= Number(getComputedStyle(parent).opacity)
    return opacity
  })).toBe(1)
  return locator.evaluate(element => {
    type Color = [number, number, number, number]
    function parse(value: string): Color {
      const parts = value.match(/[\d.]+/g)?.map(Number)
      const factor = value.startsWith('color(srgb ') ? 255 : 1
      if ((!value.startsWith('rgb') && factor !== 255) || !parts || parts.length < 3) throw new Error(`Unsupported computed color: ${value}`)
      return [parts[0]! * factor, parts[1]! * factor, parts[2]! * factor, parts[3] ?? 1]
    }
    function blend(foreground: Color, background: Color): Color {
      return [foreground[0] * foreground[3] + background[0] * (1 - foreground[3]),
        foreground[1] * foreground[3] + background[1] * (1 - foreground[3]),
        foreground[2] * foreground[3] + background[2] * (1 - foreground[3]), 1]
    }
    function luminance(color: Color): number {
      const channels = color.slice(0, 3).map(value => value / 255).map(value => value <= 0.04045 ? value / 12.92 : ((value + 0.055) / 1.055) ** 2.4)
      return channels[0]! * 0.2126 + channels[1]! * 0.7152 + channels[2]! * 0.0722
    }
    const ancestors: Element[] = []
    for (let parent: Element | null = element; parent; parent = parent.parentElement) ancestors.unshift(parent)
    const background = ancestors.reduce<Color>((color, parent) => blend(parse(getComputedStyle(parent).backgroundColor), color), [255, 255, 255, 1])
    const foreground = blend(parse(getComputedStyle(element).color), background)
    const a = luminance(foreground), b = luminance(background)
    return (Math.max(a, b) + 0.05) / (Math.min(a, b) + 0.05)
  })
}

async function fitsViewport(locator: Locator): Promise<void> {
  await expect(locator).toBeVisible()
  const bounds = await locator.evaluate(element => {
    const rect = element.getBoundingClientRect()
    return { left: rect.left, right: rect.right, top: rect.top, bottom: rect.bottom, width: innerWidth, height: innerHeight }
  })
  expect(bounds.left).toBeGreaterThanOrEqual(-1)
  expect(bounds.right).toBeLessThanOrEqual(bounds.width + 1)
  expect(bounds.top).toBeGreaterThanOrEqual(-1)
  expect(bounds.bottom).toBeLessThanOrEqual(bounds.height + 1)
}

test('alarm UI baseline: status filters have native keyboard interaction and an announced selection', async ({ page }) => {
  await prepare(page)
  await page.goto('/alarms')
  const unack = page.locator('[data-test="filter-unack"]'), returned = page.locator('[data-test="filter-rtn"]')
  await expect(page.locator('[data-test="filter-active"]')).toHaveAttribute('aria-pressed', 'true')
  await expect(unack).toHaveJSProperty('tagName', 'BUTTON')
  await expect(unack).toHaveAttribute('aria-pressed', 'false')
  await unack.focus()
  await page.keyboard.press('Enter')
  await expect(unack).toHaveAttribute('aria-pressed', 'true')
  await expect(unack).toBeFocused()
  await page.keyboard.press('Tab')
  await expect(returned).toBeFocused()
  await page.keyboard.press('Space')
  await expect(returned).toHaveAttribute('aria-pressed', 'true')
  await expect(unack).toHaveAttribute('aria-pressed', 'false')
  await expect(tableRows(page)).toHaveCount(1)
  await expect(page.getByRole('group', { name: '实时报警状态' }).getByRole('button')).toHaveCount(7)
  const live = page.locator('[data-test="filter-live"]')
  await expect(live).toHaveJSProperty('tagName', 'BUTTON')
  await live.focus()
  await page.keyboard.press('Enter')
  await expect(live).toHaveAttribute('aria-pressed', 'true')
  await expect(returned).toHaveAttribute('aria-pressed', 'false')
})

test('changing alarm tabs starts the new table at its first column', async ({ page }) => {
  await page.setViewportSize({ width: 1366, height: 768 })
  await prepare(page)
  await page.goto('/alarms')
  await expect(tableRows(page)).toHaveCount(100)
  const scroller = page.locator('.alarm-panel .el-table__body-wrapper .el-scrollbar__wrap')
  await scroller.evaluate(element => { element.scrollLeft = element.scrollWidth })
  expect(await scroller.evaluate(element => element.scrollLeft)).toBeGreaterThan(0)
  await page.getByRole('tab', { name: '历史报警', exact: true }).click()
  await expect(tableRows(page)).toHaveCount(100)
  await expect.poll(() => scroller.evaluate(element => element.scrollLeft)).toBe(0)
  await scroller.evaluate(element => { element.scrollLeft = element.scrollWidth })
  await page.getByRole('tab', { name: '实时报警', exact: true }).click()
  await expect(tableRows(page)).toHaveCount(100)
  await expect.poll(() => scroller.evaluate(element => element.scrollLeft)).toBe(0)
})

for (const theme of ['light', 'dark'] as const) {
  test(`alarm UI baseline: ${theme} status text and active action contrast is at least 4.5:1`, async ({ page }) => {
    await prepare(page, { theme })
    await page.goto('/alarms')
    const selectors = [
      '[data-test="flood-banner"] .el-alert__title',
      '[data-test="flood-banner"] .flood-detail',
      '.state-cell .el-tag--warning',
      '.state-cell .el-tag--danger',
      '[data-test="first-out"]',
      '[data-test="filter-active"]',
      '[data-test="filter-unack"] .alarm-filter-count',
      '[data-test="filter-rtn"] .alarm-filter-count',
      '[data-test="silence-ann"]',
      '[data-test="alarm-result-count"]'
    ]
    for (const selector of selectors) {
      const text = page.locator(selector).first()
      await expect(text).toBeVisible()
      expect(await contrast(text), `${theme}: ${selector}`).toBeGreaterThanOrEqual(4.5)
    }
    const row = page.getByRole('row').filter({ hasText: '出口温度过高' })
    await row.locator('.el-checkbox').click()
    const primary = page.locator('[data-test="batch-ack"]')
    await expect(primary).toBeEnabled()
    await primary.evaluate(element => element.getAnimations().forEach(animation => animation.finish()))
    expect(await contrast(primary), `${theme}: enabled batch action`).toBeGreaterThanOrEqual(4.5)
    await primary.hover()
    await primary.evaluate(element => element.getAnimations().forEach(animation => animation.finish()))
    expect(await contrast(primary), `${theme}: hovered batch action`).toBeGreaterThanOrEqual(4.5)
    await row.getByRole('button', { name: '暂挂', exact: true }).click()
    const dialog = page.getByRole('dialog', { name: '暂挂报警', exact: true })
    await dialog.locator('[data-test="shelve-reason"]').fill('检修界面验收，不提交')
    const submit = dialog.locator('[data-test="shelve-submit"]')
    await expect(submit).toBeEnabled()
    await submit.evaluate(element => element.getAnimations().forEach(animation => animation.finish()))
    expect(await contrast(submit), `${theme}: enabled shelving action`).toBeGreaterThanOrEqual(4.5)
    await submit.hover()
    await submit.evaluate(element => element.getAnimations().forEach(animation => animation.finish()))
    expect(await contrast(submit), `${theme}: hovered shelving action`).toBeGreaterThanOrEqual(4.5)
    await dialog.getByRole('button', { name: '取消', exact: true }).click()
  })
}

for (const viewport of [{ width: 1366, height: 768 }, { width: 1920, height: 1080 }]) {
  for (const theme of ['light', 'dark'] as const) {
    for (const density of ['comfortable', 'compact'] as const) {
      test(`alarm workbench fits ${viewport.width}x${viewport.height} ${theme} ${density} across all three tabs`, async ({ page }, testInfo) => {
        await page.setViewportSize(viewport)
        const errors: string[] = []
        page.on('pageerror', error => errors.push(error.message))
        page.on('console', message => { if (message.type() === 'error') errors.push(message.text()) })
        await prepare(page, { theme, density })
        await page.goto('/alarms')
        await expect(tableRows(page)).toHaveCount(100)
        const longRow = tableRows(page).filter({ hasText: '长报警内容与设备来源需要完整可读且不能覆盖相邻字段' })
        const longAction = longRow.locator('[data-test="ack"]')
        await longAction.focus()
        await fitsViewport(longAction)
        const content = await longRow.evaluate(row => {
          const message = row.querySelector('.alarm-message'), source = row.querySelector('.alarm-source')
          if (!message || !source) throw new Error('Alarm content and source must remain available.')
          return {
            overflow: message.scrollWidth - message.clientWidth,
            messageBottom: message.getBoundingClientRect().bottom,
            sourceBottom: source.getBoundingClientRect().bottom,
            rowBottom: row.getBoundingClientRect().bottom
          }
        })
        expect(content.overflow).toBeLessThanOrEqual(1)
        expect(content.messageBottom).toBeLessThanOrEqual(content.rowBottom + 1)
        expect(content.sourceBottom).toBeLessThanOrEqual(content.rowBottom + 1)
        await page.locator('.alarm-panel .el-table__body-wrapper .el-scrollbar__wrap').evaluate(element => { element.scrollTop = 0; element.scrollLeft = 0 })
        for (const tab of ['实时报警', '历史报警', '暂挂 (1)']) {
          await page.getByRole('tab', { name: tab, exact: true }).click()
          const region = page.locator('[data-test="alarm-table-region"]')
          await expect(region).toHaveAttribute('aria-busy', 'false')
          const overflow = await page.evaluate(() => ({
            document: document.documentElement.scrollWidth - document.documentElement.clientWidth,
            main: document.querySelector('main')!.scrollWidth - document.querySelector('main')!.clientWidth
          }))
          expect(overflow.document).toBeLessThanOrEqual(1)
          expect(overflow.main).toBeLessThanOrEqual(1)
          await fitsViewport(region)
          if (tab !== '暂挂 (1)') await fitsViewport(page.locator('.alarm-pager'))
          if (tab === '历史报警') {
            for (const input of await page.locator('.alarm-history-range input').all()) {
              const text = await input.evaluate(input => {
                if (!(input instanceof HTMLInputElement)) throw new Error('A date input is required.')
                const context = document.createElement('canvas').getContext('2d')
                if (!context) throw new Error('Text measurement is unavailable.')
                const style = getComputedStyle(input)
                context.font = style.font
                return {
                  width: context.measureText(input.value).width,
                  available: input.clientWidth - parseFloat(style.paddingLeft) - parseFloat(style.paddingRight)
                }
              })
              expect(text.width).toBeLessThanOrEqual(text.available + 1)
            }
            await expect(page.getByRole('combobox', { name: '每页条数', exact: true })).toBeVisible()
            const pageSizeGap = await page.locator('[data-test="alarm-history-pagination"]').evaluate(pager => {
              const total = pager.querySelector('.el-pagination__total'), size = pager.querySelector('.alarm-page-size')
              if (!total || !size) throw new Error('History total and page-size control are required.')
              return size.getBoundingClientRect().left - total.getBoundingClientRect().right
            })
            expect(pageSizeGap).toBeGreaterThanOrEqual(8)
          }
          const scroller = region.locator('.el-table__body-wrapper .el-scrollbar__wrap')
          await expect(scroller).toHaveAttribute('tabindex', '0')
          const wide = await scroller.evaluate(element => element.scrollWidth > element.clientWidth + 1)
          if (wide) {
            await expect(region.locator('.el-scrollbar__bar.is-horizontal')).toBeVisible()
            await scroller.focus()
            await page.keyboard.press('ArrowRight')
            await expect.poll(() => scroller.evaluate(element => element.scrollLeft)).toBeGreaterThan(0)
            await scroller.evaluate(element => { element.scrollLeft = 0 })
          }
          if (tab === '实时报警') await expect(region.locator('.state-cell .el-tag').nth(1)).toHaveText('严重')
          await expect.poll(() => region.locator('.el-tag').evaluateAll(tags => tags.every(tag => Number(getComputedStyle(tag).opacity) === 1))).toBe(true)
          await page.screenshot({ path: testInfo.outputPath(`${tab.startsWith('暂挂') ? 'shelved' : tab === '实时报警' ? 'live' : 'history'}.png`), animations: 'disabled' })
        }
        expect(errors).toEqual([])
      })
    }
  }
}

test('shelved alarms distinguish loading and failure from an empty list and retry in place', async ({ page }) => {
  const { shelves } = await prepare(page)
  await page.goto('/alarms')
  await expect(tableRows(page)).toHaveCount(100)
  let release!: () => void, failed = true
  const pending = new Promise<void>(resolve => { release = resolve })
  await page.route(url => url.pathname === '/api/alarm-shelves', async route => {
    if (failed) { await pending; return route.fulfill({ status: 500, json: { message: '暂挂列表读取失败' } }) }
    return route.fulfill({ json: shelves })
  })
  try {
    await page.getByRole('tab', { name: '暂挂 (1)', exact: true }).click()
    const region = page.locator('[data-test="alarm-table-region"]')
    await expect(region).toHaveAttribute('aria-busy', 'true')
    await expect(region.locator('.el-loading-mask')).toBeVisible()
    release()
    await expect(page.locator('[data-test="alarms-load-error"]')).toContainText('暂挂列表读取失败')
    failed = false
    await page.locator('[data-test="alarms-list-retry"]').click()
    await expect(page.locator('[data-test="alarms-load-error"]')).toBeHidden()
    await expect(region).toHaveAttribute('aria-busy', 'false')
    await expect(tableRows(page)).toContainText(['设备检修，等待现场复核'])
    await expect(page.locator('.alarm-scroll-hint')).toBeVisible()
    shelves.splice(0)
    await page.getByRole('tab', { name: '实时报警', exact: true }).click()
    await expect(tableRows(page)).toHaveCount(100)
    await page.getByRole('tab', { name: /^暂挂/ }).click()
    await expect(region).toHaveAttribute('aria-busy', 'false')
    await expect(region).toContainText('没有暂挂中的报警')
    await expect(page.locator('.alarm-scroll-hint')).toBeHidden()
  } finally { release() }
})

test('alarm timeline retries its own failed record and returns keyboard focus when closed', async ({ page }) => {
  const { items } = await prepare(page)
  let calls = 0
  await page.route(url => url.pathname === '/api/alarms/1/timeline', route => {
    calls++
    return calls === 1 ? route.fulfill({ status: 500, json: { message: '处理记录暂不可用' } })
      : route.fulfill({ json: { alarm: items[0], activities: [{ id: 1, alarmEventId: 1, ts: now, action: 'Note', user: 'operator', note: '已联系现场复核' }], notifications: [] } })
  })
  await page.goto('/alarms')
  const trigger = page.getByRole('row').filter({ hasText: '出口温度过高' }).locator('[data-test="timeline"]')
  await trigger.focus()
  await page.keyboard.press('Enter')
  const drawer = page.locator('[data-test="alarm-timeline"]')
  await expect(drawer.locator('[data-test="alarm-timeline-error"]')).toContainText('处理记录暂不可用')
  await drawer.locator('[data-test="alarm-timeline-retry"]').click()
  await expect(drawer).toContainText('已联系现场复核')
  await expect(drawer.locator('[data-test="alarm-timeline-error"]')).toBeHidden()
  expect(calls).toBe(2)
  await page.keyboard.press('Escape')
  await expect(drawer).toBeHidden()
  await expect(trigger).toBeFocused()
})

test('read-only alarm users retain filtering and records without acknowledgment controls', async ({ page }) => {
  await prepare(page, { readOnly: true })
  await page.goto('/alarms')
  await expect(tableRows(page)).toHaveCount(100)
  await expect(page.locator('[data-test="batch-ack"], [data-test="ack-visible"], [data-test="silence-ann"], [data-test="ack"]')).toHaveCount(0)
  await page.locator('[data-test="filter-rtn"]').focus()
  await page.keyboard.press('Enter')
  await expect(tableRows(page)).toHaveCount(1)
  await expect(page.locator('[data-test="timeline"]')).toHaveCount(1)
})

test('English alarm history names each date endpoint and retains clear filter controls', async ({ page }) => {
  await prepare(page, { english: true })
  await page.goto('/alarms')
  await page.getByRole('tab', { name: 'Alarm history', exact: true }).click()
  await expect(page.locator('.el-date-editor input').first()).toHaveAccessibleName('Start time')
  await expect(page.locator('.el-date-editor input').last()).toHaveAccessibleName('End time')
  await expect(page.locator('[data-test="alarm-keyword"]')).toHaveAccessibleName('Filter by content')
  await expect(page.getByRole('combobox', { name: 'Rows per page', exact: true })).toBeVisible()
})

test('editing the history range clearly distinguishes draft criteria from loaded results', async ({ page }) => {
  await prepare(page)
  await page.goto('/alarms')
  await page.getByRole('tab', { name: '历史报警', exact: true }).click()
  const context = page.locator('[data-test="alarm-history-range"]')
  const previous = await context.textContent()
  await page.locator('#alarm-history-start').fill('2026-10-28 10:00:00')
  await page.locator('#alarm-history-start').press('Enter')
  await page.keyboard.press('Escape')
  await expect(page.locator('[data-test="alarm-range-changed"]')).toBeVisible()
  await expect(context).toHaveText(previous!)
  await page.getByRole('button', { name: '查询', exact: true }).click()
  await expect(page.locator('[data-test="alarm-range-changed"]')).toBeHidden()
  await expect(context).toContainText('2026-10-28 10:00:00')
})

for (const viewport of [{ width: 768, height: 1024 }, { width: 390, height: 844 }, { width: 683, height: 384 }]) {
  test(`alarm controls remain reachable at CSS viewport ${viewport.width}x${viewport.height}`, async ({ page }) => {
    await page.setViewportSize(viewport)
    await prepare(page)
    await page.goto('/alarms')
    await expect(tableRows(page)).toHaveCount(100)
    const rootOverflow = () => page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth)
    expect(await rootOverflow()).toBeLessThanOrEqual(1)
    await page.getByRole('tab', { name: '历史报警', exact: true }).click()
    await page.locator('.el-date-editor input').first().click()
    const picker = page.locator('.el-date-range-picker:visible')
    const bounds = await picker.boundingBox()
    expect(bounds).not.toBeNull()
    expect(bounds!.x).toBeGreaterThanOrEqual(-1)
    expect(bounds!.x + bounds!.width).toBeLessThanOrEqual(viewport.width + 1)
    await page.keyboard.press('Escape')
    expect(await rootOverflow()).toBeLessThanOrEqual(1)
    await page.getByRole('tab', { name: '实时报警', exact: true }).click()
    await page.getByRole('row').filter({ hasText: '出口温度过高' }).locator('[data-test="ack"]').click()
    const dialog = page.getByRole('dialog', { name: '确认报警', exact: true })
    await expect(dialog).toBeVisible()
    await dialog.locator('[data-test="note-input"]').fill('仅验证界面，不提交报警确认')
    await dialog.getByRole('button', { name: '取消', exact: true }).click()
    await expect(dialog).toBeHidden()
  })
}
