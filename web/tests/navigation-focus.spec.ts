import { test, expect, type APIRequestContext, type Page } from './fixtures'

async function signIn(page: Page, request: APIRequestContext): Promise<void> {
  const response = await request.post('/api/auth/login', { data: { userName: 'admin', password: 'admin123' } })
  expect(response.ok()).toBeTruthy()
  const session = await response.json()
  await page.addInitScript((data) => {
    localStorage.setItem('scada_token', data.token)
    localStorage.setItem('scada_user', JSON.stringify(data.user))
  }, session)
}

test('skip link opens the main region and only path changes move focus to the page title', async ({ page, request }) => {
  await signIn(page, request)
  await page.goto('/alarms')
  await expect(page.getByRole('heading', { level: 1 })).toHaveText('报警')

  // 首次 Tab 的第一落点必须是“跳到主内容”，而不是侧栏或顶栏的几十个控件
  await page.keyboard.press('Tab')
  const skip = page.locator('[data-test="skip-to-content"]')
  await expect(skip).toBeFocused()
  await expect(skip).toBeInViewport()
  await page.keyboard.press('Enter')
  await expect(page.locator('#main-content')).toBeFocused()

  // 应用内切页：焦点落到新页面标题，读屏与键盘用户从标题继续，而不是回到 body
  await page.getByRole('menuitem', { name: '趋势曲线', exact: true }).click()
  await expect(page.getByRole('heading', { level: 1 })).toHaveText('趋势曲线')
  await expect(page.locator('.page-title')).toBeFocused()
  expect(await page.evaluate(() => document.getElementById('main-content')!.scrollTop)).toBe(0)

  // 仅查询参数变化（筛选条件）不移动焦点：否则刚点开的输入框会立刻失焦
  await page.getByRole('menuitem', { name: '报警', exact: true }).click()
  const keyword = page.locator('[data-test="alarm-keyword"]')
  await keyword.fill('泵')
  await expect(page).toHaveURL(/keyword=/)
  await expect(keyword).toBeFocused()
  await expect(page.locator('.page-title')).not.toBeFocused()
})
