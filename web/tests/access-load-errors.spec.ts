import { test, expect, type Page } from './fixtures'

async function signIn(page: Page): Promise<void> {
  await page.goto('/login')
  await page.getByPlaceholder(/用户名/).fill('admin')
  await page.getByPlaceholder(/密码/).fill('admin123')
  await page.keyboard.press('Enter')
  await expect(page).toHaveURL(/overview/)
}

test('area, role, and grant catalogs keep a load failure on the page', async ({ page }) => {
  await signIn(page)
  await page.route((url) => url.pathname === '/api/areas' || url.pathname === '/api/roles', (route) => {
    if (route.request().method() !== 'GET') return route.continue()
    const message = route.request().url().includes('/api/roles') ? '角色列表不可用' : '区域列表不可用'
    return route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message }) })
  })
  await page.goto('/system')
  await expect(page.locator('[data-test="user-assign"]').first()).toBeVisible()
  await page.locator('[data-test="user-assign"]').first().click()
  const dialog = page.getByRole('dialog')
  await expect(page.locator('[data-test="assignment-load-error"]')).toBeVisible()
  await expect(dialog.getByText('暂无数据', { exact: true })).toHaveCount(0)
  await dialog.getByRole('button', { name: '取消' }).click()
  await expect(dialog).not.toBeVisible()

  await page.getByRole('tab', { name: '区域与角色' }).click()
  const areas = page.locator('[data-test="area-table"]')
  await expect(page.locator('[data-test="access-areas-error"]')).toContainText('区域列表不可用')
  await expect(areas).toContainText('区域列表不可用')
  await expect(areas.getByText('默认区域', { exact: true })).toHaveCount(0)
  await expect(areas.getByText('暂无数据', { exact: true })).toHaveCount(0)
  await page.getByRole('tab', { name: '角色', exact: true }).click()
  const roles = page.locator('[data-test="role-table"]')
  await expect(page.locator('[data-test="access-roles-error"]')).toContainText('角色列表不可用')
  await expect(roles).toContainText('角色列表不可用')
  await expect(roles.getByText('暂无数据', { exact: true })).toHaveCount(0)
})
