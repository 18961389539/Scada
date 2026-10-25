import { test, expect } from '@playwright/test'

// 轮询与用户操作必须分开：
// 1) 10 秒一次的后台刷新不能把「刷新」按钮点亮（之前每 10 秒自己转一次，像有人在点）；
// 2) 后台失败不能每 10 秒弹一次 toast（一次断网就会连成一片），应就地提示并保留上次数据。
test('background polling does not spin the refresh button or spam errors', async ({ page }) => {
  test.setTimeout(60000)
  await page.goto('/login')
  await page.getByPlaceholder(/用户名/).fill('admin')
  await page.getByPlaceholder(/密码/).fill('admin123')
  await page.keyboard.press('Enter')
  await expect(page).toHaveURL(/overview/)

  // 让健康检查持续失败，观察后台轮询如何处理
  await page.route('**/api/health*', (route) => route.fulfill({ status: 500, contentType: 'application/json', body: '{"message":"模拟故障"}' }))

  await page.goto('/system')
  await page.getByRole('tab', { name: '系统状态', exact: true }).click()

  const error = page.locator('[data-test="health-error"]')
  // 首次加载是用户可见的操作（会 toast，卡片为空）；10 秒后的后台轮询才会写入就地提示
  await expect(error).toBeVisible({ timeout: 20000 })
  await expect(error).toContainText('模拟故障')

  // 后台轮询期间按钮不应处于 loading 态
  const refresh = page.getByRole('tabpanel').getByRole('button', { name: '刷新', exact: true })
  await expect(refresh).not.toHaveClass(/is-loading/)

  // 恢复后提示自动消失（下一次后台刷新成功）
  await page.unroute('**/api/health*')
  await expect(error).toHaveCount(0, { timeout: 20000 })
})
