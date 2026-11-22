import { test, expect, type Page } from './fixtures'
import { readFileSync, writeFileSync } from 'node:fs'
import { collectContrast } from './contrast'

/**
 * 对比度取证：核心页 × 明暗 × 桌面/手机，采集所有可见文本的前景/背景对比度。
 *
 * 首轮只输出清单（B3 取证）；清单确认后落成棘轮基线 `contrast-baseline.json`：
 * 只允许"已知项减少"，任何新增违规都会让用例失败。修掉某项后从基线删除即可。
 */
const PAGES = [
  '/overview', '/realtime', '/alarms', '/trends', '/control',
  '/config/tags', '/config/devices', '/system',
  // 自定义组件最密集的三页：控制台/画面目录/报警分析，暗色漏色风险最高。
  '/console', '/screens', '/alarm-analytics'
]
const THEMES = ['light', 'dark'] as const
const VIEWPORTS = [
  { label: 'desktop-1440', width: 1440, height: 900 },
  { label: 'phone-390', width: 390, height: 844 }
]

async function signIn(page: Page, theme: 'light' | 'dark'): Promise<void> {
  const response = await page.request.post('/api/auth/login', { data: { userName: 'admin', password: 'admin123' } })
  expect(response.ok()).toBeTruthy()
  const auth = await response.json()
  await page.addInitScript(({ token, user, theme }) => {
    localStorage.setItem('scada_token', token)
    localStorage.setItem('scada_user', JSON.stringify(user))
    localStorage.setItem('scada_theme', theme)
    localStorage.setItem('scada_density', 'comfortable')
    localStorage.setItem('scada_locale', 'zh-CN')
    localStorage.setItem('scada_sound', 'off')
  }, { token: auth.token, user: auth.user, theme })
}

for (const theme of THEMES) {
  test(`contrast audit (${theme})`, async ({ page }, testInfo) => {
    test.setTimeout(300000)
    await signIn(page, theme)
    const rows = []
    for (const viewport of VIEWPORTS) {
      await page.setViewportSize({ width: viewport.width, height: viewport.height })
      for (const path of PAGES) {
        await page.goto(path, { waitUntil: 'domcontentloaded' })
        await expect(page.locator('main, [role="main"]').first(), path).toBeVisible()
        // 等筛选/图表/表格首帧稳定，避免把过渡态的半透明文字算成违规。
        await page.waitForTimeout(700)
        rows.push({ theme, viewport: viewport.label, path, ...(await collectContrast(page)) })
      }
    }
    const body = JSON.stringify(rows, null, 2)
    writeFileSync(`test-results/contrast-audit-${theme}.json`, body)
    await testInfo.attach(`contrast-audit-${theme}.json`, { body, contentType: 'application/json' })
    expect(rows.length).toBe(PAGES.length * VIEWPORTS.length)

    // 棘轮：基线里是已确认的既有欠账，只允许减少；新增签名一律失败。
    // 签名按"缺陷族"聚合（selector + 阈值 + 前景色），不绑定具体文案——否则每新增一个页面名都会被当成新违规。
    const signatures = [...new Set(rows.flatMap(row => row.violations.map(item => `${item.selector}|${item.required}|${item.color}`)))]
    const baseline = new Set<string>(JSON.parse(readFileSync('tests/contrast-baseline.json', 'utf8')).allowed as string[])
    const added = signatures.filter(signature => !baseline.has(signature))
    const fixed = [...baseline].filter(signature => !signatures.includes(signature))
    const summary = {
      theme,
      checked: rows.reduce((sum, row) => sum + row.checked, 0),
      undetermined: rows.reduce((sum, row) => sum + row.undetermined, 0),
      violations: rows.reduce((sum, row) => sum + row.violations.length, 0),
      signatures, added, fixed
    }
    await testInfo.attach(`contrast-summary-${theme}.json`, { body: JSON.stringify(summary, null, 2), contentType: 'application/json' })
    expect(added, `对比度新增违规（基线只允许减少）：${added.join(' | ')}`).toEqual([])
  })
}

test('contrast collector detects low-contrast text and ignores disabled controls', async ({ page }) => {
  await page.goto('/login')
  await page.evaluate(() => {
    const host = document.createElement('div')
    host.id = 'contrast-probe'
    host.style.cssText = 'position:fixed;left:0;top:0;width:240px;height:64px;background:#ffffff;padding:4px'
    host.innerHTML = '<span id="probe-bad" style="color:#a8abb2;font-size:14px">探针低对比</span>'
      + '<button id="probe-disabled" disabled style="color:#eeeeee;background:#ffffff;font-size:14px">禁用不计</button>'
    document.body.appendChild(host)
  })
  const report = await collectContrast(page)
  const probes = report.violations.filter(item => item.text.includes('探针低对比'))
  expect(probes).toHaveLength(1)
  expect(probes[0]!.ratio).toBeLessThan(4.5)
  // 禁用控件属于 WCAG 豁免，采集器必须放过，否则基线会被"禁用态"噪声淹没。
  expect(report.violations.some(item => item.text.includes('禁用不计'))).toBe(false)
})