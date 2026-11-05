import { test, expect } from './fixtures'

test('page help button opens page-specific guidance on main pages', async ({ page, request }) => {
  const response = await request.post('/api/auth/login', { data: { userName: 'admin', password: 'admin123' } })
  expect(response.ok()).toBeTruthy()
  const auth = (await response.json()) as { token: string; user: unknown }
  await page.addInitScript(({ token, user }) => {
    localStorage.setItem('scada_token', token)
    localStorage.setItem('scada_user', JSON.stringify(user))
    localStorage.setItem('scada_sound', 'off')
  }, auth)

  const cases: [string, string, string][] = [
    ['/overview', '总览', '总览就像工厂的仪表盘'],
    ['/config/tags', '测点配置', '测点是采集系统认识设备的最小单位'],
    ['/alarms', '报警', '报警就像设备发出的'],
    ['/console', '运行工作台', '运行工作台是把多个工艺画面拼在一起']
  ]

  for (const [path, title, bodyText] of cases) {
    await page.goto(path)
    const button = page.locator('[data-test="page-help"]')
    await expect(button, path).toBeVisible()
    await button.click()
    const drawer = page.locator('[data-test="page-help-drawer"]')
    await expect(drawer, path).toBeVisible()
    await expect(drawer).toContainText(title)
    await expect(drawer).toContainText(bodyText)
    await expect(drawer.locator('[data-test="page-help-toc"]')).toContainText('怎么用')
    await expect(drawer.locator('[data-test="page-help-toc"]')).toContainText('小提醒与常见问题')
    await expect(drawer.locator('[data-test="page-help-start"]')).toContainText('先看这里')
    await expect(drawer.locator('.page-help-start-detail').first()).toBeVisible()
    await expect(drawer.locator('[data-test="page-help-section"]').filter({ hasText: '页面上有什么' }).locator('[data-test="page-help-section-body"]')).toBeHidden()
    await page.locator('[data-test="page-help-close"]').click()
    await expect(drawer).toBeHidden()
  }
})

test('page help search filters the current page and highlights matches', async ({ page, request }) => {
  const response = await request.post('/api/auth/login', { data: { userName: 'admin', password: 'admin123' } })
  expect(response.ok()).toBeTruthy()
  const auth = (await response.json()) as { token: string; user: unknown }
  await page.addInitScript(({ token, user }) => {
    localStorage.setItem('scada_token', token)
    localStorage.setItem('scada_user', JSON.stringify(user))
    localStorage.setItem('scada_sound', 'off')
  }, auth)

  await page.goto('/overview')
  await page.locator('[data-test="page-help"]').click()
  const drawer = page.locator('[data-test="page-help-drawer"]')
  await expect(drawer).toBeVisible()

  const start = drawer.locator('[data-test="page-help-start-item"]').first()
  await expect(start).toContainText('交接班或巡检时先看更新条')
  await expect(start).toContainText('数据已过期')
  await start.click()
  await expect(drawer.locator('.page-help-step.is-focused')).toContainText('交接班或巡检时先看更新条')

  const input = drawer.locator('input[data-test="page-help-search-input"]')
  await expect(input).toBeHidden()
  await drawer.locator('[data-test="page-help-search"]').click()
  await expect(input).toBeVisible()
  await input.fill('报警洪水')
  await expect(drawer.locator('[data-test="page-help-search-count"]')).toHaveText('匹配 1 条')
  await expect(drawer.locator('mark').first()).toHaveText('报警洪水')
  await expect(drawer).not.toContainText('配置引导')

  await input.fill('')
  await expect(drawer).toContainText('配置引导')
  const inventory = drawer.locator('[data-test="page-help-section"]').filter({ hasText: '页面上有什么' })
  await expect(inventory.locator('[data-test="page-help-section-body"]')).toBeHidden()
  await inventory.locator('.page-help-section-head').click()
  await expect(inventory.locator('[data-test="page-help-step-title"]').filter({ hasText: '报警洪水横幅' })).toBeVisible()

  const howTo = drawer.locator('[data-test="page-help-section"]').filter({ hasText: '怎么用' })
  await drawer.locator('[data-test="page-help-jump"]').filter({ hasText: '怎么用' }).click()
  await expect(howTo.locator('[data-test="page-help-section-body"]')).toBeVisible()
  await expect(howTo).toContainText('交接班或巡检时先看更新条')
  const fold = drawer.locator('[data-test="page-help-fold"]')
  await expect(fold).toHaveText('全部展开')
  await fold.click()
  await expect(inventory.locator('[data-test="page-help-section-body"]')).toBeVisible()
  await expect(fold).toHaveText('全部收起')
  await fold.click()
  await expect(howTo.locator('[data-test="page-help-section-body"]')).toBeHidden()

  await input.fill('不存在的词')
  await expect(drawer.locator('[data-test="page-help-search-empty"]')).toBeVisible()
})
