import { test, expect } from '@playwright/test'

// 总览易用性：统计卡片从纯展示变为可点击跳转，且支持键盘操作；无权页面保持纯展示。
test('overview usability: stat cards navigate to their pages', async ({ page }) => {
  test.setTimeout(90000)
  await page.goto('/login')
  await page.getByPlaceholder(/用户名/).fill('admin')
  await page.getByPlaceholder(/密码/).fill('admin123')
  await page.keyboard.press('Enter')
  await expect(page).toHaveURL(/overview/)

  // admin 可见的 4 张卡片（设备/报警/测点/存储）可点击，运行时长卡片保持纯展示
  await expect(page.locator('.stat-card.clickable')).toHaveCount(4)
  await expect(page.locator('[data-test="stat-devices"]')).toHaveClass(/clickable/)

  // 设备在线 → 实时监视
  await page.locator('[data-test="stat-devices"]').click()
  await expect(page).toHaveURL(/realtime/)

  // 未确认报警 → 报警页
  await page.goto('/overview')
  await page.locator('[data-test="stat-alarms"]').click()
  await expect(page).toHaveURL(/alarms/)

  // 历史存储 → 系统管理
  await page.goto('/overview')
  await page.locator('[data-test="stat-storage"]').click()
  await expect(page).toHaveURL(/system/)

  // 键盘可达：聚焦卡片后回车同样跳转
  await page.goto('/overview')
  await expect(page.locator('[data-test="stat-tags"]')).toHaveAttribute('tabindex', '0')
  await page.locator('[data-test="stat-tags"]').focus()
  await page.keyboard.press('Enter')
  await expect(page).toHaveURL(/realtime/)
})

// 数据口径回归：未确认报警卡片原先用「列表前 10 条」自己数，超过 10 条就少报（最多显示 10）。
// 现在必须用服务端 /alarms/summary 的全量统计。
test('overview usability: unacknowledged count comes from server summary, not the loaded slice', async ({ page }) => {
  test.setTimeout(60000)
  await page.route('**/api/alarms/summary', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ unack: 42, acked: 3, rtnUnack: 1, shelved: 0, suppressed: 0, chattering: 0, shelvedRules: 0, outOfServiceRules: 0 })
    })
  )
  // 列表只给空数组：模拟「页面拿到的活动报警列表远少于全量统计」
  await page.route(
    (url) => url.pathname === '/api/alarms' && url.searchParams.get('activeOnly') === 'true',
    (route) => route.fulfill({ status: 200, contentType: 'application/json', body: '[]' })
  )

  await page.goto('/login')
  await page.getByPlaceholder(/用户名/).fill('admin')
  await page.getByPlaceholder(/密码/).fill('admin123')
  await page.keyboard.press('Enter')
  await expect(page).toHaveURL(/overview/)

  const card = page.locator('[data-test="stat-alarms"]')
  await expect(card.locator('.value')).toHaveText('42', { timeout: 15000 })
  // 活动报警总数 = 未确认 + 已确认 + 恢复未确认
  await expect(card).toContainText('当前活动报警 46 条')
})
