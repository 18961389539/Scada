import { test, expect, type Page } from './fixtures'
import type { OeeReport } from '../src/api/oee'

function report(from: number, to: number, empty = false): OeeReport {
  const day = { from, to: Math.min(to, from + 86400000), label: '测试日期', oee: 0.8, availability: 0.9, performance: 0.95, quality: null, good: 90, total: 100 }
  return {
    kpi: { ...day, to, plannedMs: empty ? 0 : 3600000, runMs: empty ? 0 : 3000000, downMs: 0, faultMs: 0,
      good: empty ? 0 : 90, total: empty ? 0 : 100, reject: 10, scrap: 0, scrapRate: 0.1,
      mtbfHours: 1, mttrHours: 0.1, faultCount: 1, productionVsTargetPercent: 90, target: 100,
      performanceOver100: false, performanceClamped: false, downtimePareto: [], byShift: [], byProduct: [], byDay: [day] },
    trend: [day], detailFrom: from, usedRollups: false,
    thresholds: { targetOee: 0.85, targetAvailability: 0.9, targetPerformance: 0.95, targetQuality: 0.99, clampPerformance: true, topN: 10 }
  }
}

async function prepare(page: Page, empty = false) {
  const response = await page.request.post('/api/auth/login', { data: { userName: 'admin', password: 'admin123' } })
  expect(response.ok()).toBeTruthy()
  await page.addInitScript(({ token, user }) => {
    localStorage.setItem('scada_token', token)
    localStorage.setItem('scada_user', JSON.stringify(user))
    localStorage.setItem('scada_sound', 'off')
  }, await response.json())
  const catalogs: Record<string, unknown> = { '/api/areas': [], '/api/oee/lines': [], '/api/oee/products': [], '/api/oee/shifts': [] }
  await page.route(url => Object.hasOwn(catalogs, url.pathname), route => route.fulfill({ json: catalogs[new URL(route.request().url()).pathname] }))
  const queries: { from: number; to: number }[] = []
  await page.route(url => url.pathname === '/api/oee/analytics', route => {
    const query = new URL(route.request().url()).searchParams
    const from = Number(query.get('from')), to = Number(query.get('to'))
    queries.push({ from, to })
    return route.fulfill({ json: report(from, to, empty) })
  })
  await page.goto('/oee')
  await expect(page.locator('[data-test="kpi-oee"]')).toBeVisible()
  return queries
}

for (const width of [390, 1280]) {
  test(`OEE separates core and supporting metrics and explains targets at ${width}px`, async ({ page }) => {
    await page.setViewportSize({ width, height: 900 })
    await prepare(page)
    const core = page.locator('[data-test="oee-core-metrics"]')
    await expect(core.locator('.core-card')).toHaveCount(4)
    await expect(core.locator('[data-test="kpi-good"]')).toHaveCount(0)
    await expect(page.locator('[data-test="oee-supporting-metrics"] > div')).toHaveCount(6)
    await expect(core.locator('[data-test="kpi-oee"]')).toContainText('目标 85.0%')
    await expect(core.locator('[data-test="kpi-oee"] [data-test="oee-target-status"]')).toHaveText('低于目标')
    await expect(core.locator('[data-test="kpi-a"] [data-test="oee-target-status"]')).toHaveText('达标')
    await expect(core.locator('[data-test="kpi-q"] [data-test="oee-target-status"]')).toHaveText('无数据')
    await expect(core.locator('[data-test="kpi-q"] .value')).toHaveText('—')
    const overflow = await core.evaluate(el => {
      const r = el.getBoundingClientRect()
      return [...el.querySelectorAll('.value, .metric-target, .metric-status')].some(item => {
        const box = item.getBoundingClientRect()
        return box.left < r.left - 1 || box.right > r.right + 1
      })
    })
    expect(overflow).toBe(false)
    await page.screenshot({ path: test.info().outputPath(`oee-metrics-${width}.png`), fullPage: true, animations: 'disabled' })
  })
}

test('OEE no production displays neutral no-data status while preserving configured targets', async ({ page }) => {
  await prepare(page, true)
  const cards = page.locator('[data-test="oee-core-metrics"] .core-card')
  await expect(cards.locator('[data-test="oee-target-status"]')).toHaveText(['无数据', '无数据', '无数据', '无数据'])
  await expect(cards.locator('.value')).toHaveText(['—', '—', '—', '—'])
  await expect(page.locator('[data-test="oee-core-metrics"] .bad, [data-test="oee-core-metrics"] .warn')).toHaveCount(0)
  await expect(page.locator('[data-test="kpi-oee"]')).toContainText('目标 85.0%')
})

test('OEE day navigation clears the quick preset and restores the exact original range', async ({ page }) => {
  const queries = await prepare(page)
  const sevenDays = page.getByRole('button', { name: '7 天', exact: true })
  await sevenDays.click()
  await page.locator('[data-test="oee-query"]').click()
  await expect(sevenDays).toHaveClass(/el-button--primary/)
  await expect(page.locator('[data-test="oee-export-xlsx"]')).toBeEnabled()
  const previous = { ...queries.at(-1)! }
  await page.getByRole('tab', { name: '按日', exact: true }).click()
  await page.locator('[data-test="oee-view-day"]').click()
  await expect(page.locator('[data-test="oee-back-to-range"]')).toBeVisible()
  await expect(sevenDays).not.toHaveClass(/el-button--primary/)
  await expect.poll(() => queries.at(-1)?.to).toBe(previous.from + 86400000)
  await expect(page.locator('[data-test="oee-export-xlsx"]')).toBeEnabled()
  await page.locator('[data-test="oee-back-to-range"]').click()
  await expect.poll(() => queries.at(-1)).toEqual(previous)
  await expect(sevenDays).toHaveClass(/el-button--primary/)
  await expect(page.locator('[data-test="oee-back-to-range"]')).toBeHidden()
})
