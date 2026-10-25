import { test, expect, type APIRequestContext } from '@playwright/test'

async function login(request: APIRequestContext) {
  const r = await request.post('/api/auth/login', { data: { userName: 'admin', password: 'admin123' } })
  expect(r.ok(), await r.text()).toBeTruthy()
  return (await r.json()) as { token: string }
}

// 网关管理易用性：最后刷新显示相对时间、危险操作在下拉里分隔标红、详情设备清单可筛选。
test('gateways usability: relative last-read, danger separation and device filter', async ({ page, request }) => {
  test.setTimeout(90000)
  const session = await login(request)
  const h = { Authorization: `Bearer ${session.token}` }
  const suffix = `${Date.now()}`.slice(-6)
  const created = await request.post('/api/gateways', { headers: h, data: { name: `UX-GW-${suffix}` } })
  expect(created.ok(), await created.text()).toBeTruthy()

  await page.goto('/login')
  await page.getByPlaceholder(/用户名/).fill('admin')
  await page.getByPlaceholder(/密码/).fill('admin123')
  await page.keyboard.press('Enter')
  await expect(page).toHaveURL(/overview/)
  await page.goto('/config/gateways')

  // 相对时间：绝对时间保留用于对照日志，括号里的「N 秒前」用于判断新鲜度
  await expect(page.locator('[data-test="last-read"]')).toContainText('秒前', { timeout: 15000 })

  const row = page.getByRole('row').filter({ hasText: `UX-GW-${suffix}` })
  await expect(row).toBeVisible()

  // 危险操作与启停/轮换分隔并标红
  // 每行都渲染了自己的 dropdown（teleport 到 body 但隐藏），必须限定当前可见的那个
  await row.getByRole('button', { name: '更多网关操作' }).click()
  await expect(page.locator('.danger-item:visible')).toHaveText('释放会话')
  await page.keyboard.press('Escape')

  // 详情弹窗的设备清单带就地筛选
  await row.getByRole('button', { name: '详情', exact: true }).click()
  await expect(page.locator('[data-test="gateway-device-filter"]')).toBeVisible({ timeout: 15000 })
  await expect(page.locator('[data-test="gateway-devices"]')).toBeVisible()
})
