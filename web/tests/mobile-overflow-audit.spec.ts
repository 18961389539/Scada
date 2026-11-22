import { test, expect, loginApi } from './fixtures'
import { writeFileSync } from 'node:fs'
import { collectUiAudit } from './ui-audit'
import { NAV_ITEMS } from '../src/nav/catalog'

/**
 * 手机端横向溢出取证：给每一个导航页在 390px 下测一次。
 * ui-audit-gate 只覆盖 8 个高频页，其余页面是否在小屏破版一直没有实测证据。
 * 首轮只输出清单，确认后再决定是补 @media 还是把某页标记为"桌面专用"。
 */
test('390px: every navigation page is measured for horizontal overflow', async ({ page }, testInfo) => {
  test.setTimeout(300000)
  const auth = await loginApi(page.request)
  await page.addInitScript(({ token, user }) => {
    localStorage.setItem('scada_token', token)
    localStorage.setItem('scada_user', JSON.stringify(user))
    localStorage.setItem('scada_theme', 'light')
    localStorage.setItem('scada_density', 'comfortable')
    localStorage.setItem('scada_locale', 'zh-CN')
  }, auth)
  await page.setViewportSize({ width: 390, height: 844 })

  const rows = []
  for (const item of NAV_ITEMS) {
    await page.goto(item.path, { waitUntil: 'domcontentloaded' })
    await expect(page.locator('main, [role="main"]').first(), item.path).toBeVisible()
    await page.waitForTimeout(500)
    const audit = await collectUiAudit(page)
    rows.push({ path: item.path, overflow: audit.horizontalOverflow, unnamedButtons: audit.unnamedButtons.length })
  }

  const body = JSON.stringify(rows, null, 2)
  writeFileSync('test-results/mobile-overflow-390.json', body)
  await testInfo.attach('mobile-overflow-390.json', { body, contentType: 'application/json' })
  expect(rows.length).toBe(NAV_ITEMS.length)
  // 实测 37 页在 390px 下全部 0 溢出，直接落成硬门禁：新增页面或布局改动一旦破版就失败。
  const overflowing = rows.filter(row => row.overflow > 1)
  expect(overflowing, `390px 横向溢出：${overflowing.map(row => `${row.path} ${row.overflow}px`).join(' | ')}`).toEqual([])
})