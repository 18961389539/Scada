import { test, expect, type APIRequestContext, type Locator, type Page } from './fixtures'

// hover 后把鼠标移开：tooltip 会浮在元素上方，遮住后续要点的按钮，导致 click 一直等不到可点状态
async function hoverAndCheck(page: Page, target: Locator, text: string): Promise<void> {
  await target.hover()
  await expect(page.getByRole('tooltip').filter({ hasText: text })).toBeVisible({ timeout: 10000 })
  await page.mouse.move(800, 950)
}

// 顶栏易用性：健康/声音/报警的悬停说明、声音开关持久化、连接正常时不显示断线提示。
test('topbar usability: health/sound/alarm tooltips and sound persistence', async ({ page }) => {
  test.setTimeout(90000)
  await page.goto('/login')
  await page.getByPlaceholder(/用户名/).fill('admin')
  await page.getByPlaceholder(/密码/).fill('admin123')
  await page.keyboard.press('Enter')
  await expect(page).toHaveURL(/overview/)

  // 健康标签补充信息：设备在线数 / 积压 / 磁盘
  const healthTag = page.locator('[data-test="health-tag"]')
  await expect(healthTag).toBeVisible({ timeout: 15000 })
  await hoverAndCheck(page, healthTag, '在线')

  // 声音开关有说明文字
  const sound = page.locator('[data-test="sound-switch"]')
  await hoverAndCheck(page, sound, '浏览器报警提示音')

  // 关闭声音后本机记忆生效
  await sound.click()
  await expect.poll(() => page.evaluate(() => localStorage.getItem('scada_sound'))).toBe('off')
  await page.reload()
  await expect(page.locator('[data-test="sound-switch"]')).toBeVisible({ timeout: 15000 })
  await expect(page.locator('[data-test="sound-switch"]')).not.toHaveClass(/is-checked/)

  // 恢复开启
  await page.locator('[data-test="sound-switch"]').click()
  await expect.poll(() => page.evaluate(() => localStorage.getItem('scada_sound'))).toBe('on')
  await expect(page.locator('[data-test="sound-switch"]')).toHaveClass(/is-checked/)

  // 报警铃铛说明未确认条数
  await hoverAndCheck(page, page.locator('[data-test="alarm-bell"]'), '未确认报警')

  // 连接正常时不显示断线提示（异常态才占位）
  await expect(page.locator('[data-test="hub-state"]')).toHaveCount(0)
})

async function signInForToolbar(page: Page, request: APIRequestContext, locale = 'zh-CN'): Promise<void> {
  const response = await request.post('/api/auth/login', { data: { userName: 'admin', password: 'admin123' } })
  expect(response.ok()).toBeTruthy()
  const session = await response.json()
  await page.addInitScript(({ token, user, locale }) => {
    localStorage.setItem('scada_token', token)
    localStorage.setItem('scada_user', JSON.stringify(user))
    localStorage.setItem('scada_locale', locale)
    localStorage.setItem('scada_sound', 'off')
  }, { token: session.token, user: session.user, locale })
  await page.route('**/api/auth/site-info', route => route.fulfill({ json: { siteName: 'SCADA Customer Demo - Utilities and Production', demoMode: true } }))
}

async function expectToolbarFits(page: Page, width: number): Promise<void> {
  await expect.poll(() => page.locator('.header').evaluate((header, width) => {
    const title = header.querySelector<HTMLElement>('.page-title')!
    const left = header.querySelector<HTMLElement>('.header-left')!.getBoundingClientRect()
    const right = header.querySelector<HTMLElement>('.header-right')!.getBoundingClientRect()
    const account = header.querySelector<HTMLElement>('[data-test="account-menu"]')!.getBoundingClientRect()
    const main = document.querySelector<HTMLElement>('.main')!
    const controls = header.querySelectorAll<HTMLElement>('.nav-toggle, .page-heading, [data-test="nav-search"], [data-test="sound-switch"], [data-test="alarm-bell"], [data-test="account-menu"]')
    return {
      titleFits: title.clientWidth >= 60 && title.scrollWidth <= title.clientWidth + 1,
      controlsFit: controls.length === 6 && [...controls].every(control => {
        const box = control.getBoundingClientRect()
        return box.width > 0 && box.left >= 0 && box.right <= width
      }),
      noOverlap: left.right <= right.left,
      noOverflow: header.scrollWidth <= header.clientWidth + 1 && document.documentElement.scrollWidth <= width,
      badgesFit: [...header.querySelectorAll<HTMLElement>('.el-badge__content')].every(badge => {
        const box = badge.getBoundingClientRect()
        return !box.width || (box.top >= 0 && box.right <= account.left - 2)
      }),
      heightMatches: Math.abs(header.getBoundingClientRect().height - parseFloat(getComputedStyle(main).getPropertyValue('--scada-header-height'))) < 1
    }
  }, width)).toEqual({ titleFits: true, controlsFit: true, noOverlap: true, noOverflow: true, badgesFit: true, heightMatches: true })
}

for (const width of [320, 390]) {
  test(`mobile topbar at ${width}px keeps its title and controls usable with connection and standby warnings`, async ({ page, request }) => {
    await page.setViewportSize({ width, height: 800 })
    await signInForToolbar(page, request)
    const primaryUrl = `http://primary.invalid/${'long-failover-address/'.repeat(8)}`
    await page.route('**/api/health', async route => {
      const response = await route.fetch()
      const health = await response.json()
      await route.fulfill({ json: { ...health, ha: { ...health.ha, role: 'Standby', primaryUrl } } })
    })
    await page.route('**/api/alarms/summary', async route => {
      const response = await route.fetch()
      await route.fulfill({ json: { ...await response.json(), unack: 137 } })
    })
    await page.route('**/hubs/realtime/**', route => route.fulfill({ status: 503, body: 'Connection unavailable in navigation test' }))
    await page.goto('/screens')
    const title = page.getByRole('heading', { name: '工艺画面', exact: true })
    await expect(title).toBeVisible()
    await expect(page.locator('[data-test="hub-state"]')).toBeVisible()
    await expect(page.locator('[data-test="standby-banner"]')).toContainText('备机')
    await expect(page.locator('[data-test="standby-banner"] a')).toHaveAttribute('href', primaryUrl)
    await expect(page.locator('.header .el-badge__content')).toHaveText('99+')
    await expectToolbarFits(page, width)
    const status = await page.locator('[data-test="header-status"]').boundingBox()
    const heading = await title.boundingBox()
    expect(status!.y).toBeGreaterThan(heading!.y + heading!.height)

    await page.locator('[data-test="sound-switch"]').click()
    await expect(page.getByRole('switch', { name: '浏览器报警提示音' })).toBeChecked()
    await page.locator('[data-test="account-menu"]').click()
    await expect(page.locator('[data-test="theme-toggle"]')).toBeVisible()
    await page.keyboard.press('Escape')
    await page.getByRole('button', { name: '搜索页面', exact: true }).click()
    await expect(page.locator('[data-test="command-palette"]')).toBeVisible()
    await page.keyboard.press('Escape')
    await expectToolbarFits(page, width)
  })
}

test('desktop toolbar and page search remain readable with long English site names at laptop widths', async ({ page, request }) => {
  await signInForToolbar(page, request, 'en-US')
  await page.goto('/screens')
  for (const width of [1000, 1366]) {
    await page.setViewportSize({ width, height: 800 })
    await expect(page.locator('[data-test="sidebar-toggle"]')).toBeVisible()
    await expect(page.getByRole('button', { name: 'Search pages', exact: true })).toBeVisible()
    await expectToolbarFits(page, width)
  }
  await page.getByRole('button', { name: 'Search pages', exact: true }).click()
  await expect(page.locator('[data-test="command-palette"]')).toBeVisible()
})
