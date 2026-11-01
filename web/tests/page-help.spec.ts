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

  await drawer.locator('[data-test="page-help-search"]').click()
  const input = drawer.locator('input[data-test="page-help-search-input"]')
  await expect(input).toBeVisible()
  await input.fill('报警洪水')
  await expect(drawer.locator('[data-test="page-help-search-count"]')).toHaveText('匹配 1 条')
  await expect(drawer.locator('mark').first()).toHaveText('报警洪水')
  await expect(drawer).not.toContainText('配置引导')

  await input.fill('')
  await expect(drawer).toContainText('配置引导')

  await input.fill('不存在的词')
  await expect(drawer.locator('[data-test="page-help-search-empty"]')).toBeVisible()
})
