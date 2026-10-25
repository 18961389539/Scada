import { test, expect } from '@playwright/test'

// 数据语义回归：/health 的 uptimeSeconds 此前取的是 Environment.TickCount64（**系统开机时长**），
// 界面却标成「运行时长 / 启动于」——服务重启了也显示「已运行 3 天」。现在必须是进程运行时长，
// 主机开机时长单独给出，两者并列才能区分「服务刚重启」和「整机刚重启」。
test('health separates process uptime from host uptime', async ({ page, request }) => {
  test.setTimeout(60000)
  const response = await request.get('/api/health')
  expect(response.ok(), await response.text()).toBeTruthy()
  const body = await response.json()

  expect(typeof body.uptimeSeconds, 'uptimeSeconds 必须是数字').toBe('number')
  expect(typeof body.hostUptimeSeconds, 'hostUptimeSeconds 必须单独给出').toBe('number')
  // 进程不可能比整机活得更久
  expect(body.hostUptimeSeconds).toBeGreaterThanOrEqual(body.uptimeSeconds)
  // 测试服务是本轮刚拉起的：若仍用系统开机时长，宿主机连续开机较久时这里会失败
  expect(body.uptimeSeconds).toBeLessThan(1800)

  await page.goto('/login')
  await page.getByPlaceholder(/用户名/).fill('admin')
  await page.getByPlaceholder(/密码/).fill('admin123')
  await page.keyboard.press('Enter')
  await expect(page).toHaveURL(/overview/)
  await page.goto('/system')
  await page.getByRole('tab', { name: '系统状态', exact: true }).click()

  const card = page.locator('.stat-card').filter({ hasText: '运行时长' })
  await expect(card).toContainText('启动于')
  await expect(card).toContainText('主机已开机')
})
