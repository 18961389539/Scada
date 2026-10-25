import { test, expect, type Locator, type Page } from '@playwright/test'

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
