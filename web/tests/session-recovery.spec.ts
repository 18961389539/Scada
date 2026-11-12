import { test, expect, type APIRequestContext, type Page } from './fixtures'

// 会话恢复：401 / 428 都会整页跳转，跳转前必须把当前地址（含查询参数）存进 redirect，
// 重新登录或改密之后回到原页面，而不是每次都退回默认首页、让用户重填筛选条件。

async function seedSession(page: Page, request: APIRequestContext): Promise<void> {
  const response = await request.post('/api/auth/login', { data: { userName: 'admin', password: 'admin123' } })
  expect(response.ok(), await response.text()).toBeTruthy()
  const session = await response.json()
  await page.addInitScript(({ token, user }) => {
    localStorage.setItem('scada_token', token)
    localStorage.setItem('scada_user', JSON.stringify(user))
  }, session)
}

test('an expired session returns the user to the filtered page after signing in again', async ({ page, request }) => {
  test.setTimeout(90000)
  await seedSession(page, request)
  // 只让告警列表分页接口过期：模拟「页面已经打开、令牌恰好失效」，其余请求照常返回。
  let expire = true
  await page.route(url => url.pathname === '/api/alarms/page', route => expire
    ? route.fulfill({ status: 401, json: { message: 'expired' } })
    : route.continue())

  await page.goto('/alarms?state=unack&level=Critical').catch(() => undefined)

  await expect(page).toHaveURL(/\/login\?/, { timeout: 30000 })
  const login = new URL(page.url())
  expect(login.searchParams.get('redirect')).toBe('/alarms?state=unack&level=Critical')
  // 被动登出要说明原因，否则用户只看到一张空白表单
  expect(login.searchParams.get('expired')).toBe('1')
  await expect(page.locator('[data-test="login-session-expired"]')).toBeVisible()

  expire = false
  await page.getByPlaceholder(/用户名/).fill('admin')
  await page.getByPlaceholder(/密码/).fill('admin123')
  await page.keyboard.press('Enter')

  await expect.poll(() => new URL(page.url()).pathname, { timeout: 30000 }).toBe('/alarms')
  const restored = new URL(page.url())
  expect(restored.searchParams.get('state')).toBe('unack')
  expect(restored.searchParams.get('level')).toBe('Critical')
})

test('a rejected sign-in never makes the login page its own redirect target', async ({ page }) => {
  test.setTimeout(90000)
  await page.goto('/login?redirect=/overview')
  await page.getByPlaceholder(/用户名/).fill('admin')
  await page.getByPlaceholder(/密码/).fill('definitely-not-the-password')
  await page.keyboard.press('Enter')

  await expect(page.locator('[data-test="login-error"]')).toBeVisible({ timeout: 30000 })
  const url = new URL(page.url())
  expect(url.pathname).toBe('/login')
  // 只保留最初的 redirect，没有被叠成 login?redirect=login?redirect=…
  expect(url.searchParams.get('redirect')).toBe('/overview')
})

test('the forced password rotation keeps the page the user was opening', async ({ page, request }) => {
  test.setTimeout(90000)
  await seedSession(page, request)
  // 除认证接口外一律 428：模拟「缓存里的用户信息还没带改密标记」的现场。
  await page.route(url => url.pathname.startsWith('/api/') && !url.pathname.startsWith('/api/auth/'), route =>
    route.fulfill({ status: 428, json: { message: 'must change password' } }))

  await page.goto('/realtime?quality=Bad').catch(() => undefined)

  await expect(page).toHaveURL(/\/change-password\?/, { timeout: 30000 })
  expect(new URL(page.url()).searchParams.get('redirect')).toBe('/realtime?quality=Bad')
})
