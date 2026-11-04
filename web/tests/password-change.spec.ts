import { test, expect } from './fixtures'

// 改密页与接口链路：路由守门、表单校验、改完换令牌并放行。
// 服务端「未改密一律 428」的拦截由 PasswordChangeTests 覆盖；这里验证浏览器侧真的能走通。
test('core: change-password page gates the session and unlocks it', async ({ page, request }) => {
  const login = await request.post('/api/auth/login', { data: { userName: 'admin', password: 'admin123' } })
  expect(login.ok(), await login.text()).toBe(true)
  const session = await login.json()

  // 模拟「缓存的用户信息仍标记为需改密」的现场：令牌有效，但守卫应把人送到改密页。
  await page.addInitScript(({ token, user }) => {
    localStorage.setItem('scada_token', token)
    localStorage.setItem('scada_user', JSON.stringify({ ...user, mustChangePassword: true }))
  }, session)

  await page.goto('/overview')
  await expect(page).toHaveURL(/change-password/)
  await expect(page.locator('input[data-test="change-current"]')).toBeVisible()

  // ElInput 把 $attrs 透传给内部 input，所以选择器写成 input[data-test=...]。
  // ElInput 把 $attrs 透传给内部 input，所以选择器写成 input[data-test=...]。
  // 两次输入不一致时不提交。
  await page.locator('input[data-test="change-current"]').fill('admin123')
  await page.locator('input[data-test="change-new"]').fill('changed-by-test-1')
  await page.locator('input[data-test="change-confirm"]').fill('changed-by-test-2')
  await page.locator('[data-test="change-submit"]').click()
  await expect(page.locator('.el-form-item__error').first()).toBeVisible()
  await expect(page).toHaveURL(/change-password/)

  // 改成一致后应当离开改密页。
  await page.locator('input[data-test="change-confirm"]').fill('changed-by-test-1')
  await page.locator('[data-test="change-submit"]').click()
  await expect(page).not.toHaveURL(/change-password/, { timeout: 15000 })

  // 旧口令立即失效，新口令可以登录：证明服务端确实换了哈希而不是只改了前端状态。
  expect((await request.post('/api/auth/login', { data: { userName: 'admin', password: 'admin123' } })).status()).toBe(401)
  const relogin = await request.post('/api/auth/login', { data: { userName: 'admin', password: 'changed-by-test-1' } })
  expect(relogin.ok(), await relogin.text()).toBe(true)
  expect((await relogin.json()).user.mustChangePassword).toBe(false)
})
